-- =============================================================================
-- 024 — CAMINO B Y LA RESPUESTA AL RECLAMO (fase 4d)
--
-- Lienzo «Fase 4 — Traspasos», pantallas 9 a 15.
--
-- CAMINO B: una cuenta pide llevar una empresa que ya está en Reyban (en otra
-- cuenta). Se le escribe SOLO al correo del titular; si aprueba desde la página
-- del enlace, la empresa pasa ENTERA a la cuenta que la pidió (como en la 021:
-- comprobantes, clientes, series y sus usuarios de empresa). Si estaba en
-- pausa, se reanuda. El titular puede quedarse con un usuario Administrador de
-- su empresa.
--
-- CAMINO C, LA RESPUESTA: la cuenta que tiene la empresa reclamada ve el
-- reclamo («traspaso en revisión») y responde UNA vez: de acuerdo, o se opone
-- con un motivo y un documento. Solo lo lee el super administrador.
--
-- Y mientras haya un reclamo abierto, una empresa en pausa no se reanuda.
--
-- Todo por funciones SECURITY DEFINER, como la 021 y la 022: las tablas nuevas
-- no tienen políticas y plataforma_app no las lee. No se borra nada.
-- =============================================================================

BEGIN;

-- =============================================================================
-- 1. LOS PEDIDOS DEL CAMINO B
-- =============================================================================

CREATE TABLE pedidos_cartera (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id      uuid        NOT NULL REFERENCES empresas (id),

    -- Quién pide, y para qué cuenta.
    cuenta_id       uuid        NOT NULL REFERENCES cuentas (id),
    pedido_por      uuid        NOT NULL REFERENCES usuarios (id),
    mensaje         text        CHECK (mensaje IS NULL OR length(mensaje) <= 500),

    -- A dónde se escribió (el correo del titular de ese momento) y el enlace.
    correo          text        NOT NULL,
    token_hash      bytea       NOT NULL,
    expira_en       timestamptz NOT NULL,

    estado          text        NOT NULL DEFAULT 'abierto'
                                CHECK (estado IN ('abierto', 'aprobado', 'rechazado', 'cancelado', 'vencido')),
    respondido_en   timestamptz,
    cuenta_anterior uuid,

    -- «Quitar de la lista», en Mis empresas de quien pidió.
    oculto          boolean     NOT NULL DEFAULT false,

    creado_en       timestamptz NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean     NOT NULL DEFAULT true,
    version         integer     NOT NULL DEFAULT 1
);

CREATE UNIQUE INDEX ux_pedidos_cartera_token ON pedidos_cartera (token_hash);
-- Un pedido abierto por empresa, de quien sea.
CREATE UNIQUE INDEX ux_pedidos_cartera_abierto ON pedidos_cartera (empresa_id) WHERE estado = 'abierto';
CREATE INDEX ix_pedidos_cartera_cuenta ON pedidos_cartera (cuenta_id, creado_en DESC);

ALTER TABLE pedidos_cartera ENABLE ROW LEVEL SECURITY;
ALTER TABLE pedidos_cartera FORCE  ROW LEVEL SECURITY;

SELECT vigilar_tabla('pedidos_cartera', ARRAY['token_hash']);


-- =============================================================================
-- 2. LA RESPUESTA AL RECLAMO (camino C)
-- =============================================================================

ALTER TABLE solicitudes_traspaso ADD COLUMN respuesta       text
    CHECK (respuesta IS NULL OR respuesta IN ('de_acuerdo', 'se_opone'));
ALTER TABLE solicitudes_traspaso ADD COLUMN respuesta_texto text;
ALTER TABLE solicitudes_traspaso ADD COLUMN respuesta_por   uuid;
ALTER TABLE solicitudes_traspaso ADD COLUMN respuesta_en    timestamptz;

ALTER TABLE solicitudes_traspaso ADD CONSTRAINT ck_solicitudes_respuesta CHECK (
    respuesta IS DISTINCT FROM 'se_opone' OR length(btrim(COALESCE(respuesta_texto, ''))) BETWEEN 10 AND 1000);

-- El documento que adjunta la cuenta al responder.
ALTER TABLE solicitud_documentos DROP CONSTRAINT solicitud_documentos_tipo_check;
ALTER TABLE solicitud_documentos ADD CONSTRAINT solicitud_documentos_tipo_check
    CHECK (tipo IN ('ficha_ruc', 'dni', 'vigencia_poder', 'certificado', 'respuesta'));


-- =============================================================================
-- 3. AYUDAS INTERNAS
-- =============================================================================

-- j•••@tallerlopez.pe
CREATE OR REPLACE FUNCTION correo_tapado(p_correo text)
RETURNS text
LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE WHEN p_correo IS NULL OR position('@' IN p_correo) < 2 THEN NULL
                ELSE left(p_correo, 1) || '•••' || substr(p_correo, position('@' IN p_correo)) END;
$$;

-- Los pedidos abiertos cuyo enlace venció pasan a «vencido» (se llama antes de
-- mirar o de crear uno; así el índice de «uno abierto por empresa» no se queda
-- trabado con un pedido que ya nadie puede responder).
CREATE OR REPLACE FUNCTION vencer_pedidos_cartera()
RETURNS void
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = public AS $$
    UPDATE pedidos_cartera SET estado = 'vencido'
     WHERE estado = 'abierto' AND expira_en <= now();
$$;
REVOKE ALL ON FUNCTION vencer_pedidos_cartera() FROM PUBLIC;


-- =============================================================================
-- 4. ¿PUEDO PEDIR ESTA EMPRESA? (con sesión, el asistente)
--
-- resultado:
--   libre            el RUC no está en Reyban: se agrega con el asistente
--   ok               se puede pedir al titular
--   ya_es_tuya       ya está en tu cuenta
--   ya_pediste       tu cuenta ya la pidió y está esperando
--   ya_pedida        otra cuenta la pidió y está esperando
--   reclamo_abierto  hay un reclamo con documentos abierto
--   sin_correo       la empresa no tiene correo del titular
--   sin_permiso      colaborador, usuario de empresa o super administrador
--   convertir        Cuenta Negocio que ya tiene su empresa
--   cuenta_no_activa · tope (10 pedidos por día y cuenta)
-- =============================================================================

CREATE OR REPLACE FUNCTION revisar_pedido_empresa(p_ruc text)
RETURNS TABLE (resultado text, empresa text, correo text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
#variable_conflict use_column
DECLARE
    v_yo record;
    v_e  record;
BEGIN
    PERFORM vencer_pedidos_cartera();

    SELECT e.id, e.cuenta_id, COALESCE(e.razon_social, e.nombre) AS nombre, e.correo_titular INTO v_e
      FROM empresas e WHERE e.ruc = btrim(p_ruc) AND e.activo;

    IF NOT FOUND THEN RETURN QUERY SELECT 'libre'::text, NULL::text, NULL::text; RETURN; END IF;

    SELECT u.id, u.cuenta_id, u.nivel_cuenta, u.es_super_admin, c.tipo, c.estado INTO v_yo
      FROM usuarios u LEFT JOIN cuentas c ON c.id = u.cuenta_id
     WHERE u.id = ctx_usuario() AND u.activo;

    IF NOT FOUND OR v_yo.es_super_admin OR v_yo.nivel_cuenta IS NULL OR v_yo.nivel_cuenta = 'colaborador' THEN
        RETURN QUERY SELECT 'sin_permiso'::text, v_e.nombre, NULL::text; RETURN;
    END IF;
    IF v_e.cuenta_id = v_yo.cuenta_id THEN
        RETURN QUERY SELECT 'ya_es_tuya'::text, v_e.nombre, NULL::text; RETURN;
    END IF;
    IF v_yo.estado IS DISTINCT FROM 'activa' THEN
        RETURN QUERY SELECT 'cuenta_no_activa'::text, v_e.nombre, NULL::text; RETURN;
    END IF;
    IF v_yo.tipo = 'negocio' AND EXISTS (SELECT 1 FROM empresas x WHERE x.cuenta_id = v_yo.cuenta_id AND x.activo) THEN
        RETURN QUERY SELECT 'convertir'::text, v_e.nombre, NULL::text; RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM pedidos_cartera p WHERE p.empresa_id = v_e.id AND p.estado = 'abierto') THEN
        RETURN QUERY SELECT CASE WHEN EXISTS (SELECT 1 FROM pedidos_cartera p
                                              WHERE p.empresa_id = v_e.id AND p.estado = 'abierto'
                                                AND p.cuenta_id = v_yo.cuenta_id)
                                 THEN 'ya_pediste' ELSE 'ya_pedida' END, v_e.nombre, NULL::text;
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM solicitudes_traspaso s WHERE s.empresa_id = v_e.id AND s.estado = 'por_revisar') THEN
        RETURN QUERY SELECT 'reclamo_abierto'::text, v_e.nombre, NULL::text; RETURN;
    END IF;
    IF v_e.correo_titular IS NULL OR btrim(v_e.correo_titular) = '' THEN
        RETURN QUERY SELECT 'sin_correo'::text, v_e.nombre, NULL::text; RETURN;
    END IF;
    IF (SELECT count(*) FROM pedidos_cartera p
         WHERE p.cuenta_id = v_yo.cuenta_id AND p.creado_en > now() - interval '1 day') >= 10 THEN
        RETURN QUERY SELECT 'tope'::text, v_e.nombre, NULL::text; RETURN;
    END IF;

    RETURN QUERY SELECT 'ok'::text, v_e.nombre, correo_tapado(btrim(v_e.correo_titular));
END $$;


-- =============================================================================
-- 5. PEDIRLA
--
-- Mismos códigos que arriba. Si es «ok», devuelve el correo REAL del titular
-- (para que la API le escriba; a la pantalla solo llega el tapado) y los
-- nombres para el correo.
-- =============================================================================

CREATE OR REPLACE FUNCTION pedir_empresa(p_ruc text, p_mensaje text, p_token_hash bytea, p_expira timestamptz)
RETURNS TABLE (resultado text, pedido_id uuid, correo text, empresa text, ruc text,
               quien text, cuenta text, correo_tapado text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
#variable_conflict use_column
DECLARE
    v_rev record;
    v_e   record;
    v_yo  record;
    v_id  uuid;
BEGIN
    SELECT * INTO v_rev FROM revisar_pedido_empresa(p_ruc);
    IF v_rev.resultado <> 'ok' THEN
        RETURN QUERY SELECT v_rev.resultado, NULL::uuid, NULL::text, v_rev.empresa, NULL::text,
                            NULL::text, NULL::text, NULL::text;
        RETURN;
    END IF;

    SELECT e.id, e.cuenta_id, e.ruc, COALESCE(e.razon_social, e.nombre) AS nombre, btrim(e.correo_titular) AS correo
      INTO v_e FROM empresas e WHERE e.ruc = btrim(p_ruc) AND e.activo FOR UPDATE;

    SELECT u.id, u.nombre, u.cuenta_id, c.titular_nombre INTO v_yo
      FROM usuarios u JOIN cuentas c ON c.id = u.cuenta_id WHERE u.id = ctx_usuario();

    INSERT INTO pedidos_cartera (empresa_id, cuenta_id, pedido_por, mensaje, correo, token_hash, expira_en,
                                 cuenta_anterior)
    VALUES (v_e.id, v_yo.cuenta_id, v_yo.id, NULLIF(left(btrim(COALESCE(p_mensaje, '')), 500), ''),
            v_e.correo, p_token_hash, p_expira, v_e.cuenta_id)
    RETURNING id INTO v_id;

    PERFORM registrar_evento('otro'::text, true, v_yo.id, v_e.id, v_e.correo,
        jsonb_build_object('accion', 'empresa_pedida', 'pedido', v_id, 'cuenta', v_yo.cuenta_id));

    RETURN QUERY SELECT 'ok'::text, v_id, v_e.correo, v_e.nombre, v_e.ruc,
                        v_yo.nombre, COALESCE(v_yo.titular_nombre, v_yo.nombre), correo_tapado(v_e.correo);
END $$;


-- =============================================================================
-- 6. LOS PEDIDOS DE MI CUENTA (Mis empresas) · CANCELAR · QUITAR DE LA LISTA
-- =============================================================================

CREATE OR REPLACE FUNCTION mis_pedidos()
RETURNS TABLE (id uuid, empresa text, ruc text, estado text, creado_en timestamptz,
               expira_en timestamptz, respondido_en timestamptz)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
#variable_conflict use_column
BEGIN
    PERFORM vencer_pedidos_cartera();

    RETURN QUERY
        SELECT p.id, COALESCE(e.razon_social, e.nombre), e.ruc, p.estado, p.creado_en, p.expira_en, p.respondido_en
          FROM pedidos_cartera p
          JOIN empresas e ON e.id = p.empresa_id
          JOIN usuarios u ON u.id = ctx_usuario() AND u.activo AND u.cuenta_id = p.cuenta_id
         WHERE u.nivel_cuenta IN ('dueno', 'colaborador_admin')
           AND NOT p.oculto
           -- Los aprobados ya están en la lista de empresas: no se repiten.
           AND p.estado <> 'aprobado'
           AND (p.estado = 'abierto' OR p.creado_en > now() - interval '30 days')
         ORDER BY p.creado_en DESC;
END $$;

-- ok · no_existe · sin_permiso · no_abierto
CREATE OR REPLACE FUNCTION cancelar_pedido(p_pedido uuid)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_p pedidos_cartera%ROWTYPE;
BEGIN
    SELECT p.* INTO v_p FROM pedidos_cartera p
      JOIN usuarios u ON u.id = ctx_usuario() AND u.activo AND u.cuenta_id = p.cuenta_id
     WHERE p.id = p_pedido FOR UPDATE OF p;
    IF NOT FOUND THEN RETURN 'no_existe'; END IF;

    IF NOT EXISTS (SELECT 1 FROM usuarios u WHERE u.id = ctx_usuario()
                      AND u.nivel_cuenta IN ('dueno', 'colaborador_admin')) THEN
        RETURN 'sin_permiso';
    END IF;
    IF v_p.estado <> 'abierto' THEN RETURN 'no_abierto'; END IF;

    UPDATE pedidos_cartera SET estado = 'cancelado', respondido_en = now() WHERE id = v_p.id;
    RETURN 'ok';
END $$;

-- ok · no_existe · abierto (uno abierto se cancela, no se esconde)
CREATE OR REPLACE FUNCTION ocultar_pedido(p_pedido uuid)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_estado text;
BEGIN
    SELECT p.estado INTO v_estado FROM pedidos_cartera p
      JOIN usuarios u ON u.id = ctx_usuario() AND u.activo AND u.cuenta_id = p.cuenta_id
                     AND u.nivel_cuenta IN ('dueno', 'colaborador_admin')
     WHERE p.id = p_pedido;
    IF NOT FOUND THEN RETURN 'no_existe'; END IF;
    IF v_estado = 'abierto' THEN RETURN 'abierto'; END IF;

    UPDATE pedidos_cartera SET oculto = true WHERE id = p_pedido;
    RETURN 'ok';
END $$;


-- =============================================================================
-- 7. LA PÁGINA DEL TITULAR (sin sesión)
--
-- estado: ok · vencido · usado (ya respondido) · cancelado · no_existe
-- titular (qué pasa con el titular si marca «quiero seguir entrando»):
--   crear       su correo no tiene usuario: se le crea uno de la empresa
--   mover       es el dueño de la Cuenta Negocio que hoy la tiene, y no tiene
--               otra empresa: su usuario pasa a ser de la empresa
--   ya_entra    ya es usuario de esa empresa, o ya es de la cuenta que la pide
--   otra_cuenta su correo es de otra cuenta: no se puede (se le explica)
-- =============================================================================

CREATE OR REPLACE FUNCTION titular_del_pedido(p_correo text, p_empresa uuid, p_cuenta_nueva uuid)
RETURNS text
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_u record;
    v_e record;
BEGIN
    SELECT u.id, u.cuenta_id, u.empresa_id, u.nivel_cuenta, u.es_super_admin INTO v_u
      FROM usuarios u WHERE lower(u.correo) = lower(btrim(p_correo));
    IF NOT FOUND THEN RETURN 'crear'; END IF;

    SELECT e.cuenta_id INTO v_e FROM empresas e WHERE e.id = p_empresa;

    IF v_u.es_super_admin THEN RETURN 'otra_cuenta'; END IF;
    IF v_u.cuenta_id = p_cuenta_nueva THEN RETURN 'ya_entra'; END IF;
    IF v_u.nivel_cuenta IS NULL AND v_u.empresa_id = p_empresa THEN RETURN 'ya_entra'; END IF;

    IF v_u.nivel_cuenta = 'dueno' AND v_u.cuenta_id = v_e.cuenta_id
       AND (SELECT c.tipo FROM cuentas c WHERE c.id = v_u.cuenta_id) = 'negocio'
       AND NOT EXISTS (SELECT 1 FROM empresas x WHERE x.cuenta_id = v_u.cuenta_id AND x.activo AND x.id <> p_empresa)
       AND NOT EXISTS (SELECT 1 FROM usuarios o WHERE o.cuenta_id = v_u.cuenta_id AND o.activo
                          AND o.nivel_cuenta IS NOT NULL AND o.id <> v_u.id)
    THEN
        RETURN 'mover';
    END IF;

    RETURN 'otra_cuenta';
END $$;
REVOKE ALL ON FUNCTION titular_del_pedido(text, uuid, uuid) FROM PUBLIC;


CREATE OR REPLACE FUNCTION resolver_pedido(p_token_hash bytea)
RETURNS TABLE (estado text, empresa text, ruc text, quien text, cuenta text, mensaje text,
               hoy_la_tiene text, pausada boolean, comprobantes integer, clientes integer, series text[],
               expira_en timestamptz, titular text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
#variable_conflict use_column
DECLARE
    v_p pedidos_cartera%ROWTYPE;
    v_e record;
BEGIN
    PERFORM vencer_pedidos_cartera();

    SELECT * INTO v_p FROM pedidos_cartera p WHERE p.token_hash = p_token_hash;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'no_existe'::text, NULL::text, NULL::text, NULL::text, NULL::text, NULL::text,
                            NULL::text, false, 0, 0, NULL::text[], NULL::timestamptz, NULL::text;
        RETURN;
    END IF;

    SELECT e.id, COALESCE(e.razon_social, e.nombre) AS nombre, e.ruc, e.cuenta_id, e.pausada_en,
           btrim(e.correo_titular) AS correo_titular INTO v_e
      FROM empresas e WHERE e.id = v_p.empresa_id;

    RETURN QUERY SELECT
        -- Si entretanto cambió el correo del titular (un reclamo aprobado), el
        -- enlace ya no es del titular: no sirve.
        CASE WHEN v_p.estado = 'abierto' AND lower(v_p.correo) IS DISTINCT FROM lower(v_e.correo_titular) THEN 'cancelado'
             ELSE CASE v_p.estado WHEN 'abierto' THEN 'ok' WHEN 'vencido' THEN 'vencido'
                                  WHEN 'cancelado' THEN 'cancelado' ELSE 'usado' END END,
        v_e.nombre, v_e.ruc,
        (SELECT u.nombre FROM usuarios u WHERE u.id = v_p.pedido_por),
        (SELECT COALESCE(c.titular_nombre, '') FROM cuentas c WHERE c.id = v_p.cuenta_id),
        v_p.mensaje,
        (SELECT c.titular_nombre FROM cuentas c WHERE c.id = v_e.cuenta_id),
        v_e.pausada_en IS NOT NULL,
        (SELECT count(*)::int FROM documentos d WHERE d.empresa_id = v_e.id AND d.estado <> 'borrador'),
        (SELECT count(*)::int FROM clientes c WHERE c.empresa_id = v_e.id AND c.activo),
        ARRAY(SELECT DISTINCT d.serie FROM documentos d
               WHERE d.empresa_id = v_e.id AND d.serie IS NOT NULL ORDER BY 1),
        v_p.expira_en,
        titular_del_pedido(v_p.correo, v_e.id, v_p.cuenta_id);
END $$;


-- =============================================================================
-- 8. EL TITULAR RESPONDE (sin sesión)
--
-- resultado: aprobado · rechazado · vencido · usado · cancelado · no_existe ·
--            reclamo_abierto (hay un reclamo con documentos: primero ese) ·
--            nombre (falta el nombre para crearle el usuario)
--
-- Al aprobar, la empresa pasa entera a la cuenta que la pidió: como el
-- traspaso de la 021, pero SIN «por revisar» (el titular no la recupera para
-- sí: se la confía a otra cuenta). Si estaba en pausa, se reanuda.
--
-- p_seguir: el titular quiere seguir entrando (ver titular_del_pedido).
-- Si hay que crearle usuario, se usa p_inv_hash para su enlace de invitación.
-- =============================================================================

CREATE OR REPLACE FUNCTION responder_pedido(
    p_token_hash bytea, p_aprobar boolean, p_seguir boolean, p_nombre text,
    p_inv_hash bytea, p_inv_expira timestamptz)
RETURNS TABLE (resultado text, empresa_id uuid, empresa text, estaba_pausada boolean,
               titular text, invitacion boolean,
               pide_correo text, pide_nombre text, aviso_correo text, aviso_nombre text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
#variable_conflict use_column
DECLARE
    v_p        pedidos_cartera%ROWTYPE;
    v_e        record;
    v_titular  text;
    v_usuario  uuid;
    v_inv      boolean := false;
    v_aviso    record;
    v_pide     record;
BEGIN
    PERFORM vencer_pedidos_cartera();

    SELECT * INTO v_p FROM pedidos_cartera p WHERE p.token_hash = p_token_hash FOR UPDATE;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'no_existe'::text, NULL::uuid, NULL::text, false, NULL::text, false,
                            NULL::text, NULL::text, NULL::text, NULL::text;
        RETURN;
    END IF;
    IF v_p.estado <> 'abierto' THEN
        RETURN QUERY SELECT CASE v_p.estado WHEN 'vencido' THEN 'vencido' WHEN 'cancelado' THEN 'cancelado'
                                 ELSE 'usado' END,
                            NULL::uuid, NULL::text, false, NULL::text, false, NULL::text, NULL::text, NULL::text, NULL::text;
        RETURN;
    END IF;

    SELECT e.id, e.cuenta_id, e.pausada_en, COALESCE(e.razon_social, e.nombre) AS nombre,
           btrim(e.correo_titular) AS correo_titular INTO v_e
      FROM empresas e WHERE e.id = v_p.empresa_id FOR UPDATE;

    -- El correo del titular cambió desde el pedido (un reclamo aprobado): este
    -- enlace ya no es del titular.
    IF lower(v_p.correo) IS DISTINCT FROM lower(v_e.correo_titular) THEN
        UPDATE pedidos_cartera SET estado = 'cancelado', respondido_en = now() WHERE id = v_p.id;
        RETURN QUERY SELECT 'cancelado'::text, NULL::uuid, NULL::text, false, NULL::text, false,
                            NULL::text, NULL::text, NULL::text, NULL::text;
        RETURN;
    END IF;

    SELECT u.correo, u.nombre INTO v_pide FROM usuarios u WHERE u.id = v_p.pedido_por;

    -- «No, gracias».
    IF NOT p_aprobar THEN
        UPDATE pedidos_cartera SET estado = 'rechazado', respondido_en = now() WHERE id = v_p.id;
        PERFORM registrar_evento('otro'::text, true, NULL, v_e.id, v_p.correo,
            jsonb_build_object('accion', 'pedido_rechazado', 'pedido', v_p.id));
        RETURN QUERY SELECT 'rechazado'::text, v_e.id, v_e.nombre, false, NULL::text, false,
                            v_pide.correo, v_pide.nombre, NULL::text, NULL::text;
        RETURN;
    END IF;

    IF EXISTS (SELECT 1 FROM solicitudes_traspaso s WHERE s.empresa_id = v_e.id AND s.estado = 'por_revisar') THEN
        RETURN QUERY SELECT 'reclamo_abierto'::text, NULL::uuid, v_e.nombre, false, NULL::text, false,
                            NULL::text, NULL::text, NULL::text, NULL::text;
        RETURN;
    END IF;

    v_titular := titular_del_pedido(v_p.correo, v_e.id, v_p.cuenta_id);

    IF p_seguir AND v_titular = 'crear' AND length(btrim(COALESCE(p_nombre, ''))) < 2 THEN
        RETURN QUERY SELECT 'nombre'::text, NULL::uuid, v_e.nombre, false, v_titular, false,
                            NULL::text, NULL::text, NULL::text, NULL::text;
        RETURN;
    END IF;

    -- --- El titular, antes de mover la empresa ------------------------------
    IF p_seguir AND v_titular = 'mover' THEN
        -- Deja de ser dueño de su Cuenta Negocio (que se queda sin empresa) y
        -- pasa a ser usuario de su empresa, con el rol que ya tenía
        -- (Administrador). Se mueve con ella justo abajo.
        UPDATE usuarios SET nivel_cuenta = NULL, ve_todas = false, empresa_id = v_e.id
         WHERE lower(correo) = lower(v_p.correo);
    END IF;

    -- --- La empresa pasa a la cuenta que la pidió ---------------------------
    UPDATE usuarios SET empresa_id = NULL
     WHERE empresa_id = v_e.id AND nivel_cuenta IS NOT NULL AND cuenta_id = v_e.cuenta_id;
    UPDATE usuario_empresa SET activo = false WHERE usuario_empresa.empresa_id = v_e.id AND activo;

    UPDATE usuarios SET cuenta_id = v_p.cuenta_id
     WHERE usuarios.empresa_id = v_e.id AND nivel_cuenta IS NULL;

    UPDATE empresas
       SET cuenta_id = v_p.cuenta_id,
           pausada_en = NULL, motivo_pausa = NULL, pausada_por = NULL,
           recordatorios_enviados = 0
     WHERE id = v_e.id;

    UPDATE enlaces_recuperacion SET anulado_en = now()
     WHERE enlaces_recuperacion.empresa_id = v_e.id AND usado_en IS NULL AND anulado_en IS NULL;

    -- --- Un usuario nuevo para el titular, si lo quiere y no tiene ----------
    IF p_seguir AND v_titular = 'crear' THEN
        INSERT INTO usuarios (cuenta_id, empresa_id, correo, nombre, clave_hash)
        VALUES (v_p.cuenta_id, v_e.id, v_p.correo, btrim(p_nombre),
                'sin-clave$' || encode(gen_random_bytes(16), 'hex'))
        RETURNING id INTO v_usuario;

        INSERT INTO usuario_rol (usuario_id, rol_id)
        VALUES (v_usuario, '11111111-1111-1111-1111-111111111111');

        INSERT INTO tokens_un_uso (usuario_id, proposito, token_hash, expira_en)
        VALUES (v_usuario, 'invitacion', p_inv_hash, p_inv_expira);
        v_inv := true;
    END IF;

    UPDATE pedidos_cartera SET estado = 'aprobado', respondido_en = now() WHERE id = v_p.id;

    PERFORM registrar_evento('otro'::text, true, NULL, v_e.id, v_p.correo,
        jsonb_build_object('accion', 'pedido_aprobado', 'pedido', v_p.id,
                           'cuenta_anterior', v_e.cuenta_id, 'cuenta_nueva', v_p.cuenta_id,
                           'titular', CASE WHEN p_seguir THEN v_titular ELSE 'no' END));

    -- A quién avisar de la cuenta anterior: su dueño, si no era el propio titular.
    SELECT u.correo, u.nombre INTO v_aviso FROM usuarios u
     WHERE u.cuenta_id = v_e.cuenta_id AND u.nivel_cuenta = 'dueno' AND u.activo
       AND lower(u.correo) <> lower(v_p.correo)
     LIMIT 1;

    RETURN QUERY SELECT 'aprobado'::text, v_e.id, v_e.nombre, v_e.pausada_en IS NOT NULL,
                        CASE WHEN p_seguir THEN v_titular ELSE 'no' END, v_inv,
                        v_pide.correo, v_pide.nombre, v_aviso.correo, v_aviso.nombre;
END $$;


-- =============================================================================
-- 9. CAMINO C: LA CUENTA VE EL RECLAMO Y RESPONDE
-- =============================================================================

-- Los reclamos abiertos sobre empresas que ve quien llama (Mis empresas, Inicio).
CREATE OR REPLACE FUNCTION reclamos_sobre_mis_empresas()
RETURNS TABLE (id uuid, numero integer, empresa_id uuid, empresa text, ruc text, pide text,
               relacion text, plazo_hasta timestamptz, respuesta text, puedo_responder boolean)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    WITH yo AS (
        SELECT u.id, u.cuenta_id, u.nivel_cuenta, u.ve_todas, u.empresa_id
          FROM usuarios u
         WHERE u.id = ctx_usuario() AND u.activo AND u.cuenta_id IS NOT NULL
    )
    SELECT s.id, s.numero, e.id, COALESCE(e.razon_social, e.nombre), e.ruc, s.nombre, s.relacion,
           s.plazo_hasta, s.respuesta,
           yo.nivel_cuenta IN ('dueno', 'colaborador_admin') AND s.respuesta IS NULL
      FROM yo
      JOIN empresas e ON e.cuenta_id = yo.cuenta_id AND e.activo
      JOIN solicitudes_traspaso s ON s.empresa_id = e.id AND s.estado = 'por_revisar'
     WHERE yo.nivel_cuenta = 'dueno' OR yo.ve_todas
        OR EXISTS (SELECT 1 FROM usuario_empresa ue
                    WHERE ue.usuario_id = yo.id AND ue.empresa_id = e.id AND ue.activo)
     ORDER BY s.creado_en;
$$;

-- ok · no_existe · sin_permiso · ya_respondida · respuesta · texto
CREATE OR REPLACE FUNCTION responder_reclamo(p_solicitud uuid, p_respuesta text, p_texto text)
RETURNS TABLE (resultado text, numero integer, empresa text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
#variable_conflict use_column
DECLARE
    v_r record;
BEGIN
    SELECT r.id, r.numero, r.empresa, r.puedo_responder, r.respuesta INTO v_r
      FROM reclamos_sobre_mis_empresas() r WHERE r.id = p_solicitud;
    IF NOT FOUND THEN RETURN QUERY SELECT 'no_existe'::text, NULL::int, NULL::text; RETURN; END IF;
    IF v_r.respuesta IS NOT NULL THEN RETURN QUERY SELECT 'ya_respondida'::text, v_r.numero, v_r.empresa; RETURN; END IF;
    IF NOT v_r.puedo_responder THEN RETURN QUERY SELECT 'sin_permiso'::text, v_r.numero, v_r.empresa; RETURN; END IF;
    IF p_respuesta IS NULL OR p_respuesta NOT IN ('de_acuerdo', 'se_opone') THEN
        RETURN QUERY SELECT 'respuesta'::text, v_r.numero, v_r.empresa; RETURN;
    END IF;
    IF (p_respuesta = 'se_opone' AND length(btrim(COALESCE(p_texto, ''))) NOT BETWEEN 10 AND 1000)
       OR length(btrim(COALESCE(p_texto, ''))) > 1000 THEN
        RETURN QUERY SELECT 'texto'::text, v_r.numero, v_r.empresa; RETURN;
    END IF;

    UPDATE solicitudes_traspaso
       SET respuesta = p_respuesta, respuesta_texto = NULLIF(btrim(COALESCE(p_texto, '')), ''),
           respuesta_por = ctx_usuario(), respuesta_en = now()
     WHERE id = p_solicitud;

    PERFORM registrar_evento('otro'::text, true, ctx_usuario(), NULL, NULL,
        jsonb_build_object('accion', 'reclamo_respondido', 'solicitud', v_r.numero, 'respuesta', p_respuesta));

    RETURN QUERY SELECT 'ok'::text, v_r.numero, v_r.empresa;
END $$;

-- El documento de la respuesta: quien respondió, en sus primeros 10 minutos, uno.
CREATE OR REPLACE FUNCTION agregar_documento_respuesta(
    p_solicitud uuid, p_nombre text, p_mime text, p_tamano integer, p_contenido bytea)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM solicitudes_traspaso s
                    WHERE s.id = p_solicitud AND s.estado = 'por_revisar'
                      AND s.respuesta_por = ctx_usuario()
                      AND s.respuesta_en > now() - interval '10 minutes')
       OR EXISTS (SELECT 1 FROM solicitud_documentos d WHERE d.solicitud_id = p_solicitud AND d.tipo = 'respuesta')
    THEN
        RETURN 'no';
    END IF;

    INSERT INTO solicitud_documentos (solicitud_id, tipo, nombre, tipo_mime, tamano, contenido_cifrado)
    VALUES (p_solicitud, 'respuesta', left(p_nombre, 200), left(p_mime, 100), p_tamano, p_contenido);
    RETURN 'ok';
END $$;


-- La lista del super administrador, ahora con la respuesta. Cambia lo que
-- devuelve: se borra y se vuelve a crear (es una función, no datos).
DROP FUNCTION solicitudes_traspaso_lista();

CREATE OR REPLACE FUNCTION solicitudes_traspaso_lista()
RETURNS TABLE (
    id uuid, numero integer, estado text, empresa_id uuid, empresa text, ruc text,
    nombre text, dni text, relacion text, correo_nuevo text, telefono text, explicacion text,
    cuenta_anterior text, pausada boolean, plazo_hasta timestamptz, creado_en timestamptz,
    motivo_rechazo text, revisada_en timestamptz, documentos jsonb,
    respuesta text, respuesta_texto text, respuesta_en timestamptz, respuesta_quien text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT s.id, s.numero, s.estado, s.empresa_id, COALESCE(e.razon_social, e.nombre), s.ruc,
           s.nombre, s.dni, s.relacion, s.correo_nuevo, s.telefono, s.explicacion,
           (SELECT COALESCE(c.titular_nombre, 'Cuenta sin titular') FROM cuentas c WHERE c.id = s.cuenta_anterior),
           e.pausada_en IS NOT NULL,
           s.plazo_hasta, s.creado_en, s.motivo_rechazo, s.revisada_en,
           COALESCE((SELECT jsonb_agg(jsonb_build_object('id', d.id, 'tipo', d.tipo, 'nombre', d.nombre,
                                                         'tamano', d.tamano) ORDER BY d.creado_en)
                       FROM solicitud_documentos d WHERE d.solicitud_id = s.id), '[]'::jsonb),
           s.respuesta, s.respuesta_texto, s.respuesta_en,
           (SELECT u.nombre || CASE u.nivel_cuenta WHEN 'dueno' THEN ' (dueño)'
                                                   WHEN 'colaborador_admin' THEN ' (colaborador administrador)'
                                                   ELSE '' END
              FROM usuarios u WHERE u.id = s.respuesta_por)
      FROM solicitudes_traspaso s
      JOIN empresas e ON e.id = s.empresa_id
     WHERE ctx_es_super()
     ORDER BY s.estado <> 'por_revisar', s.creado_en DESC;
$$;


-- =============================================================================
-- 10. REANUDAR, NO MIENTRAS SE REVISA UN RECLAMO
--
-- Misma firma (CREATE OR REPLACE). Código nuevo: en_revision.
-- =============================================================================

CREATE OR REPLACE FUNCTION reanudar_empresa(p_empresa uuid)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_yo     uuid := ctx_usuario();
    v_cuenta uuid;
    v_mio    text;
BEGIN
    SELECT u.cuenta_id, u.nivel_cuenta INTO v_cuenta, v_mio FROM usuarios u WHERE u.id = v_yo AND u.activo;

    IF v_mio IS DISTINCT FROM 'dueno' THEN RETURN 'sin_permiso'; END IF;

    IF EXISTS (SELECT 1 FROM solicitudes_traspaso s
                WHERE s.empresa_id = p_empresa AND s.estado = 'por_revisar') THEN
        RETURN 'en_revision';
    END IF;

    UPDATE empresas SET pausada_en = NULL, motivo_pausa = NULL, pausada_por = NULL
     WHERE id = p_empresa AND cuenta_id = v_cuenta AND activo AND pausada_en IS NOT NULL;

    IF NOT FOUND THEN RETURN 'no_existe'; END IF;

    PERFORM registrar_evento('otro'::text, true, NULL, p_empresa, NULL,
        jsonb_build_object('accion', 'empresa_reanudada'));

    RETURN 'ok';
END $$;


GRANT EXECUTE ON FUNCTION revisar_pedido_empresa(text)                                  TO plataforma_app;
GRANT EXECUTE ON FUNCTION pedir_empresa(text, text, bytea, timestamptz)                 TO plataforma_app;
GRANT EXECUTE ON FUNCTION mis_pedidos()                                                 TO plataforma_app;
GRANT EXECUTE ON FUNCTION cancelar_pedido(uuid)                                         TO plataforma_app;
GRANT EXECUTE ON FUNCTION ocultar_pedido(uuid)                                          TO plataforma_app;
GRANT EXECUTE ON FUNCTION resolver_pedido(bytea)                                        TO plataforma_app;
GRANT EXECUTE ON FUNCTION responder_pedido(bytea, boolean, boolean, text, bytea, timestamptz) TO plataforma_app;
GRANT EXECUTE ON FUNCTION reclamos_sobre_mis_empresas()                                 TO plataforma_app;
GRANT EXECUTE ON FUNCTION responder_reclamo(uuid, text, text)                           TO plataforma_app;
GRANT EXECUTE ON FUNCTION agregar_documento_respuesta(uuid, text, text, integer, bytea) TO plataforma_app;
GRANT EXECUTE ON FUNCTION solicitudes_traspaso_lista()                                  TO plataforma_app;
GRANT EXECUTE ON FUNCTION correo_tapado(text)                                           TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('024_camino_b_y_respuesta');

COMMIT;