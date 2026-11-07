-- =============================================================================
-- 076 — COBROS, SEGUNDA PARTE (e213, 1 nov 2026)
--
-- Lo que pasa solo, sin que nadie haga clic:
--
--   1. EL COMPROBANTE DE CADA PAGO. Cuando un pago queda «pagado» (tarjeta,
--      transferencia o el aviso de Culqi), «Mi empresa» le emite al cliente
--      factura (si dio RUC) o boleta (si dio DNI). Lo hace la tarea de fondo
--      de la API (CobrosAutomaticos), que reintenta si el servicio no responde.
--   2. EL COBRO DE CADA MES. El día del cobro (al terminar el mes gratis o lo
--      ya pagado) se cobra a la tarjeta principal; si falla, se reintenta los
--      días del panel (3 y 5), luego vienen los días de gracia y al final la
--      cuenta no puede emitir hasta pagar. Nada se borra.
--   3. LOS AVISOS POR CORREO: el cobro que viene, el pago recibido, la tarjeta
--      que no pasó, la gracia y el «no puedes emitir». Cada uno una sola vez
--      (cobros_avisos).
--
-- Solo crea. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

-- -----------------------------------------------------------------------------
-- 1. EL COMPROBANTE DE CADA PAGO
-- -----------------------------------------------------------------------------
ALTER TABLE cobros ADD COLUMN IF NOT EXISTS comprobante_sunat      text;
ALTER TABLE cobros ADD COLUMN IF NOT EXISTS comprobante_error      text;
ALTER TABLE cobros ADD COLUMN IF NOT EXISTS comprobante_intentos   integer NOT NULL DEFAULT 0;
ALTER TABLE cobros ADD COLUMN IF NOT EXISTS comprobante_intento_en timestamptz;
ALTER TABLE cobros ADD COLUMN IF NOT EXISTS comprobante_empresa    uuid REFERENCES empresas(id);
ALTER TABLE cobros ADD COLUMN IF NOT EXISTS comprobante_serie      text;
ALTER TABLE cobros ADD COLUMN IF NOT EXISTS automatico             boolean NOT NULL DEFAULT false;

-- 'rechazado': SUNAT lo rechazó (gastó su número; se revisa a mano).
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_cobros_comp_estado')
       AND NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_cobros_comp_estado2') THEN
        ALTER TABLE cobros DROP CONSTRAINT ck_cobros_comp_estado;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_cobros_comp_estado2') THEN
        ALTER TABLE cobros ADD CONSTRAINT ck_cobros_comp_estado2
            CHECK (comprobante_estado IN ('pendiente', 'emitido', 'error', 'rechazado', 'no_aplica'));
    END IF;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS ux_cobros_comprobante ON cobros (comprobante_documento) WHERE comprobante_documento IS NOT NULL;

-- -----------------------------------------------------------------------------
-- 2. LOS AVISOS QUE YA SE MANDARON (uno por cuenta, tipo y clave)
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS cobros_avisos (
    id         uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    cuenta_id  uuid        NOT NULL REFERENCES cuentas(id),
    tipo       text        NOT NULL,
    clave      text        NOT NULL,
    correo     text,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,
    CONSTRAINT ck_cobros_avisos_tipo CHECK (tipo IN ('previo', 'pagado', 'rechazado', 'gracia', 'no_emite'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_cobros_avisos ON cobros_avisos (cuenta_id, tipo, clave);

DO $$ BEGIN
    ALTER TABLE cobros_avisos ENABLE ROW LEVEL SECURITY;
    ALTER TABLE cobros_avisos FORCE ROW LEVEL SECURITY;
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'cobros_avisos' AND policyname = 'p_cobros_avisos') THEN
        CREATE POLICY p_cobros_avisos ON cobros_avisos USING (ctx_es_super()) WITH CHECK (ctx_es_super());
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_cobros_avisos') THEN
        PERFORM vigilar_tabla('cobros_avisos');
    END IF;
END $$;
GRANT SELECT ON cobros_avisos TO plataforma_app;

-- -----------------------------------------------------------------------------
-- 3. LAS FUNCIONES DEL COMPROBANTE
-- -----------------------------------------------------------------------------

-- Los pagos que esperan su comprobante (con espera creciente entre intentos).
CREATE OR REPLACE FUNCTION cobros_comprobantes_pendientes() RETURNS SETOF uuid
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    RETURN QUERY
    SELECT x.id FROM cobros x, cobros_ajustes k
     WHERE k.id = 1 AND k.emisora_empresa IS NOT NULL
       AND x.estado = 'pagado' AND x.modo IN ('real', 'manual', 'prueba', 'simulado')
       AND x.comprobante_estado IN ('pendiente', 'error')
       AND x.comprobante_intentos < 10
       AND (x.comprobante_intento_en IS NULL
            OR x.comprobante_intento_en < now() - make_interval(mins => (power(2, LEAST(x.comprobante_intentos, 8)))::int))
     ORDER BY x.pagado_en
     LIMIT 50;
END $$;

-- Lo que hace falta para emitir el comprobante de un pago. NULL si todavía no
-- se puede (falta la serie). El tipo sale del documento del comprador.
CREATE OR REPLACE FUNCTION cobros_para_emitir(p_id uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    x cobros%ROWTYPE;
    k cobros_ajustes%ROWTYPE;
    c cuentas%ROWTYPE;
    v_tipo_doc text; v_doc text; v_nombre text; v_dir text; v_correo text;
    v_tipo text; v_serie text;
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    SELECT * INTO x FROM cobros WHERE id = p_id;
    SELECT * INTO k FROM cobros_ajustes WHERE id = 1;
    SELECT * INTO c FROM cuentas WHERE id = x.cuenta_id;
    IF x.id IS NULL OR k.emisora_empresa IS NULL THEN RETURN NULL; END IF;

    -- El comprador: la foto que se tomó al pagar; si no hay (un pago manual
    -- sin datos), los datos de hoy; si tampoco, el titular de la cuenta.
    v_tipo_doc := x.comprador_tipo_doc; v_doc := x.comprador_doc; v_nombre := x.comprador_nombre;
    v_dir := x.comprador_direccion; v_correo := x.comprador_correo;
    IF v_doc IS NULL THEN
        SELECT d.tipo_doc, d.numero, d.nombre, d.direccion, d.correo
          INTO v_tipo_doc, v_doc, v_nombre, v_dir, v_correo
          FROM cobros_datos d WHERE d.cuenta_id = x.cuenta_id;
    END IF;
    IF v_doc IS NULL AND c.titular_documento IS NOT NULL THEN
        v_tipo_doc := CASE WHEN length(c.titular_documento) = 11 THEN 'ruc' ELSE 'dni' END;
        v_doc := c.titular_documento; v_nombre := c.titular_nombre;
        v_correo := (SELECT u.correo FROM usuarios u WHERE u.cuenta_id = c.id AND u.nivel_cuenta = 'dueno' ORDER BY u.creado_en LIMIT 1);
    END IF;
    IF v_doc IS NULL THEN RETURN NULL; END IF;

    v_tipo  := CASE WHEN v_tipo_doc = 'ruc' THEN '01' ELSE '03' END;
    v_serie := CASE WHEN v_tipo = '01' THEN k.serie_factura ELSE k.serie_boleta END;
    IF v_serie IS NULL THEN RETURN NULL; END IF;

    RETURN jsonb_build_object(
        'id', x.id, 'empresa', k.emisora_empresa, 'tipo', v_tipo, 'serie', v_serie,
        'monto', x.monto, 'concepto', x.concepto,
        'fecha', (COALESCE(x.pagado_en, now()) AT TIME ZONE 'America/Lima')::date,
        'receptor', jsonb_build_object(
            'tipoDocumento', CASE WHEN v_tipo_doc = 'ruc' THEN '6' ELSE '1' END,
            'numeroDocumento', v_doc, 'razonSocial', v_nombre, 'direccion', COALESCE(v_dir, ''),
            'correo', CASE WHEN k.enviar_correo THEN v_correo END),
        'codigo', 'PLAN-' || to_char(x.numero, 'FM000000'));
END $$;

-- Lo que dijo el servicio al emitir.
CREATE OR REPLACE FUNCTION cobros_comprobante_guardar(p_id uuid, p_ok boolean, p_documento uuid, p_numero text,
                                                      p_sunat text, p_error text, p_empresa uuid, p_serie text)
RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    IF p_ok THEN
        UPDATE cobros SET comprobante_estado = 'emitido', comprobante_documento = p_documento,
                          comprobante_numero = p_numero, comprobante_sunat = p_sunat, comprobante_error = NULL,
                          comprobante_empresa = p_empresa, comprobante_serie = p_serie,
                          comprobante_intentos = comprobante_intentos + 1, comprobante_intento_en = now()
         WHERE id = p_id;
    ELSE
        UPDATE cobros SET comprobante_estado = 'error', comprobante_error = left(p_error, 300),
                          comprobante_intentos = comprobante_intentos + 1, comprobante_intento_en = now()
         WHERE id = p_id;
    END IF;
END $$;

-- El aviso del servicio de facturación sobre un comprobante de cobro (lo llama
-- el webhook del servicio, que no lo encuentra entre los documentos).
CREATE OR REPLACE FUNCTION cobros_comprobante_aviso(p_documento uuid, p_estado text, p_mensaje text) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    -- Solo el aviso que llega firmado para la empresa que lo emitió («Mi empresa»).
    UPDATE cobros
       SET comprobante_sunat = left(lower(p_estado), 40),
           comprobante_estado = CASE WHEN lower(p_estado) = 'rechazado' THEN 'rechazado' ELSE comprobante_estado END,
           comprobante_error = CASE WHEN lower(p_estado) = 'rechazado' THEN left(p_mensaje, 300) ELSE comprobante_error END
     WHERE comprobante_documento = p_documento
       AND (ctx_es_super() OR comprobante_empresa = ctx_empresa());
    RETURN FOUND;
END $$;

-- La lista de comprobantes de cobros, para «Mi empresa».
CREATE OR REPLACE FUNCTION cobros_comprobantes_lista() RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    RETURN (SELECT COALESCE(jsonb_agg(jsonb_build_object(
                'id', x.id, 'numero', x.numero, 'fecha', x.pagado_en, 'monto', x.monto, 'concepto', x.concepto,
                'modo', x.modo, 'comprador', x.comprador_nombre, 'documento', x.comprador_doc,
                'estado', x.comprobante_estado, 'sunat', x.comprobante_sunat, 'comprobante', x.comprobante_numero,
                'error', x.comprobante_error, 'intentos', x.comprobante_intentos)
              ORDER BY x.pagado_en DESC), '[]'::jsonb)
              FROM (SELECT * FROM cobros WHERE estado = 'pagado' ORDER BY pagado_en DESC LIMIT 300) x);
END $$;

CREATE OR REPLACE FUNCTION cobros_comprobante_reintentar(p_id uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    UPDATE cobros SET comprobante_estado = 'pendiente', comprobante_intentos = 0, comprobante_intento_en = NULL
     WHERE id = p_id AND estado = 'pagado' AND comprobante_estado IN ('error', 'pendiente');
    IF NOT FOUND THEN RAISE EXCEPTION 'COBRO_DATOS: Ese comprobante no se puede reintentar (ya se emitió o el pago no está pagado).'; END IF;
END $$;

-- -----------------------------------------------------------------------------
-- 4. EL COBRO DE CADA MES
-- -----------------------------------------------------------------------------

-- La fecha en que le toca pagar a una cuenta: el próximo cobro, o el fin de su mes gratis.
CREATE OR REPLACE FUNCTION cobros_fecha_cobro(p_cuenta uuid) RETURNS date
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE(c.pago_proximo, (c.prueba_hasta AT TIME ZONE 'America/Lima')::date)
      FROM cuentas c WHERE c.id = p_cuenta;
$$;

-- Las cuentas a las que hoy toca intentar cobrar. p_modo: el modo de verdad.
CREATE OR REPLACE FUNCTION cobros_por_cobrar(p_modo text) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE k cobros_ajustes%ROWTYPE;
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    SELECT * INTO k FROM cobros_ajustes WHERE id = 1;
    IF NOT planes_encendidos() OR NOT k.acepta_tarjeta OR p_modo NOT IN ('simulado', 'prueba', 'real') THEN
        RETURN '[]'::jsonb;
    END IF;
    RETURN (SELECT COALESCE(jsonb_agg(jsonb_build_object('cuenta', q.id, 'tarjeta', q.tarjeta, 'periodo', q.periodo, 'dias', q.dias)), '[]'::jsonb)
      FROM (
        SELECT c.id, t.id AS tarjeta,
               COALESCE((SELECT x.periodo FROM cobros x WHERE x.cuenta_id = c.id AND x.estado = 'pagado'
                          AND x.periodo IN ('mensual', 'anual') ORDER BY x.periodo_hasta DESC LIMIT 1), 'mensual') AS periodo,
               current_date - cobros_fecha_cobro(c.id) AS dias
          FROM cuentas c
          JOIN cobros_tarjetas t ON t.cuenta_id = c.id AND t.modo = p_modo AND t.principal AND t.quitada_en IS NULL
         WHERE c.activo AND c.estado = 'activa' AND NOT c.acceso_total
           AND cobros_fecha_cobro(c.id) <= current_date
           AND NOT EXISTS (SELECT 1 FROM cobros x WHERE x.cuenta_id = c.id AND x.estado = 'procesando')
           -- Un intento por día como mucho.
           AND NOT EXISTS (SELECT 1 FROM cobros x WHERE x.cuenta_id = c.id AND x.automatico
                              AND (x.creado_en AT TIME ZONE 'America/Lima')::date = current_date)
           -- El día del cobro, los días de reintento, o si nunca se intentó en este periodo.
           AND ((current_date - cobros_fecha_cobro(c.id)) = 0
                OR (current_date - cobros_fecha_cobro(c.id)) = ANY (k.reintento_dias)
                OR NOT EXISTS (SELECT 1 FROM cobros x WHERE x.cuenta_id = c.id AND x.automatico
                                  AND x.periodo_desde = cobros_fecha_cobro(c.id)))
           -- Pasada la gracia ya no se insiste solo: paga cuando entre.
           AND (current_date - cobros_fecha_cobro(c.id)) <= COALESCE((SELECT max(d) FROM unnest(k.reintento_dias) d), 0) + k.gracia_dias
        LIMIT 200) q);
END $$;

-- Empezar el cobro automático de una cuenta (como cobros_preparar, sin sesión).
CREATE OR REPLACE FUNCTION cobros_preparar_auto(p_cuenta uuid, p_modo text, p_periodo text, p_tarjeta uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    d cobros_datos%ROWTYPE;
    t cobros_tarjetas%ROWTYPE;
    m jsonb;
    v_id uuid;
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    SELECT * INTO t FROM cobros_tarjetas WHERE id = p_tarjeta AND cuenta_id = p_cuenta AND modo = p_modo AND quitada_en IS NULL;
    IF NOT FOUND THEN RETURN jsonb_build_object('nada', true, 'motivo', 'sin tarjeta'); END IF;
    m := cobros_monto(p_cuenta, CASE WHEN p_periodo = 'anual' THEN 'anual' ELSE 'mensual' END, p_modo IN ('simulado', 'prueba'));
    IF (m->>'nada')::boolean THEN RETURN m; END IF;
    SELECT * INTO d FROM cobros_datos WHERE cuenta_id = p_cuenta;

    INSERT INTO cobros (cuenta_id, modo, medio, tarjeta_id, monto, concepto, periodo, periodo_desde, periodo_hasta, estado,
                        comprador_tipo_doc, comprador_doc, comprador_nombre, comprador_direccion, comprador_correo,
                        comprobante_tipo, automatico)
    VALUES (p_cuenta, p_modo, 'tarjeta', t.id, (m->>'monto')::numeric, m->>'concepto', m->>'periodo',
            (m->>'desde')::date, (m->>'hasta')::date, 'procesando',
            d.tipo_doc, d.numero, d.nombre, d.direccion, d.correo,
            CASE d.tipo_doc WHEN 'ruc' THEN '01' WHEN 'dni' THEN '03' END, true)
    RETURNING id INTO v_id;
    RETURN jsonb_build_object('nada', false, 'id', v_id, 'cuenta', p_cuenta, 'monto', (m->>'monto')::numeric,
        'concepto', m->>'concepto', 'tarjeta', t.tarjeta_ref,
        'correo', COALESCE(d.correo, (SELECT u.correo FROM usuarios u WHERE u.cuenta_id = p_cuenta AND u.nivel_cuenta = 'dueno' ORDER BY u.creado_en LIMIT 1)));
END $$;

-- El estado de pago de cada cuenta según los días de atraso. Devuelve las que cambiaron.
CREATE OR REPLACE FUNCTION cobros_estados_actualizar() RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    k cobros_ajustes%ROWTYPE;
    v_reint integer;
    v_cambios jsonb := '[]'::jsonb;
    r record;
    v_nuevo text;
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    SELECT * INTO k FROM cobros_ajustes WHERE id = 1;
    v_reint := COALESCE((SELECT max(d) FROM unnest(k.reintento_dias) d), 0);

    FOR r IN
        SELECT c.id, c.pago_estado, cobros_fecha_cobro(c.id) AS fecha
          FROM cuentas c
         WHERE c.activo AND c.estado = 'activa'
    LOOP
        IF NOT planes_encendidos() OR r.fecha IS NULL OR r.fecha >= current_date
           OR (SELECT acceso_total FROM cuentas WHERE id = r.id)
           OR (cobros_monto(r.id, 'mensual', false)->>'nada')::boolean THEN
            v_nuevo := 'al_dia';
        ELSIF current_date - r.fecha <= v_reint THEN
            v_nuevo := 'reintento';
        ELSIF current_date - r.fecha <= v_reint + k.gracia_dias THEN
            v_nuevo := 'gracia';
        ELSE
            v_nuevo := 'no_emite';
        END IF;
        IF v_nuevo IS DISTINCT FROM r.pago_estado THEN
            UPDATE cuentas
               SET pago_estado = v_nuevo,
                   pago_falla_desde = CASE WHEN v_nuevo = 'al_dia' THEN NULL ELSE COALESCE(pago_falla_desde, r.fecha) END
             WHERE id = r.id;
            v_cambios := v_cambios || jsonb_build_object('cuenta', r.id, 'de', r.pago_estado, 'a', v_nuevo,
                                                         'fecha', r.fecha, 'gracia', r.fecha + v_reint + k.gracia_dias);
        END IF;
    END LOOP;
    RETURN v_cambios;
END $$;

-- ¿Esta empresa no puede emitir por falta de pago? (lo pregunta la API antes de emitir).
CREATE OR REPLACE FUNCTION cobros_bloquea_emision(p_empresa uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE((
        SELECT c.pago_estado = 'no_emite' AND NOT c.acceso_total AND planes_encendidos()
               AND (SELECT al_vencer FROM cobros_ajustes WHERE id = 1) = 'no_emite'
          FROM empresas e JOIN cuentas c ON c.id = e.cuenta_id
         WHERE e.id = p_empresa), false);
$$;

-- -----------------------------------------------------------------------------
-- 5. LOS AVISOS POR CORREO
-- -----------------------------------------------------------------------------

-- Lo que hay que avisar hoy (una sola vez cada cosa): el cobro que viene, los
-- pagos recibidos, las tarjetas que no pasaron.
CREATE OR REPLACE FUNCTION cobros_avisos_pendientes() RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE k cobros_ajustes%ROWTYPE;
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    SELECT * INTO k FROM cobros_ajustes WHERE id = 1;
    RETURN (SELECT COALESCE(jsonb_agg(a), '[]'::jsonb) FROM (
        -- El cobro que viene (aviso_dias antes).
        SELECT jsonb_build_object('tipo', 'previo', 'cuenta', c.id, 'clave', cobros_fecha_cobro(c.id)::text,
                   'fecha', cobros_fecha_cobro(c.id), 'monto', (cobros_monto(c.id, 'mensual', false)->>'monto')::numeric,
                   'conTarjeta', EXISTS (SELECT 1 FROM cobros_tarjetas t WHERE t.cuenta_id = c.id AND t.principal AND t.quitada_en IS NULL),
                   'nombre', COALESCE(c.titular_nombre, '')) AS a
          FROM cuentas c
         WHERE planes_encendidos() AND k.aviso_dias > 0 AND c.activo AND c.estado = 'activa' AND NOT c.acceso_total
           AND cobros_fecha_cobro(c.id) - current_date = k.aviso_dias
           AND NOT (cobros_monto(c.id, 'mensual', false)->>'nada')::boolean
        UNION ALL
        -- Los pagos recibidos de los últimos 3 días.
        SELECT jsonb_build_object('tipo', 'pagado', 'cuenta', x.cuenta_id, 'clave', x.id::text, 'monto', x.monto,
                   'concepto', x.concepto, 'numero', x.numero, 'hasta', x.periodo_hasta, 'modo', x.modo)
          FROM cobros x
         WHERE x.estado = 'pagado' AND x.pagado_en > now() - interval '3 days'
        UNION ALL
        -- Las tarjetas que no pasaron (cobros automáticos) de los últimos 3 días.
        SELECT jsonb_build_object('tipo', 'rechazado', 'cuenta', x.cuenta_id, 'clave', x.id::text, 'monto', x.monto,
                   'error', x.error_mensaje, 'reintentos', to_jsonb(k.reintento_dias))
          FROM cobros x
         WHERE x.estado = 'rechazado' AND x.automatico AND x.creado_en > now() - interval '3 days'
    ) s WHERE NOT EXISTS (SELECT 1 FROM cobros_avisos v WHERE v.cuenta_id = (s.a->>'cuenta')::uuid
                                                      AND v.tipo = s.a->>'tipo' AND v.clave = s.a->>'clave'));
END $$;

-- A quién se le escribe por una cuenta: el correo del comprobante, o el del dueño.
CREATE OR REPLACE FUNCTION cobros_correo_cuenta(p_cuenta uuid) RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE((SELECT d.correo FROM cobros_datos d WHERE d.cuenta_id = p_cuenta),
                    (SELECT u.correo FROM usuarios u WHERE u.cuenta_id = p_cuenta AND u.nivel_cuenta = 'dueno' AND u.activo
                      ORDER BY u.creado_en LIMIT 1));
$$;

-- Anotar que un aviso ya salió. false si ya estaba (otra copia lo mandó).
CREATE OR REPLACE FUNCTION cobros_aviso_anotar(p_cuenta uuid, p_tipo text, p_clave text, p_correo text) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    INSERT INTO cobros_avisos (cuenta_id, tipo, clave, correo) VALUES (p_cuenta, p_tipo, p_clave, p_correo)
    ON CONFLICT (cuenta_id, tipo, clave) DO NOTHING;
    RETURN FOUND;
END $$;

-- -----------------------------------------------------------------------------
-- 6. DUEÑO Y PERMISOS
-- -----------------------------------------------------------------------------
DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY[
        'cobros_comprobantes_pendientes()', 'cobros_para_emitir(uuid)',
        'cobros_comprobante_guardar(uuid, boolean, uuid, text, text, text, uuid, text)',
        'cobros_comprobante_aviso(uuid, text, text)', 'cobros_comprobantes_lista()', 'cobros_comprobante_reintentar(uuid)',
        'cobros_fecha_cobro(uuid)', 'cobros_por_cobrar(text)', 'cobros_preparar_auto(uuid, text, text, uuid)',
        'cobros_estados_actualizar()', 'cobros_bloquea_emision(uuid)', 'cobros_avisos_pendientes()',
        'cobros_correo_cuenta(uuid)', 'cobros_aviso_anotar(uuid, text, text, text)']
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
    END LOOP;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('076_cobros_automaticos')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '076: cobros automáticos (comprobante de cada pago, cobro de cada mes con reintentos y gracia, avisos por correo). Nada se borró.';
END $$;