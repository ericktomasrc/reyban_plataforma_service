-- =============================================================================
-- 091 — VENTAS (super admin) Y PAGOS Y COMPROBANTES (cuentas) (e274, 22 nov 2026)
--
-- Solo lectura: no cambia ninguna tabla ni ningún dato. Funciones nuevas sobre
-- `cobros` (los pagos de los planes):
--
--   · ventas_resumen / ventas_lista / ventas_reportes (solo la plataforma):
--     lo cobrado, quién paga, qué plan, rechazados y atrasados. Por defecto
--     solo dinero de verdad (modo real y pagos a mano); con p_pruebas = true
--     también los de prueba (QA).
--   · cobros_mis_pagos (dueño o administrador de la cuenta): lo que la cuenta
--     le pagó a Reyban, con su comprobante y sus gastos por mes.
--   · cobros_comprobante_mio: el comprobante de UN pago, si es de tu cuenta
--     (o eres la plataforma), para bajar su PDF o XML.
--
-- El plan de cada pago sale de su concepto («Plan Básico — …»): no se guardó
-- aparte. Si el texto de la plantilla se cambió, sale «Otro».
-- =============================================================================

BEGIN;

-- El plan, legible, a partir del concepto del pago.
CREATE OR REPLACE FUNCTION cobros_plan_nombre(p_concepto text, p_tipo_cuenta text) RETURNS text
LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE
        WHEN p_tipo_cuenta = 'profesional' OR p_concepto ILIKE 'Plan Cartera%' THEN 'Cartera'
        ELSE COALESCE(NULLIF(btrim(substring(p_concepto FROM '^Plan (.+?) — ')), ''), 'Otro')
    END
$$;

-- Los pagos a mano dicen «Plan de la cuenta»: ahí vale el plan de hoy de sus
-- empresas (Básico, Pro…).
CREATE OR REPLACE FUNCTION cobros_plan_de(p_concepto text, p_tipo_cuenta text, p_cuenta uuid) RETURNS text
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v text := cobros_plan_nombre(p_concepto, p_tipo_cuenta);
BEGIN
    IF v IN ('de la cuenta', 'Otro') THEN
        SELECT string_agg(DISTINCT p.nombre, ' + ') INTO v
          FROM empresas e JOIN planes p ON p.codigo = planes_plan_cobro(e.id)
         WHERE e.cuenta_id = p_cuenta AND e.activo;
    END IF;
    RETURN COALESCE(v, 'Otro');
END $$;

-- Lo que vale un pago por mes (para el ingreso fijo): anual / 12; «otro» por
-- los meses que cubre.
CREATE OR REPLACE FUNCTION cobros_mensual(p_monto numeric, p_periodo text, p_desde date, p_hasta date) RETURNS numeric
LANGUAGE sql IMMUTABLE AS $$
    SELECT round(CASE p_periodo
        WHEN 'mensual' THEN p_monto
        WHEN 'anual'   THEN p_monto / 12
        ELSE p_monto / GREATEST(1, round((p_hasta - p_desde + 1) / 30.0))
    END, 2)
$$;

-- ¿Este pago cuenta como venta? Dinero de verdad, o también pruebas.
CREATE OR REPLACE FUNCTION cobros_es_venta(p_modo text, p_pruebas boolean) RETURNS boolean
LANGUAGE sql IMMUTABLE AS $$
    SELECT p_modo IN ('real', 'manual') OR (COALESCE(p_pruebas, false) AND p_modo IN ('prueba', 'simulado'))
$$;

-- -----------------------------------------------------------------------------
-- RESUMEN (la franja de arriba)
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION ventas_resumen(p_pruebas boolean) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_hoy  date := hoy_peru();
    v_mes  date := date_trunc('month', hoy_peru())::date;
    r      jsonb;
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;

    WITH pagos AS (
        SELECT x.*, (x.pagado_en AT TIME ZONE 'America/Lima')::date AS dia
          FROM cobros x
         WHERE x.estado = 'pagado' AND cobros_es_venta(x.modo, p_pruebas)
    ),
    -- El último pago de cada cuenta (lo que paga al mes).
    ultimo AS (
        SELECT DISTINCT ON (p.cuenta_id) p.cuenta_id, cobros_mensual(p.monto, p.periodo, p.periodo_desde, p.periodo_hasta) AS mensual,
               p.periodo_hasta
          FROM pagos p ORDER BY p.cuenta_id, p.pagado_en DESC
    )
    SELECT jsonb_build_object(
        'mes',        COALESCE((SELECT sum(monto) FROM pagos WHERE dia >= v_mes), 0),
        'mesPagos',   (SELECT count(*) FROM pagos WHERE dia >= v_mes),
        'hoy',        COALESCE((SELECT sum(monto) FROM pagos WHERE dia = v_hoy), 0),
        'hoyPagos',   (SELECT count(*) FROM pagos WHERE dia = v_hoy),
        'fijo',       COALESCE((SELECT sum(u.mensual) FROM ultimo u WHERE u.periodo_hasta >= v_hoy), 0),
        'clientes',   (SELECT count(*) FROM ultimo u WHERE u.periodo_hasta >= v_hoy),
        'semana',     COALESCE((SELECT sum(u.mensual) FROM cuentas c JOIN ultimo u ON u.cuenta_id = c.id
                                 WHERE c.pago_proximo BETWEEN v_hoy AND v_hoy + 7 AND NOT c.acceso_total), 0),
        'semanaCuentas', (SELECT count(*) FROM cuentas c
                           WHERE c.pago_proximo BETWEEN v_hoy AND v_hoy + 7 AND NOT c.acceso_total
                             AND EXISTS (SELECT 1 FROM ultimo u WHERE u.cuenta_id = c.id)),
        'atrasados',  (SELECT count(*) FROM cuentas c WHERE c.pago_estado <> 'al_dia' AND NOT c.acceso_total),
        'atrasadoMonto', COALESCE((SELECT sum(u.mensual) FROM cuentas c JOIN ultimo u ON u.cuenta_id = c.id
                                    WHERE c.pago_estado <> 'al_dia' AND NOT c.acceso_total), 0),
        'rechazadosMes', (SELECT count(*) FROM cobros x WHERE x.estado = 'rechazado' AND cobros_es_venta(x.modo, p_pruebas)
                                                         AND (x.creado_en AT TIME ZONE 'America/Lima')::date >= v_mes)
    ) INTO r;
    RETURN r;
END $$;

-- -----------------------------------------------------------------------------
-- LA LISTA (como Culqi), con filtros y páginas
--   p_estado: pagado | rechazado | devuelto | anulado | procesando | NULL
--   p_medio:  tarjeta | otro | NULL
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION ventas_lista(p_estado text, p_texto text, p_desde date, p_hasta date,
                                        p_plan text, p_medio text, p_pruebas boolean,
                                        p_pagina integer, p_por_pagina integer) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_t   text := '%' || lower(btrim(COALESCE(p_texto, ''))) || '%';
    v_por integer := LEAST(GREATEST(COALESCE(p_por_pagina, 20), 1), 5000);
    v_pag integer := GREATEST(COALESCE(p_pagina, 1), 1);
    r     jsonb;
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;

    WITH base AS (
        SELECT x.*, c.tipo AS tipo_cuenta,
               COALESCE(c.titular_nombre, (SELECT u.nombre FROM usuarios u WHERE u.cuenta_id = c.id AND u.nivel_cuenta = 'dueno'
                                            ORDER BY u.creado_en LIMIT 1)) AS cuenta_nombre,
               cobros_plan_de(x.concepto, c.tipo, c.id) AS plan,
               COALESCE(x.pagado_en, x.creado_en) AS fecha
          FROM cobros x JOIN cuentas c ON c.id = x.cuenta_id
         WHERE cobros_es_venta(x.modo, p_pruebas)
           AND (p_estado IS NULL OR p_estado = '' OR x.estado = p_estado)
           AND (p_desde IS NULL OR (COALESCE(x.pagado_en, x.creado_en) AT TIME ZONE 'America/Lima')::date >= p_desde)
           AND (p_hasta IS NULL OR (COALESCE(x.pagado_en, x.creado_en) AT TIME ZONE 'America/Lima')::date <= p_hasta)
           AND (p_medio IS NULL OR p_medio = '' OR (p_medio = 'tarjeta') = (x.medio = 'tarjeta'))
    ),
    filtrada AS (
        SELECT b.* FROM base b
         WHERE (p_plan IS NULL OR p_plan = '' OR b.plan = p_plan)
           AND (v_t = '%%'
                OR lower(COALESCE(b.cuenta_nombre, '')) LIKE v_t OR lower(COALESCE(b.comprador_nombre, '')) LIKE v_t
                OR COALESCE(b.comprador_doc, '') LIKE v_t OR lower(COALESCE(b.referencia, '')) LIKE v_t
                OR lower(COALESCE(b.comprobante_numero, '')) LIKE v_t
                OR b.numero::text = btrim(p_texto)
                OR EXISTS (SELECT 1 FROM empresas e WHERE e.cuenta_id = b.cuenta_id
                            AND (lower(COALESCE(e.razon_social, e.nombre, '')) LIKE v_t OR COALESCE(e.ruc, '') LIKE v_t)))
    )
    SELECT jsonb_build_object(
        'total', (SELECT count(*) FROM filtrada),
        'suma',  COALESCE((SELECT sum(monto) FROM filtrada WHERE estado = 'pagado'), 0),
        'filas', COALESCE((SELECT jsonb_agg(jsonb_build_object(
                    'id', f.id, 'numero', f.numero, 'fecha', f.fecha, 'cuentaId', f.cuenta_id,
                    'cuenta', f.cuenta_nombre, 'tipoCuenta', f.tipo_cuenta,
                    'empresa', (SELECT COALESCE(e.razon_social, e.nombre) FROM empresas e WHERE e.cuenta_id = f.cuenta_id AND e.activo
                                 ORDER BY e.creado_en LIMIT 1),
                    'empresas', (SELECT count(*) FROM empresas e WHERE e.cuenta_id = f.cuenta_id AND e.activo),
                    'plan', f.plan, 'periodo', f.periodo, 'concepto', f.concepto,
                    'medio', f.medio, 'modo', f.modo,
                    'tarjeta', (SELECT t.marca || ' •••• ' || t.ultimos4 FROM cobros_tarjetas t WHERE t.id = f.tarjeta_id),
                    'monto', f.monto, 'estado', f.estado, 'error', f.error_mensaje, 'referencia', f.referencia,
                    'comprobante', f.comprobante_estado, 'comprobanteNumero', f.comprobante_numero,
                    'comprador', f.comprador_nombre, 'documento', f.comprador_doc)
                  ORDER BY f.fecha DESC)
                  FROM (SELECT * FROM filtrada ORDER BY fecha DESC OFFSET (v_pag - 1) * v_por LIMIT v_por) f), '[]'::jsonb)
    ) INTO r;
    RETURN r;
END $$;

-- -----------------------------------------------------------------------------
-- REPORTES: por mes, por plan (este mes) y atrasados
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION ventas_reportes(p_meses integer, p_pruebas boolean) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_n   integer := LEAST(GREATEST(COALESCE(p_meses, 6), 1), 24);
    v_mes date := date_trunc('month', hoy_peru())::date;
    r     jsonb;
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;

    WITH pagos AS (
        SELECT x.*, c.tipo AS tipo_cuenta, date_trunc('month', x.pagado_en AT TIME ZONE 'America/Lima')::date AS mes
          FROM cobros x JOIN cuentas c ON c.id = x.cuenta_id
         WHERE x.estado = 'pagado' AND cobros_es_venta(x.modo, p_pruebas)
    ),
    meses AS (
        SELECT (v_mes - make_interval(months => g))::date AS mes FROM generate_series(v_n - 1, 0, -1) g
    )
    SELECT jsonb_build_object(
        'meses', (SELECT jsonb_agg(jsonb_build_object('mes', to_char(m.mes, 'YYYY-MM'),
                                    'cobrado', COALESCE((SELECT sum(p.monto) FROM pagos p WHERE p.mes = m.mes), 0),
                                    'pagos', (SELECT count(*) FROM pagos p WHERE p.mes = m.mes)) ORDER BY m.mes)
                    FROM meses m),
        'planes', COALESCE((SELECT jsonb_agg(jsonb_build_object('plan', q.plan, 'clientes', q.clientes, 'cobrado', q.cobrado)
                                             ORDER BY q.cobrado DESC)
                    FROM (SELECT cobros_plan_de(p.concepto, p.tipo_cuenta, p.cuenta_id) AS plan,
                                 count(DISTINCT p.cuenta_id) AS clientes, sum(p.monto) AS cobrado
                            FROM pagos p WHERE p.mes = v_mes GROUP BY 1) q), '[]'::jsonb),
        'atrasados', COALESCE((SELECT jsonb_agg(jsonb_build_object(
                    'cuentaId', c.id,
                    'cuenta', COALESCE(c.titular_nombre, (SELECT u.nombre FROM usuarios u WHERE u.cuenta_id = c.id AND u.nivel_cuenta = 'dueno'
                                                           ORDER BY u.creado_en LIMIT 1)),
                    'estado', c.pago_estado, 'intentos', c.pago_intentos, 'proximo', c.pago_proximo, 'fallaDesde', c.pago_falla_desde,
                    'plan', (SELECT cobros_plan_de(x.concepto, c.tipo, c.id) FROM cobros x WHERE x.cuenta_id = c.id
                              ORDER BY x.creado_en DESC LIMIT 1),
                    'tarjeta', (SELECT t.marca || ' •••• ' || t.ultimos4 FROM cobros_tarjetas t
                                 WHERE t.cuenta_id = c.id AND t.activo AND t.principal LIMIT 1),
                    'debe', (SELECT cobros_mensual(x.monto, x.periodo, x.periodo_desde, x.periodo_hasta) FROM cobros x
                              WHERE x.cuenta_id = c.id AND x.estado IN ('pagado', 'rechazado') ORDER BY x.creado_en DESC LIMIT 1),
                    'error', (SELECT x.error_mensaje FROM cobros x WHERE x.cuenta_id = c.id AND x.estado = 'rechazado'
                               ORDER BY x.creado_en DESC LIMIT 1))
                  ORDER BY CASE c.pago_estado WHEN 'no_emite' THEN 0 WHEN 'gracia' THEN 1 ELSE 2 END, c.pago_falla_desde)
                  FROM cuentas c WHERE c.pago_estado <> 'al_dia' AND NOT c.acceso_total), '[]'::jsonb)
    ) INTO r;
    RETURN r;
END $$;

-- -----------------------------------------------------------------------------
-- LA CUENTA: lo que le pagó a Reyban (dueño o administrador de la cuenta)
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION cobros_mis_pagos(p_anio integer) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := cobros_mi_cuenta();
    v_hoy    date := hoy_peru();
    v_anio   integer := COALESCE(p_anio, extract(year FROM hoy_peru())::integer);
    c        cuentas%ROWTYPE;
    r        jsonb;
BEGIN
    IF v_cuenta IS NULL THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo el dueño o un administrador de la cuenta ve sus pagos.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    SELECT * INTO c FROM cuentas WHERE id = v_cuenta;

    WITH mios AS (
        SELECT x.*, COALESCE(x.pagado_en, x.creado_en) AS fecha,
               extract(year FROM COALESCE(x.pagado_en, x.creado_en) AT TIME ZONE 'America/Lima')::integer AS anio,
               extract(month FROM COALESCE(x.pagado_en, x.creado_en) AT TIME ZONE 'America/Lima')::integer AS mes
          FROM cobros x WHERE x.cuenta_id = v_cuenta
    ),
    meses AS (
        SELECT g AS mes FROM generate_series(1, CASE WHEN v_anio = extract(year FROM v_hoy) THEN extract(month FROM v_hoy)::integer ELSE 12 END) g
    )
    SELECT jsonb_build_object(
        'anio', v_anio,
        'anios', COALESCE((SELECT jsonb_agg(a ORDER BY a DESC) FROM (SELECT DISTINCT anio AS a FROM mios
                            UNION SELECT extract(year FROM v_hoy)::integer) z), '[]'::jsonb),
        'pagadoAnio', COALESCE((SELECT sum(monto) FROM mios WHERE estado = 'pagado' AND anio = v_anio), 0),
        'pagosAnio',  (SELECT count(*) FROM mios WHERE estado = 'pagado' AND anio = v_anio),
        'pagadoMes',  COALESCE((SELECT sum(monto) FROM mios WHERE estado = 'pagado' AND anio = extract(year FROM v_hoy)
                                                      AND mes = extract(month FROM v_hoy)), 0),
        'plan', (SELECT cobros_plan_de(x.concepto, c.tipo, c.id) FROM mios x WHERE x.estado = 'pagado' ORDER BY x.fecha DESC LIMIT 1),
        'planMensual', (SELECT cobros_mensual(x.monto, x.periodo, x.periodo_desde, x.periodo_hasta) FROM mios x
                         WHERE x.estado = 'pagado' ORDER BY x.fecha DESC LIMIT 1),
        'proximo', c.pago_proximo,
        'estadoPago', c.pago_estado,
        'tarjeta', (SELECT t.marca || ' •••• ' || t.ultimos4 FROM cobros_tarjetas t
                     WHERE t.cuenta_id = v_cuenta AND t.activo AND t.principal LIMIT 1),
        'filas', COALESCE((SELECT jsonb_agg(jsonb_build_object(
                    'id', x.id, 'numero', x.numero, 'fecha', x.fecha, 'concepto', x.concepto,
                    'desde', x.periodo_desde, 'hasta', x.periodo_hasta, 'medio', x.medio, 'modo', x.modo,
                    'tarjeta', (SELECT t.marca || ' •••• ' || t.ultimos4 FROM cobros_tarjetas t WHERE t.id = x.tarjeta_id),
                    'monto', x.monto, 'estado', x.estado, 'error', x.error_mensaje,
                    'comprobanteNumero', x.comprobante_numero, 'comprobanteTipo', x.comprobante_tipo,
                    'comprobanteListo', x.comprobante_estado = 'emitido' AND x.comprobante_documento IS NOT NULL)
                  ORDER BY x.fecha DESC) FROM mios x WHERE x.anio = v_anio), '[]'::jsonb),
        'porMes', (SELECT jsonb_agg(jsonb_build_object(
                    'mes', m.mes,
                    'pagos', (SELECT count(*) FROM mios x WHERE x.estado = 'pagado' AND x.anio = v_anio AND x.mes = m.mes),
                    'total', COALESCE((SELECT sum(x.monto) FROM mios x WHERE x.estado = 'pagado' AND x.anio = v_anio AND x.mes = m.mes), 0))
                  ORDER BY m.mes DESC) FROM meses m)
    ) INTO r;
    RETURN r;
END $$;

-- El comprobante de un pago, para bajarlo: solo si el pago es de tu cuenta
-- (dueño o administrador) o eres la plataforma.
CREATE OR REPLACE FUNCTION cobros_comprobante_mio(p_cobro uuid) RETURNS TABLE (empresa uuid, documento uuid, tipo text, numero text)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    RETURN QUERY
    SELECT x.comprobante_empresa, x.comprobante_documento, x.comprobante_tipo, x.comprobante_numero
      FROM cobros x
     WHERE x.id = p_cobro
       AND x.comprobante_estado = 'emitido' AND x.comprobante_documento IS NOT NULL AND x.comprobante_empresa IS NOT NULL
       AND (ctx_es_super() OR x.cuenta_id = cobros_mi_cuenta());
END $$;

DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY[
        'cobros_plan_nombre(text, text)', 'cobros_plan_de(text, text, uuid)', 'cobros_mensual(numeric, text, date, date)', 'cobros_es_venta(text, boolean)',
        'ventas_resumen(boolean)', 'ventas_lista(text, text, date, date, text, text, boolean, integer, integer)',
        'ventas_reportes(integer, boolean)', 'cobros_mis_pagos(integer)', 'cobros_comprobante_mio(uuid)'] LOOP
        EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
    END LOOP;
    -- Las «puertas» (SECURITY DEFINER) son de plataforma_puertas, como todas (027).
    FOREACH f IN ARRAY ARRAY[
        'cobros_plan_de(text, text, uuid)', 'ventas_resumen(boolean)',
        'ventas_lista(text, text, date, date, text, text, boolean, integer, integer)',
        'ventas_reportes(integer, boolean)', 'cobros_mis_pagos(integer)', 'cobros_comprobante_mio(uuid)'] LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
    END LOOP;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('091_ventas')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '091: ventas (plataforma) y pagos y comprobantes (cuentas). Solo lectura: nada cambió ni se borró.';
END $$;