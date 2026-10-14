-- =============================================================================
-- 021 — RECUPERAR LA EMPRESA (fase 4a, camino A)
--
-- Lienzo «Fase 4 — Traspasos», pantallas 1 a 4. Cuando un contador quita una
-- empresa de su cartera, la empresa queda «en pausa» (018). Desde aquí su
-- titular la puede recuperar:
--
--   · El correo de la pausa trae «Seguir yo mismo»: un enlace de un uso,
--     7 días, que va SOLO al correo del titular.
--   · «¿Tu empresa está en pausa? Recupérala» en el ingreso pide uno nuevo
--     escribiendo el RUC. Responde lo mismo exista o no.
--   · A los 30, 60 y 90 días de la pausa, un recordatorio con enlace nuevo.
--
-- Con el enlace, el titular:
--   · crea su Cuenta Negocio (el correo es el del titular; la contraseña y el
--     segundo factor, en la misma pantalla de la invitación), o
--   · si ese correo ya tiene cuenta, entra y la empresa se agrega a ella.
--
-- La empresa PASA ENTERA a la cuenta del titular: comprobantes, clientes,
-- series y sus usuarios de empresa (la cajera). Sale de la cartera del
-- contador para siempre. Queda «por revisar» (series y clave SOL) hasta que
-- el titular lo marque.
--
-- Todo por funciones SECURITY DEFINER: quien usa el enlace no tiene sesión,
-- o tiene una de otra cuenta. La tabla de enlaces no la lee nadie más.
--
-- No se borra nada.
-- =============================================================================

BEGIN;

-- =============================================================================
-- 1. LOS ENLACES
-- =============================================================================

CREATE TABLE enlaces_recuperacion (
    id          uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id  uuid        NOT NULL REFERENCES empresas (id),
    token_hash  bytea       NOT NULL,

    -- A dónde se mandó: el correo del titular de ese momento.
    correo      text        NOT NULL,

    -- pausa: el correo de la pausa · pedido: «Recupérala» · recordatorio.
    motivo      text        NOT NULL CHECK (motivo IN ('pausa', 'pedido', 'recordatorio')),

    creado_en   timestamptz NOT NULL DEFAULT now(),
    expira_en   timestamptz NOT NULL,
    usado_en    timestamptz,
    usado_por   uuid,

    -- Un enlace nuevo anula los anteriores de la misma empresa.
    anulado_en  timestamptz,

    -- Los de siempre, para la bitácora.
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1
);

CREATE UNIQUE INDEX ux_enlaces_recuperacion ON enlaces_recuperacion (token_hash);
CREATE INDEX ix_enlaces_recuperacion_empresa ON enlaces_recuperacion (empresa_id, creado_en DESC);

-- Sin políticas: plataforma_app no ve ni una fila. Solo las funciones.
ALTER TABLE enlaces_recuperacion ENABLE ROW LEVEL SECURITY;
ALTER TABLE enlaces_recuperacion FORCE ROW LEVEL SECURITY;

-- En la bitácora, sin el hash del enlace.
SELECT vigilar_tabla('enlaces_recuperacion', ARRAY['token_hash']);


-- =============================================================================
-- 2. LA EMPRESA RECUERDA LO QUE PASÓ
-- =============================================================================

ALTER TABLE empresas ADD COLUMN recordatorios_enviados integer NOT NULL DEFAULT 0;
ALTER TABLE empresas ADD COLUMN recuperada_en          timestamptz;
ALTER TABLE empresas ADD COLUMN revision_pendiente     boolean NOT NULL DEFAULT false;

COMMENT ON COLUMN empresas.recordatorios_enviados IS
    'Recordatorios de recuperación enviados desde la última pausa (máximo 3: a los 30, 60 y 90 días).';
COMMENT ON COLUMN empresas.revision_pendiente IS
    'Recién recuperada: el titular todavía no confirmó que revisó sus series y su clave SOL.';


-- =============================================================================
-- 3. CREAR UN ENLACE (interna)
--
-- Anula los anteriores sin usar y guarda el nuevo. Devuelve a quién escribir,
-- o nada si la empresa no está en pausa o no tiene correo del titular.
-- =============================================================================

CREATE OR REPLACE FUNCTION nuevo_enlace_recuperacion(
    p_empresa uuid, p_token_hash bytea, p_expira timestamptz, p_motivo text)
RETURNS TABLE (correo text, nombre text, ruc text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_e record;
BEGIN
    SELECT e.id, e.nombre, e.razon_social, e.ruc, e.correo_titular INTO v_e
      FROM empresas e
     WHERE e.id = p_empresa AND e.activo AND e.pausada_en IS NOT NULL
       AND e.correo_titular IS NOT NULL AND btrim(e.correo_titular) <> '';

    IF NOT FOUND THEN RETURN; END IF;

    UPDATE enlaces_recuperacion SET anulado_en = now()
     WHERE empresa_id = p_empresa AND usado_en IS NULL AND anulado_en IS NULL;

    INSERT INTO enlaces_recuperacion (empresa_id, token_hash, correo, motivo, expira_en)
    VALUES (p_empresa, p_token_hash, btrim(v_e.correo_titular), p_motivo, p_expira);

    RETURN QUERY SELECT btrim(v_e.correo_titular), COALESCE(v_e.razon_social, v_e.nombre), v_e.ruc;
END $$;

REVOKE ALL ON FUNCTION nuevo_enlace_recuperacion(uuid, bytea, timestamptz, text) FROM PUBLIC;


-- --- El de la pausa: solo quien acaba de pausarla, y una vez --------------

CREATE OR REPLACE FUNCTION enlace_tras_pausa(p_empresa uuid, p_token_hash bytea, p_expira timestamptz)
RETURNS TABLE (correo text, nombre text, ruc text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM empresas e
                    WHERE e.id = p_empresa
                      AND e.pausada_por = ctx_usuario()
                      AND e.pausada_en > now() - interval '10 minutes')
       OR EXISTS (SELECT 1 FROM enlaces_recuperacion r
                   WHERE r.empresa_id = p_empresa AND r.motivo = 'pausa'
                     AND r.creado_en > now() - interval '10 minutes')
    THEN
        RETURN;
    END IF;

    -- Una pausa nueva vuelve a contar los recordatorios desde cero.
    UPDATE empresas SET recordatorios_enviados = 0 WHERE id = p_empresa;

    RETURN QUERY SELECT * FROM nuevo_enlace_recuperacion(p_empresa, p_token_hash, p_expira, 'pausa');
END $$;


-- --- «Recupérala»: sin sesión, por RUC --------------------------------------
--
-- Topes: 10 pedidos por hora desde la misma red, y un enlace por empresa cada
-- 10 minutos. Si no toca enviar, no devuelve nada; la pantalla responde lo
-- mismo en todos los casos.

CREATE OR REPLACE FUNCTION pedir_recuperacion(
    p_ruc text, p_token_hash bytea, p_expira timestamptz, p_ip inet)
RETURNS TABLE (correo text, nombre text, ruc text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_empresa uuid;
BEGIN
    IF p_ip IS NOT NULL AND (
        SELECT count(*) FROM registro_intentos
         WHERE ip = p_ip AND resultado LIKE 'recuperar%'
           AND ocurrido_en > now() - interval '1 hour') >= 10
    THEN
        INSERT INTO registro_intentos (ip, correo, resultado) VALUES (p_ip, NULL, 'recuperar_demasiados');
        RETURN;
    END IF;

    INSERT INTO registro_intentos (ip, correo, resultado) VALUES (p_ip, NULL, 'recuperar_pedido');

    SELECT e.id INTO v_empresa
      FROM empresas e
     WHERE e.ruc = btrim(p_ruc) AND e.activo AND e.pausada_en IS NOT NULL;

    IF v_empresa IS NULL THEN RETURN; END IF;

    IF EXISTS (SELECT 1 FROM enlaces_recuperacion r
                WHERE r.empresa_id = v_empresa AND r.creado_en > now() - interval '10 minutes') THEN
        RETURN;
    END IF;

    RETURN QUERY SELECT * FROM nuevo_enlace_recuperacion(v_empresa, p_token_hash, p_expira, 'pedido');
END $$;


-- --- Recordatorios: a los 30, 60 y 90 días ----------------------------------

CREATE OR REPLACE FUNCTION recordatorios_pendientes()
RETURNS SETOF uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT e.id
      FROM empresas e
     WHERE e.activo AND e.pausada_en IS NOT NULL
       AND e.correo_titular IS NOT NULL
       AND e.recordatorios_enviados < 3
       AND now() >= e.pausada_en + (e.recordatorios_enviados + 1) * interval '30 days';
$$;

CREATE OR REPLACE FUNCTION enlace_recordatorio(p_empresa uuid, p_token_hash bytea, p_expira timestamptz)
RETURNS TABLE (correo text, nombre text, ruc text, numero integer)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_n integer;
BEGIN
    -- Se vuelve a comprobar aquí: que toque de verdad.
    IF NOT EXISTS (SELECT 1 FROM recordatorios_pendientes() r WHERE r = p_empresa) THEN RETURN; END IF;

    UPDATE empresas SET recordatorios_enviados = recordatorios_enviados + 1
     WHERE id = p_empresa
    RETURNING recordatorios_enviados INTO v_n;

    RETURN QUERY SELECT x.correo, x.nombre, x.ruc, v_n
      FROM nuevo_enlace_recuperacion(p_empresa, p_token_hash, p_expira, 'recordatorio') x;
END $$;


-- =============================================================================
-- 4. LO QUE ENSEÑA LA PÁGINA DEL ENLACE
--
-- estado: ok · vencido · usado · no_existe · no_pausada
-- correo_con_cuenta: ese correo ya tiene usuario (entonces se entra y se
-- agrega, en vez de crear otra cuenta).
-- =============================================================================

CREATE OR REPLACE FUNCTION resolver_recuperacion(p_token_hash bytea)
RETURNS TABLE (
    estado            text,
    empresa_id        uuid,
    nombre            text,
    ruc               text,
    correo            text,
    pausada_en        timestamptz,
    motivo_pausa      text,
    quien             text,
    comprobantes      integer,
    clientes          integer,
    series            text[],
    expira_en         timestamptz,
    correo_con_cuenta boolean
)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_l enlaces_recuperacion%ROWTYPE;
    v_e record;
BEGIN
    SELECT * INTO v_l FROM enlaces_recuperacion WHERE token_hash = p_token_hash;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'no_existe'::text, NULL::uuid, NULL::text, NULL::text, NULL::text, NULL::timestamptz,
                            NULL::text, NULL::text, 0, 0, NULL::text[], NULL::timestamptz, false;
        RETURN;
    END IF;

    SELECT e.id, COALESCE(e.razon_social, e.nombre) AS nombre, e.ruc, e.pausada_en, e.motivo_pausa,
           (SELECT COALESCE(c.titular_nombre, u.nombre)
              FROM usuarios u LEFT JOIN cuentas c ON c.id = u.cuenta_id
             WHERE u.id = e.pausada_por) AS quien
      INTO v_e
      FROM empresas e WHERE e.id = v_l.empresa_id;

    RETURN QUERY SELECT
        CASE WHEN v_l.usado_en IS NOT NULL THEN 'usado'
             WHEN v_l.anulado_en IS NOT NULL OR v_l.expira_en <= now() THEN 'vencido'
             WHEN v_e.pausada_en IS NULL THEN 'no_pausada'
             ELSE 'ok' END,
        v_e.id, v_e.nombre, v_e.ruc, v_l.correo, v_e.pausada_en, v_e.motivo_pausa, v_e.quien,
        (SELECT count(*)::int FROM documentos d WHERE d.empresa_id = v_e.id AND d.estado <> 'borrador'),
        (SELECT count(*)::int FROM clientes c WHERE c.empresa_id = v_e.id AND c.activo),
        ARRAY(SELECT DISTINCT d.serie FROM documentos d
               WHERE d.empresa_id = v_e.id AND d.serie IS NOT NULL ORDER BY 1),
        v_l.expira_en,
        EXISTS (SELECT 1 FROM usuarios u WHERE lower(u.correo) = lower(v_l.correo));
END $$;


-- =============================================================================
-- 5. EL TRASPASO (interna)
--
-- La empresa pasa ENTERA a la cuenta nueva: sale de la pausa, la gente de la
-- cuenta anterior la deja de ver, sus usuarios de empresa (la cajera) se van
-- con ella, y queda «por revisar». Devuelve a quién avisar de la cuenta
-- anterior (su dueño).
-- =============================================================================

CREATE OR REPLACE FUNCTION traspasar_empresa(p_empresa uuid, p_cuenta uuid, p_usuario uuid, p_via text)
RETURNS TABLE (aviso_correo text, aviso_nombre text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_anterior uuid;
BEGIN
    SELECT cuenta_id INTO v_anterior FROM empresas WHERE id = p_empresa FOR UPDATE;

    -- La gente de la cuenta anterior: si la tenían abierta, a su bandeja; y
    -- se les quita el permiso de verla.
    UPDATE usuarios SET empresa_id = NULL
     WHERE empresa_id = p_empresa AND nivel_cuenta IS NOT NULL AND cuenta_id = v_anterior;
    UPDATE usuario_empresa SET activo = false WHERE empresa_id = p_empresa AND activo;

    -- Sus usuarios de empresa se van con ella.
    UPDATE usuarios SET cuenta_id = p_cuenta
     WHERE empresa_id = p_empresa AND nivel_cuenta IS NULL;

    UPDATE empresas
       SET cuenta_id = p_cuenta,
           pausada_en = NULL, motivo_pausa = NULL, pausada_por = NULL,
           recordatorios_enviados = 0,
           recuperada_en = now(),
           revision_pendiente = true
     WHERE id = p_empresa;

    -- Quien la recupera, la tiene abierta.
    UPDATE usuarios SET empresa_id = p_empresa WHERE id = p_usuario;

    UPDATE enlaces_recuperacion SET anulado_en = now()
     WHERE empresa_id = p_empresa AND usado_en IS NULL AND anulado_en IS NULL;

    PERFORM registrar_evento('otro'::text, true, p_usuario, p_empresa, NULL,
        jsonb_build_object('accion', 'empresa_recuperada', 'via', p_via,
                           'cuenta_anterior', v_anterior, 'cuenta_nueva', p_cuenta));

    RETURN QUERY
        SELECT u.correo, u.nombre FROM usuarios u
         WHERE u.cuenta_id = v_anterior AND u.nivel_cuenta = 'dueno' AND u.activo
         LIMIT 1;
END $$;

REVOKE ALL ON FUNCTION traspasar_empresa(uuid, uuid, uuid, text) FROM PUBLIC;


-- =============================================================================
-- 6. RECUPERAR CREANDO UNA CUENTA NUEVA (sin sesión)
--
-- resultado: ok · vencido · usado · no_existe · no_pausada · correo_con_cuenta
-- Crea la Cuenta Negocio, su dueño (sin contraseña: la elige en la pantalla
-- de la invitación, con el token que se devuelve) y le pasa la empresa.
-- Usar el enlace del correo ya prueba que el correo es suyo.
-- =============================================================================

CREATE OR REPLACE FUNCTION recuperar_con_cuenta_nueva(
    p_token_hash   bytea,
    p_nombre       text,
    p_inv_hash     bytea,
    p_inv_expira   timestamptz,
    p_ip           inet)
RETURNS TABLE (resultado text, usuario_id uuid, correo text, aviso_correo text, aviso_nombre text, empresa text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_l       enlaces_recuperacion%ROWTYPE;
    v_e       record;
    v_cuenta  uuid;
    v_usuario uuid;
    v_tipo    text;
    v_doc     text;
    v_aviso   record;
BEGIN
    SELECT * INTO v_l FROM enlaces_recuperacion WHERE token_hash = p_token_hash FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'no_existe'::text, NULL::uuid, NULL::text, NULL::text, NULL::text, NULL::text; RETURN;
    END IF;
    IF v_l.usado_en IS NOT NULL THEN
        RETURN QUERY SELECT 'usado'::text, NULL::uuid, NULL::text, NULL::text, NULL::text, NULL::text; RETURN;
    END IF;
    IF v_l.anulado_en IS NOT NULL OR v_l.expira_en <= now() THEN
        RETURN QUERY SELECT 'vencido'::text, NULL::uuid, NULL::text, NULL::text, NULL::text, NULL::text; RETURN;
    END IF;

    SELECT e.id, e.ruc, COALESCE(e.razon_social, e.nombre) AS nombre, e.pausada_en INTO v_e
      FROM empresas e WHERE e.id = v_l.empresa_id FOR UPDATE;

    IF v_e.pausada_en IS NULL THEN
        RETURN QUERY SELECT 'no_pausada'::text, NULL::uuid, NULL::text, NULL::text, NULL::text, NULL::text; RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM usuarios u WHERE lower(u.correo) = lower(v_l.correo)) THEN
        RETURN QUERY SELECT 'correo_con_cuenta'::text, NULL::uuid, NULL::text, NULL::text, NULL::text, NULL::text; RETURN;
    END IF;

    -- El titular de la cuenta: del RUC. 10… = persona natural (su DNI va
    -- dentro del RUC); 20… = persona jurídica.
    IF left(v_e.ruc, 2) = '10' THEN
        v_tipo := 'natural';  v_doc := substr(v_e.ruc, 3, 8);
    ELSE
        v_tipo := 'juridica'; v_doc := v_e.ruc;
    END IF;

    INSERT INTO cuentas (tipo, titular_tipo, titular_documento, titular_nombre, estado, origen,
                         terminos_aceptados_en)
    VALUES ('negocio', v_tipo, v_doc, v_e.nombre, 'pendiente', 'registro', now())
    RETURNING id INTO v_cuenta;

    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta)
    VALUES (v_cuenta, v_l.correo, btrim(p_nombre),
            'sin-clave$' || encode(gen_random_bytes(16), 'hex'), 'dueno')
    RETURNING id INTO v_usuario;

    PERFORM ajustar_roles_de_nivel(v_usuario);

    INSERT INTO tokens_un_uso (usuario_id, proposito, token_hash, expira_en)
    VALUES (v_usuario, 'invitacion', p_inv_hash, p_inv_expira);

    UPDATE enlaces_recuperacion SET usado_en = now(), usado_por = v_usuario WHERE id = v_l.id;

    SELECT * INTO v_aviso FROM traspasar_empresa(v_e.id, v_cuenta, v_usuario, 'cuenta_nueva');

    INSERT INTO registro_intentos (ip, correo, resultado) VALUES (p_ip, v_l.correo, 'recuperar_ok');

    RETURN QUERY SELECT 'ok'::text, v_usuario, v_l.correo, v_aviso.aviso_correo, v_aviso.aviso_nombre, v_e.nombre;
END $$;


-- =============================================================================
-- 7. RECUPERAR A LA CUENTA QUE YA TENGO (con sesión)
--
-- resultado: ok · vencido · usado · no_existe · no_pausada · otro_correo ·
--            sin_permiso (no es dueño) · convertir (Cuenta Negocio que ya
--            tiene otra empresa: primero hay que pasarla a Profesional)
-- =============================================================================

CREATE OR REPLACE FUNCTION recuperar_a_mi_cuenta(p_token_hash bytea)
RETURNS TABLE (resultado text, aviso_correo text, aviso_nombre text, empresa text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_l     enlaces_recuperacion%ROWTYPE;
    v_yo    record;
    v_e     record;
    v_aviso record;
BEGIN
    SELECT * INTO v_l FROM enlaces_recuperacion WHERE token_hash = p_token_hash FOR UPDATE;

    IF NOT FOUND THEN RETURN QUERY SELECT 'no_existe'::text, NULL::text, NULL::text, NULL::text; RETURN; END IF;
    IF v_l.usado_en IS NOT NULL THEN RETURN QUERY SELECT 'usado'::text, NULL::text, NULL::text, NULL::text; RETURN; END IF;
    IF v_l.anulado_en IS NOT NULL OR v_l.expira_en <= now() THEN
        RETURN QUERY SELECT 'vencido'::text, NULL::text, NULL::text, NULL::text; RETURN;
    END IF;

    SELECT u.id, u.correo, u.cuenta_id, u.nivel_cuenta, c.tipo INTO v_yo
      FROM usuarios u LEFT JOIN cuentas c ON c.id = u.cuenta_id
     WHERE u.id = ctx_usuario() AND u.activo;

    IF NOT FOUND OR lower(v_yo.correo) <> lower(v_l.correo) THEN
        RETURN QUERY SELECT 'otro_correo'::text, NULL::text, NULL::text, NULL::text; RETURN;
    END IF;
    IF v_yo.nivel_cuenta IS DISTINCT FROM 'dueno' THEN
        RETURN QUERY SELECT 'sin_permiso'::text, NULL::text, NULL::text, NULL::text; RETURN;
    END IF;

    SELECT e.id, COALESCE(e.razon_social, e.nombre) AS nombre, e.pausada_en, e.cuenta_id INTO v_e
      FROM empresas e WHERE e.id = v_l.empresa_id FOR UPDATE;

    IF v_e.pausada_en IS NULL THEN
        RETURN QUERY SELECT 'no_pausada'::text, NULL::text, NULL::text, NULL::text; RETURN;
    END IF;

    IF v_yo.tipo = 'negocio' AND EXISTS (
        SELECT 1 FROM empresas e WHERE e.cuenta_id = v_yo.cuenta_id AND e.activo AND e.id <> v_e.id)
    THEN
        RETURN QUERY SELECT 'convertir'::text, NULL::text, NULL::text, NULL::text; RETURN;
    END IF;

    UPDATE enlaces_recuperacion SET usado_en = now(), usado_por = v_yo.id WHERE id = v_l.id;

    -- Si era de su propia cuenta (la pausó él mismo), solo se reanuda.
    SELECT * INTO v_aviso FROM traspasar_empresa(v_e.id, v_yo.cuenta_id, v_yo.id, 'cuenta_existente');

    RETURN QUERY SELECT 'ok'::text,
        CASE WHEN v_e.cuenta_id = v_yo.cuenta_id THEN NULL ELSE v_aviso.aviso_correo END,
        CASE WHEN v_e.cuenta_id = v_yo.cuenta_id THEN NULL ELSE v_aviso.aviso_nombre END,
        v_e.nombre;
END $$;


-- =============================================================================
-- 8. «YA LO REVISÉ» (con sesión, la empresa abierta)
-- =============================================================================

CREATE OR REPLACE FUNCTION revision_hecha()
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
BEGIN
    UPDATE empresas SET revision_pendiente = false
     WHERE id = ctx_empresa() AND revision_pendiente;
    IF NOT FOUND THEN RETURN 'nada'; END IF;

    PERFORM registrar_evento('otro'::text, true, ctx_usuario(), ctx_empresa(), NULL,
        jsonb_build_object('accion', 'revision_tras_recuperar'));
    RETURN 'ok';
END $$;


GRANT EXECUTE ON FUNCTION enlace_tras_pausa(uuid, bytea, timestamptz)                     TO plataforma_app;
GRANT EXECUTE ON FUNCTION pedir_recuperacion(text, bytea, timestamptz, inet)              TO plataforma_app;
GRANT EXECUTE ON FUNCTION recordatorios_pendientes()                                      TO plataforma_app;
GRANT EXECUTE ON FUNCTION enlace_recordatorio(uuid, bytea, timestamptz)                   TO plataforma_app;
GRANT EXECUTE ON FUNCTION resolver_recuperacion(bytea)                                    TO plataforma_app;
GRANT EXECUTE ON FUNCTION recuperar_con_cuenta_nueva(bytea, text, bytea, timestamptz, inet) TO plataforma_app;
GRANT EXECUTE ON FUNCTION recuperar_a_mi_cuenta(bytea)                                    TO plataforma_app;
GRANT EXECUTE ON FUNCTION revision_hecha()                                                TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('021_recuperar_empresa');

COMMIT;