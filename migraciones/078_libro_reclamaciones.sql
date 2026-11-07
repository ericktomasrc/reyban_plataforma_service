-- =============================================================================
-- 078 — LIBRO DE RECLAMACIONES VIRTUAL (7 nov 2026, e217)
--
-- Lo pide Indecopi (Código de Protección y Defensa del Consumidor, Ley 29571,
-- y su reglamento) y lo pide Culqi para aprobar la tienda: integrado en la
-- web, sin formularios ni enlaces de afuera.
--
--   · reclamos                    una hoja por reclamo o queja, con número
--                                 correlativo por año (2026-000001), los datos
--                                 del proveedor tal como estaban ese día y la
--                                 fecha límite para responder (15 días hábiles).
--   · reclamo_registrar(datos, ip) SIN SESIÓN (la página pública). Valida los
--                                 campos obligatorios y pone un tope por IP.
--   · reclamos_lista()            para la plataforma (permiso nuevo).
--   · reclamo_responder(id, texto) la respuesta queda guardada; la API la manda
--                                 por correo.
--
-- Nada se borra: una hoja no se edita ni se elimina, solo se responde.
-- Se puede correr dos veces.
-- =============================================================================
BEGIN;

CREATE TABLE IF NOT EXISTS reclamos (
    id             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    anio           integer     NOT NULL,
    numero         integer     NOT NULL,
    hoja           text        NOT NULL,
    tipo           text        NOT NULL,              -- reclamo | queja
    bien           text        NOT NULL,              -- servicio | producto
    nombre         text        NOT NULL,
    doc_tipo       text        NOT NULL,              -- DNI | CE | RUC | PASAPORTE
    doc_numero     text        NOT NULL,
    domicilio      text        NOT NULL,
    telefono       text        NOT NULL,
    correo         text        NOT NULL,
    apoderado      text,
    monto          numeric(12,2),
    descripcion    text        NOT NULL,
    detalle        text        NOT NULL,
    pedido         text        NOT NULL,
    proveedor      jsonb       NOT NULL,              -- razón social, RUC y dirección de ese día
    vence          date        NOT NULL,
    estado         text        NOT NULL DEFAULT 'pendiente',
    respuesta      text,
    respondido_en  timestamptz,
    respondido_por uuid,
    ip             inet,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,
    CONSTRAINT ck_reclamos_tipo   CHECK (tipo IN ('reclamo', 'queja')),
    CONSTRAINT ck_reclamos_bien   CHECK (bien IN ('servicio', 'producto')),
    CONSTRAINT ck_reclamos_estado CHECK (estado IN ('pendiente', 'respondido'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_reclamos_numero ON reclamos (anio, numero);

DO $$ BEGIN
    ALTER TABLE reclamos ENABLE ROW LEVEL SECURITY;
    ALTER TABLE reclamos FORCE ROW LEVEL SECURITY;
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'reclamos' AND policyname = 'p_reclamos') THEN
        CREATE POLICY p_reclamos ON reclamos USING (ctx_es_super()) WITH CHECK (ctx_es_super());
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_reclamos') THEN
        PERFORM vigilar_tabla('reclamos');
    END IF;
END $$;
GRANT SELECT ON reclamos TO plataforma_app;

-- 15 días hábiles (lunes a viernes) desde una fecha.
CREATE OR REPLACE FUNCTION dias_habiles_despues(p_desde date, p_dias integer) RETURNS date
LANGUAGE plpgsql IMMUTABLE AS $$
DECLARE d date := p_desde; n integer := 0;
BEGIN
    WHILE n < p_dias LOOP
        d := d + 1;
        IF extract(isodow FROM d) < 6 THEN n := n + 1; END IF;
    END LOOP;
    RETURN d;
END $$;

-- Registrar una hoja (sin sesión). Devuelve {hoja, vence, correo} o un error con
-- el campo que falta (RECLAMO_CAMPO: campo|mensaje).
CREATE OR REPLACE FUNCTION reclamo_registrar(p jsonb, p_ip inet) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_anio   integer := extract(year FROM (now() AT TIME ZONE 'America/Lima'))::integer;
    v_num    integer;
    v_hoja   text;
    v_vence  date := dias_habiles_despues((now() AT TIME ZONE 'America/Lima')::date, 15);
    v_prov   jsonb;
    v_monto  numeric;
    t        text;
BEGIN
    -- Obligatorios, con un mensaje que se entiende.
    FOREACH t IN ARRAY ARRAY['nombre', 'docTipo', 'docNumero', 'domicilio', 'telefono', 'correo', 'descripcion', 'detalle', 'pedido'] LOOP
        IF length(trim(COALESCE(p->>t, ''))) < 2 THEN
            RAISE EXCEPTION 'RECLAMO_CAMPO: %|Completa este dato.', t USING ERRCODE = 'check_violation';
        END IF;
    END LOOP;
    IF COALESCE(p->>'tipo', '') NOT IN ('reclamo', 'queja') THEN
        RAISE EXCEPTION 'RECLAMO_CAMPO: tipo|Elige si es un reclamo o una queja.' USING ERRCODE = 'check_violation';
    END IF;
    IF COALESCE(p->>'bien', '') NOT IN ('servicio', 'producto') THEN
        RAISE EXCEPTION 'RECLAMO_CAMPO: bien|Elige si es un servicio o un producto.' USING ERRCODE = 'check_violation';
    END IF;
    IF (p->>'correo') !~ '^[^@\s]+@[^@\s]+\.[^@\s]+$' THEN
        RAISE EXCEPTION 'RECLAMO_CAMPO: correo|Ese correo no es válido: ahí te llega la copia.' USING ERRCODE = 'check_violation';
    END IF;
    IF (p->>'docTipo') NOT IN ('DNI', 'CE', 'RUC', 'PASAPORTE') THEN
        RAISE EXCEPTION 'RECLAMO_CAMPO: docTipo|Elige el tipo de documento.' USING ERRCODE = 'check_violation';
    END IF;
    IF (p->>'docTipo') = 'DNI' AND (p->>'docNumero') !~ '^[0-9]{8}$' THEN
        RAISE EXCEPTION 'RECLAMO_CAMPO: docNumero|El DNI tiene 8 números.' USING ERRCODE = 'check_violation';
    END IF;
    IF (p->>'docTipo') = 'RUC' AND (p->>'docNumero') !~ '^[0-9]{11}$' THEN
        RAISE EXCEPTION 'RECLAMO_CAMPO: docNumero|El RUC tiene 11 números.' USING ERRCODE = 'check_violation';
    END IF;
    IF length(p->>'detalle') > 3000 OR length(p->>'pedido') > 2000 THEN
        RAISE EXCEPTION 'RECLAMO_CAMPO: detalle|El texto es demasiado largo (máximo 3000 letras).' USING ERRCODE = 'check_violation';
    END IF;
    IF NULLIF(trim(COALESCE(p->>'monto', '')), '') IS NOT NULL THEN
        BEGIN v_monto := round((p->>'monto')::numeric, 2);
        EXCEPTION WHEN others THEN
            RAISE EXCEPTION 'RECLAMO_CAMPO: monto|El monto solo lleva números (por ejemplo 39.00).' USING ERRCODE = 'check_violation';
        END;
        IF v_monto < 0 OR v_monto > 9999999 THEN
            RAISE EXCEPTION 'RECLAMO_CAMPO: monto|Revisa el monto.' USING ERRCODE = 'check_violation';
        END IF;
    END IF;

    -- Tope contra el abuso: 5 por hora desde la misma IP.
    IF p_ip IS NOT NULL AND (SELECT count(*) FROM reclamos WHERE ip = p_ip AND creado_en > now() - interval '1 hour') >= 5 THEN
        RAISE EXCEPTION 'RECLAMO_TOPE: Ya registraste varias hojas en la última hora. Espera un momento o escríbenos al correo.'
            USING ERRCODE = 'check_violation';
    END IF;

    SELECT jsonb_build_object('razonSocial', a.publica_razon_social, 'ruc', a.publica_ruc, 'direccion', a.publica_direccion,
                              'correo', a.publica_correo)
      INTO v_prov FROM plataforma_ajustes a WHERE a.id = 1;

    PERFORM pg_advisory_xact_lock(hashtext('reclamos'), v_anio);
    SELECT COALESCE(max(numero), 0) + 1 INTO v_num FROM reclamos WHERE anio = v_anio;
    v_hoja := v_anio::text || '-' || lpad(v_num::text, 6, '0');

    INSERT INTO reclamos (anio, numero, hoja, tipo, bien, nombre, doc_tipo, doc_numero, domicilio, telefono, correo,
                          apoderado, monto, descripcion, detalle, pedido, proveedor, vence, ip)
    VALUES (v_anio, v_num, v_hoja, p->>'tipo', p->>'bien', upper(trim(p->>'nombre')), p->>'docTipo', trim(p->>'docNumero'),
            trim(p->>'domicilio'), trim(p->>'telefono'), lower(trim(p->>'correo')), NULLIF(trim(COALESCE(p->>'apoderado', '')), ''),
            v_monto, trim(p->>'descripcion'), trim(p->>'detalle'), trim(p->>'pedido'), COALESCE(v_prov, '{}'::jsonb), v_vence, p_ip);

    RETURN jsonb_build_object('hoja', v_hoja, 'vence', v_vence, 'proveedor', v_prov,
                              'fecha', (now() AT TIME ZONE 'America/Lima')::date);
END $$;

-- La lista para la plataforma.
CREATE OR REPLACE FUNCTION reclamos_lista() RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'RECLAMO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    RETURN (SELECT COALESCE(jsonb_agg(jsonb_build_object(
                'id', r.id, 'hoja', r.hoja, 'fecha', r.creado_en, 'tipo', r.tipo, 'bien', r.bien, 'nombre', r.nombre,
                'docTipo', r.doc_tipo, 'docNumero', r.doc_numero, 'domicilio', r.domicilio, 'telefono', r.telefono,
                'correo', r.correo, 'apoderado', r.apoderado, 'monto', r.monto, 'descripcion', r.descripcion,
                'detalle', r.detalle, 'pedido', r.pedido, 'vence', r.vence, 'estado', r.estado,
                'respuesta', r.respuesta, 'respondidoEn', r.respondido_en,
                'diasQuedan', CASE WHEN r.estado = 'pendiente' THEN r.vence - (now() AT TIME ZONE 'America/Lima')::date END)
              ORDER BY (r.estado = 'pendiente') DESC, r.creado_en DESC), '[]'::jsonb)
              FROM reclamos r);
END $$;

-- Responder (una vez). Devuelve los datos para el correo.
CREATE OR REPLACE FUNCTION reclamo_responder(p_id uuid, p_texto text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE r reclamos%ROWTYPE;
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'RECLAMO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    IF length(trim(COALESCE(p_texto, ''))) < 10 THEN
        RAISE EXCEPTION 'RECLAMO_CAMPO: respuesta|Escribe la respuesta (al menos una frase).' USING ERRCODE = 'check_violation';
    END IF;
    SELECT * INTO r FROM reclamos WHERE id = p_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'RECLAMO_NO_PUEDE: No existe esa hoja.' USING ERRCODE = 'no_data_found'; END IF;
    IF r.estado = 'respondido' THEN
        RAISE EXCEPTION 'RECLAMO_NO_PUEDE: Esta hoja ya se respondió el %.', to_char(r.respondido_en AT TIME ZONE 'America/Lima', 'DD/MM/YYYY')
            USING ERRCODE = 'check_violation';
    END IF;
    UPDATE reclamos SET estado = 'respondido', respuesta = trim(p_texto), respondido_en = now(), respondido_por = ctx_usuario()
     WHERE id = p_id;
    RETURN jsonb_build_object('hoja', r.hoja, 'correo', r.correo, 'nombre', r.nombre, 'tipo', r.tipo, 'proveedor', r.proveedor);
END $$;

DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY['reclamo_registrar(jsonb, inet)', 'reclamos_lista()', 'reclamo_responder(uuid, text)'] LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
    END LOOP;
END $$;

INSERT INTO permisos (codigo, area, descripcion) VALUES
    ('plataforma.reclamos', 'plataforma', 'Libro de Reclamaciones: ver las hojas y responderlas')
ON CONFLICT (codigo) DO NOTHING;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('078_libro_reclamaciones')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '078: Libro de Reclamaciones virtual (hojas numeradas, 15 días hábiles para responder). Nada se borró.';
END $$;