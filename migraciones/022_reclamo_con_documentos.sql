-- =============================================================================
-- 022 — RECLAMAR LA EMPRESA CON DOCUMENTOS (fase 4b, camino C)
--
-- Lienzo «Fase 4 — Traspasos», pantallas 5, 6 y 7. Para cuando el correo del
-- titular ya no es de quien reclama, o la empresa la tiene otra cuenta:
--
--   1. Quien reclama llena el formulario (sin sesión) con sus documentos:
--      ficha RUC, DNI, vigencia de poder y, si puede, el certificado digital.
--      Los archivos se guardan CIFRADOS con la llave maestra (lo hace la API).
--   2. Se avisa a la cuenta que hoy tiene la empresa: 5 días para responder.
--      Y a los super administradores.
--   3. El super administrador revisa y APRUEBA o RECHAZA (con motivo).
--
-- APROBAR NO INVENTA UN CAMINO NUEVO: reutiliza el de la 021. La empresa queda
-- en pausa (si no lo estaba), su correo del titular pasa a ser el que dio quien
-- reclamó, y a ese correo le llega el enlace de «Recupera tu empresa». Desde
-- ahí, lo mismo que el camino A: crea su cuenta o entra con la que tiene.
--
-- Nadie más que el super administrador ve una solicitud ni un documento.
-- No se borra nada.
-- =============================================================================

BEGIN;

-- =============================================================================
-- 1. LAS SOLICITUDES
-- =============================================================================

CREATE SEQUENCE solicitudes_traspaso_numero_seq;

CREATE TABLE solicitudes_traspaso (
    id               uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    numero           integer     NOT NULL DEFAULT nextval('solicitudes_traspaso_numero_seq'),

    empresa_id       uuid        NOT NULL REFERENCES empresas (id),
    ruc              text        NOT NULL,

    -- Quien reclama.
    nombre           text        NOT NULL,
    dni              text        NOT NULL CHECK (dni ~ '^[0-9]{8}$'),
    relacion         text        NOT NULL CHECK (relacion IN ('titular', 'representante', 'apoderado')),
    correo_nuevo     text        NOT NULL,
    telefono         text,
    explicacion      text        NOT NULL,
    ip               inet,

    -- La cuenta que tenía la empresa al pedirla, y hasta cuándo puede responder.
    cuenta_anterior  uuid,
    plazo_hasta      timestamptz NOT NULL,

    estado           text        NOT NULL DEFAULT 'por_revisar'
                                 CHECK (estado IN ('por_revisar', 'aprobada', 'rechazada')),
    motivo_rechazo   text,
    revisada_por     uuid,
    revisada_en      timestamptz,

    creado_en        timestamptz NOT NULL DEFAULT now(),
    creado_por       uuid,
    modificado_en    timestamptz,
    modificado_por   uuid,
    activo           boolean     NOT NULL DEFAULT true,
    version          integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_solicitudes_rechazo CHECK (
        estado <> 'rechazada' OR length(btrim(COALESCE(motivo_rechazo, ''))) >= 10)
);

ALTER SEQUENCE solicitudes_traspaso_numero_seq OWNED BY solicitudes_traspaso.numero;

CREATE UNIQUE INDEX ux_solicitudes_traspaso_numero ON solicitudes_traspaso (numero);

-- Una sola abierta por empresa.
CREATE UNIQUE INDEX ux_solicitudes_traspaso_abierta
    ON solicitudes_traspaso (empresa_id) WHERE estado = 'por_revisar';


CREATE TABLE solicitud_documentos (
    id            uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    solicitud_id  uuid        NOT NULL REFERENCES solicitudes_traspaso (id),
    tipo          text        NOT NULL CHECK (tipo IN ('ficha_ruc', 'dni', 'vigencia_poder', 'certificado')),
    nombre        text        NOT NULL,
    tipo_mime     text        NOT NULL,
    tamano        integer     NOT NULL CHECK (tamano BETWEEN 1 AND 5242880),

    -- AES-GCM con la llave maestra, como las credenciales. Lo cifra la API.
    contenido_cifrado bytea   NOT NULL,

    creado_en     timestamptz NOT NULL DEFAULT now(),
    creado_por    uuid,
    modificado_en timestamptz,
    modificado_por uuid,
    activo        boolean     NOT NULL DEFAULT true,
    version       integer     NOT NULL DEFAULT 1
);

CREATE INDEX ix_solicitud_documentos ON solicitud_documentos (solicitud_id);

-- Sin permisos para plataforma_app: todo pasa por las funciones de abajo.
ALTER TABLE solicitudes_traspaso ENABLE ROW LEVEL SECURITY;
ALTER TABLE solicitudes_traspaso FORCE  ROW LEVEL SECURITY;
ALTER TABLE solicitud_documentos ENABLE ROW LEVEL SECURITY;
ALTER TABLE solicitud_documentos FORCE  ROW LEVEL SECURITY;

SELECT vigilar_tabla('solicitudes_traspaso');
SELECT vigilar_tabla('solicitud_documentos', ARRAY['contenido_cifrado']);


-- =============================================================================
-- 2. RECLAMAR (sin sesión)
--
-- resultado: ok · sin_empresa · ya_hay (una abierta para esa empresa) ·
--            demasiados (5 por hora desde la misma red)
-- Devuelve a quién avisar de la cuenta que la tiene (su dueño).
-- =============================================================================

CREATE OR REPLACE FUNCTION crear_solicitud_traspaso(
    p_ruc text, p_nombre text, p_dni text, p_relacion text,
    p_correo text, p_telefono text, p_explicacion text, p_ip inet)
RETURNS TABLE (resultado text, solicitud_id uuid, numero integer, empresa text,
               plazo_hasta timestamptz, aviso_correo text, aviso_nombre text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_e   record;
    v_id  uuid;
    v_num integer;
    v_plazo timestamptz := now() + interval '5 days';
BEGIN
    IF p_ip IS NOT NULL AND (
        SELECT count(*) FROM registro_intentos ri
         WHERE ri.ip = p_ip AND ri.resultado LIKE 'reclamo%'
           AND ri.ocurrido_en > now() - interval '1 hour') >= 5
    THEN
        INSERT INTO registro_intentos (ip, correo, resultado) VALUES (p_ip, p_correo, 'reclamo_demasiados');
        RETURN QUERY SELECT 'demasiados'::text, NULL::uuid, NULL::int, NULL::text, NULL::timestamptz, NULL::text, NULL::text;
        RETURN;
    END IF;

    INSERT INTO registro_intentos (ip, correo, resultado) VALUES (p_ip, p_correo, 'reclamo');

    SELECT e.id, e.cuenta_id, COALESCE(e.razon_social, e.nombre) AS nombre INTO v_e
      FROM empresas e WHERE e.ruc = btrim(p_ruc) AND e.activo;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 'sin_empresa'::text, NULL::uuid, NULL::int, NULL::text, NULL::timestamptz, NULL::text, NULL::text;
        RETURN;
    END IF;

    IF EXISTS (SELECT 1 FROM solicitudes_traspaso s WHERE s.empresa_id = v_e.id AND s.estado = 'por_revisar') THEN
        RETURN QUERY SELECT 'ya_hay'::text, NULL::uuid, NULL::int, v_e.nombre, NULL::timestamptz, NULL::text, NULL::text;
        RETURN;
    END IF;

    INSERT INTO solicitudes_traspaso (empresa_id, ruc, nombre, dni, relacion, correo_nuevo, telefono,
                                      explicacion, ip, cuenta_anterior, plazo_hasta)
    VALUES (v_e.id, btrim(p_ruc), btrim(p_nombre), btrim(p_dni), p_relacion, btrim(p_correo),
            NULLIF(btrim(COALESCE(p_telefono, '')), ''), btrim(p_explicacion), p_ip, v_e.cuenta_id, v_plazo)
    RETURNING id, solicitudes_traspaso.numero INTO v_id, v_num;

    PERFORM registrar_evento('otro'::text, true, NULL, v_e.id, btrim(p_correo),
        jsonb_build_object('accion', 'traspaso_pedido', 'solicitud', v_num));

    RETURN QUERY
        SELECT 'ok'::text, v_id, v_num, v_e.nombre, v_plazo,
               (SELECT u.correo FROM usuarios u
                 WHERE u.cuenta_id = v_e.cuenta_id AND u.nivel_cuenta = 'dueno' AND u.activo LIMIT 1),
               (SELECT u.nombre FROM usuarios u
                 WHERE u.cuenta_id = v_e.cuenta_id AND u.nivel_cuenta = 'dueno' AND u.activo LIMIT 1);
END $$;


-- Los documentos, en la misma petición que la solicitud: solo en sus primeros
-- 10 minutos y hasta 6.
CREATE OR REPLACE FUNCTION agregar_documento_solicitud(
    p_solicitud uuid, p_tipo text, p_nombre text, p_mime text, p_tamano integer, p_contenido bytea)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM solicitudes_traspaso s
                    WHERE s.id = p_solicitud AND s.estado = 'por_revisar'
                      AND s.creado_en > now() - interval '10 minutes')
       OR (SELECT count(*) FROM solicitud_documentos d WHERE d.solicitud_id = p_solicitud) >= 6
    THEN
        RETURN 'no';
    END IF;

    INSERT INTO solicitud_documentos (solicitud_id, tipo, nombre, tipo_mime, tamano, contenido_cifrado)
    VALUES (p_solicitud, p_tipo, left(p_nombre, 200), left(p_mime, 100), p_tamano, p_contenido);
    RETURN 'ok';
END $$;


-- =============================================================================
-- 3. EL SUPER ADMINISTRADOR REVISA
-- =============================================================================

CREATE OR REPLACE FUNCTION solicitudes_traspaso_lista()
RETURNS TABLE (
    id uuid, numero integer, estado text, empresa_id uuid, empresa text, ruc text,
    nombre text, dni text, relacion text, correo_nuevo text, telefono text, explicacion text,
    cuenta_anterior text, pausada boolean, plazo_hasta timestamptz, creado_en timestamptz,
    motivo_rechazo text, revisada_en timestamptz, documentos jsonb)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT s.id, s.numero, s.estado, s.empresa_id, COALESCE(e.razon_social, e.nombre), s.ruc,
           s.nombre, s.dni, s.relacion, s.correo_nuevo, s.telefono, s.explicacion,
           (SELECT COALESCE(c.titular_nombre, 'Cuenta sin titular') FROM cuentas c WHERE c.id = s.cuenta_anterior),
           e.pausada_en IS NOT NULL,
           s.plazo_hasta, s.creado_en, s.motivo_rechazo, s.revisada_en,
           COALESCE((SELECT jsonb_agg(jsonb_build_object('id', d.id, 'tipo', d.tipo, 'nombre', d.nombre,
                                                         'tamano', d.tamano) ORDER BY d.creado_en)
                       FROM solicitud_documentos d WHERE d.solicitud_id = s.id), '[]'::jsonb)
      FROM solicitudes_traspaso s
      JOIN empresas e ON e.id = s.empresa_id
     WHERE ctx_es_super()
     ORDER BY s.estado <> 'por_revisar', s.creado_en DESC;
$$;

CREATE OR REPLACE FUNCTION documento_solicitud(p_documento uuid)
RETURNS TABLE (nombre text, tipo_mime text, contenido_cifrado bytea)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT d.nombre, d.tipo_mime, d.contenido_cifrado
      FROM solicitud_documentos d
     WHERE d.id = p_documento AND ctx_es_super();
$$;


-- --- Aprobar ------------------------------------------------------------------
--
-- resultado: ok · sin_permiso · no_existe · ya_revisada
-- Deja la empresa en pausa (si no lo estaba), cambia el correo del titular y
-- crea el enlace de recuperación (021) hacia ese correo. Devuelve a quién
-- escribir y a quién avisar de la cuenta anterior.

CREATE OR REPLACE FUNCTION aprobar_traspaso(p_solicitud uuid, p_token_hash bytea, p_expira timestamptz)
RETURNS TABLE (resultado text, correo text, nombre text, empresa text, ruc text,
               aviso_correo text, aviso_nombre text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_s solicitudes_traspaso%ROWTYPE;
    v_e record;
BEGIN
    IF NOT ctx_es_super() THEN
        RETURN QUERY SELECT 'sin_permiso'::text, NULL::text, NULL::text, NULL::text, NULL::text, NULL::text, NULL::text; RETURN;
    END IF;

    SELECT * INTO v_s FROM solicitudes_traspaso WHERE id = p_solicitud FOR UPDATE;
    IF NOT FOUND THEN
        RETURN QUERY SELECT 'no_existe'::text, NULL::text, NULL::text, NULL::text, NULL::text, NULL::text, NULL::text; RETURN;
    END IF;
    IF v_s.estado <> 'por_revisar' THEN
        RETURN QUERY SELECT 'ya_revisada'::text, NULL::text, NULL::text, NULL::text, NULL::text, NULL::text, NULL::text; RETURN;
    END IF;

    SELECT e.id, e.cuenta_id, e.pausada_en, e.ruc, COALESCE(e.razon_social, e.nombre) AS nombre INTO v_e
      FROM empresas e WHERE e.id = v_s.empresa_id FOR UPDATE;

    -- La pausa corta el acceso de la cuenta que la tenía, ya.
    IF v_e.pausada_en IS NULL THEN
        UPDATE empresas
           SET pausada_en = now(), pausada_por = ctx_usuario(),
               motivo_pausa = 'Traspaso aprobado a su titular con documentos (solicitud N.º ' || v_s.numero || ').'
         WHERE id = v_e.id;
        UPDATE usuarios SET empresa_id = NULL
         WHERE empresa_id = v_e.id AND nivel_cuenta IS NOT NULL;
    END IF;

    UPDATE empresas SET correo_titular = v_s.correo_nuevo, recordatorios_enviados = 0 WHERE id = v_e.id;

    UPDATE solicitudes_traspaso
       SET estado = 'aprobada', revisada_por = ctx_usuario(), revisada_en = now()
     WHERE id = v_s.id;

    PERFORM nuevo_enlace_recuperacion(v_e.id, p_token_hash, p_expira, 'pedido');

    PERFORM registrar_evento('otro'::text, true, ctx_usuario(), v_e.id, v_s.correo_nuevo,
        jsonb_build_object('accion', 'traspaso_aprobado', 'solicitud', v_s.numero));

    RETURN QUERY
        SELECT 'ok'::text, v_s.correo_nuevo, v_s.nombre, v_e.nombre, v_e.ruc,
               (SELECT u.correo FROM usuarios u
                 WHERE u.cuenta_id = v_e.cuenta_id AND u.nivel_cuenta = 'dueno' AND u.activo LIMIT 1),
               (SELECT u.nombre FROM usuarios u
                 WHERE u.cuenta_id = v_e.cuenta_id AND u.nivel_cuenta = 'dueno' AND u.activo LIMIT 1);
END $$;


-- --- Rechazar -------------------------------------------------------------------
-- resultado: ok · sin_permiso · no_existe · ya_revisada · motivo

CREATE OR REPLACE FUNCTION rechazar_traspaso(p_solicitud uuid, p_motivo text)
RETURNS TABLE (resultado text, correo text, nombre text, empresa text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_s solicitudes_traspaso%ROWTYPE;
BEGIN
    IF NOT ctx_es_super() THEN
        RETURN QUERY SELECT 'sin_permiso'::text, NULL::text, NULL::text, NULL::text; RETURN;
    END IF;
    IF length(btrim(COALESCE(p_motivo, ''))) NOT BETWEEN 10 AND 500 THEN
        RETURN QUERY SELECT 'motivo'::text, NULL::text, NULL::text, NULL::text; RETURN;
    END IF;

    SELECT * INTO v_s FROM solicitudes_traspaso WHERE id = p_solicitud FOR UPDATE;
    IF NOT FOUND THEN RETURN QUERY SELECT 'no_existe'::text, NULL::text, NULL::text, NULL::text; RETURN; END IF;
    IF v_s.estado <> 'por_revisar' THEN
        RETURN QUERY SELECT 'ya_revisada'::text, NULL::text, NULL::text, NULL::text; RETURN;
    END IF;

    UPDATE solicitudes_traspaso
       SET estado = 'rechazada', motivo_rechazo = btrim(p_motivo),
           revisada_por = ctx_usuario(), revisada_en = now()
     WHERE id = v_s.id;

    PERFORM registrar_evento('otro'::text, true, ctx_usuario(), v_s.empresa_id, v_s.correo_nuevo,
        jsonb_build_object('accion', 'traspaso_rechazado', 'solicitud', v_s.numero));

    RETURN QUERY SELECT 'ok'::text, v_s.correo_nuevo, v_s.nombre,
                        (SELECT COALESCE(e.razon_social, e.nombre) FROM empresas e WHERE e.id = v_s.empresa_id);
END $$;


GRANT EXECUTE ON FUNCTION crear_solicitud_traspaso(text, text, text, text, text, text, text, inet) TO plataforma_app;
GRANT EXECUTE ON FUNCTION agregar_documento_solicitud(uuid, text, text, text, integer, bytea)        TO plataforma_app;
GRANT EXECUTE ON FUNCTION solicitudes_traspaso_lista()                                             TO plataforma_app;
GRANT EXECUTE ON FUNCTION documento_solicitud(uuid)                                                TO plataforma_app;
GRANT EXECUTE ON FUNCTION aprobar_traspaso(uuid, bytea, timestamptz)                               TO plataforma_app;
GRANT EXECUTE ON FUNCTION rechazar_traspaso(uuid, text)                                            TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('022_reclamo_con_documentos');

COMMIT;