-- =============================================================================
-- 086 — ARREGLOS DE LA REVISIÓN QA DE COBROS Y PLANES (8 nov 2026, e237)
--
-- Una revisión independiente encontró casos en que se cobraba de más o de
-- menos. Todos se arreglan aquí (y se comprueban en verificar.sql, 83):
--
--   A1  La Cartera paga por las empresas que USA (nunca menos), y no se pueden
--       poner menos cupos que las empresas activas.
--   A2  Un paquete o una diferencia de plan NO cuentan como «mes pagado»: no
--       borran la deuda ni desbloquean (solo los periodos de suscripción).
--   A3  Dos clics o dos pestañas en «Pagar», «Subir de plan» o «Comprar
--       paquete»: el segundo espera al primero y no vuelve a cobrar.
--   A4  Bajar de plan, pagar el mes siguiente barato y «deshacer»: ya no se
--       puede deshacer si ese mes ya se pagó con el plan menor.
--   A5  Los cupos de la Cartera pagados se guardan POR PERIODO: bajar, pagar
--       por adelantado y volver a subir cobra la diferencia de ese periodo.
--   A6  La base trabaja en hora de Lima (las fechas y los reintentos de cobro
--       ya no se adelantan 5 horas en un servidor en UTC).
--   A7  Una subida de plan cuyo pago quedó sin respuesta de Culqi no se puede
--       repetir hasta que se aclare (no se cobra dos veces).
--   A8  Dos clics en «Subir» sin tarjeta: una sola diferencia pendiente.
--   A9  Un cobro automático sin respuesta ya no se queda «procesando» para
--       siempre; esa cuenta se reintenta recién a las 24 horas.
--   A10 Comprar un paquete o pagar una diferencia ya no deshace la cancelación.
--   A11 Un pago manual de varios meses (3, 6, 24…) cuenta como periodo pagado.
--   A12 Con un periodo pagado por adelantado en el mes gratis (o en cortesía),
--       subir de plan cobra la diferencia de ese periodo.
--   A13 Una devolución de Culqi recalcula la cuenta y suelta los pendientes.
--   A14 Si SUNAT rechaza un comprobante que gastó saldo del paquete, el saldo vuelve.
--   A15 Los 12 meses de lanzamiento se cuentan desde el primer pago de
--       suscripción (no desde un paquete).
--
-- Se puede correr dos veces. Nada se borra.
-- =============================================================================

-- A6: hora de Lima para toda la base (las sesiones nuevas la toman).
DO $$
BEGIN
    EXECUTE format('ALTER DATABASE %I SET timezone = %L', current_database(), 'America/Lima');
EXCEPTION WHEN insufficient_privilege THEN
    RAISE NOTICE '086: no se pudo fijar la hora de Lima en la base (hace falta el dueño). Pídela con: ALTER DATABASE % SET timezone = ''America/Lima'';', current_database();
END $$;

BEGIN;
SET LOCAL timezone = 'America/Lima';

-- -----------------------------------------------------------------------------
-- 0. QUÉ COBRO ES UN PERIODO DE SUSCRIPCIÓN
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION cobros_es_suscripcion(p_periodo text, p_plan_empresa uuid, p_paquete_empresa uuid) RETURNS boolean
LANGUAGE sql IMMUTABLE AS $$
    SELECT p_periodo IN ('mensual', 'anual')
        OR (p_periodo = 'otro' AND p_plan_empresa IS NULL AND p_paquete_empresa IS NULL);
$$;
COMMENT ON FUNCTION cobros_es_suscripcion(text, uuid, uuid) IS
    '086: mensual, anual o un pago manual de varios meses. NO: diferencias de plan ni paquetes.';

-- Cuántos «meses de precio» cubre un periodo (para prorratear la diferencia).
CREATE OR REPLACE FUNCTION cobros_factor_meses(p_periodo text, p_desde date, p_hasta date) RETURNS numeric
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT CASE p_periodo
             WHEN 'mensual' THEN 1
             WHEN 'anual'   THEN 12 - (SELECT anual_meses_gratis FROM plataforma_ajustes WHERE id = 1)
             ELSE GREATEST(round((p_hasta - p_desde + 1) / 30.4375), 1) END::numeric;
$$;

-- A5: los cupos de la Cartera que paga CADA cobro de suscripción.
ALTER TABLE cobros ADD COLUMN IF NOT EXISTS cupos_cubiertos integer;
COMMENT ON COLUMN cobros.cupos_cubiertos IS '086: empresas de la Cartera pagadas en este periodo (sube si se paga una diferencia).';

-- A14: el comprobante gastó una unidad del paquete.
ALTER TABLE documentos ADD COLUMN IF NOT EXISTS gasto_paquete boolean NOT NULL DEFAULT false;


-- A2
CREATE OR REPLACE FUNCTION public.cobros_aplicar_cuenta(p_cuenta uuid)
 RETURNS void
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public'
AS $function$
DECLARE v_hasta date;
BEGIN
    SELECT max(periodo_hasta) INTO v_hasta FROM cobros
     WHERE cuenta_id = p_cuenta AND estado = 'pagado'
       AND cobros_es_suscripcion(periodo, plan_empresa, paquete_empresa);   -- 086 (A2)
    UPDATE cuentas
       SET pago_proximo = CASE WHEN v_hasta IS NULL THEN pago_proximo ELSE v_hasta + 1 END,
           pago_estado = CASE WHEN v_hasta IS NOT NULL AND v_hasta >= current_date THEN 'al_dia' ELSE pago_estado END,
           pago_falla_desde = CASE WHEN v_hasta IS NOT NULL AND v_hasta >= current_date THEN NULL ELSE pago_falla_desde END,
           pago_intentos = CASE WHEN v_hasta IS NOT NULL AND v_hasta >= current_date THEN 0 ELSE pago_intentos END
     WHERE id = p_cuenta;
END $function$;

-- A10
CREATE OR REPLACE FUNCTION public.fn_cobros_reactiva()
 RETURNS trigger
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public'
AS $function$
BEGIN
    IF NEW.estado = 'pagado' AND (TG_OP = 'INSERT' OR OLD.estado IS DISTINCT FROM 'pagado')
       AND cobros_es_suscripcion(NEW.periodo, NEW.plan_empresa, NEW.paquete_empresa) THEN   -- 086 (A10)
        UPDATE cuentas SET cancelado_en = NULL, cancelado_motivo = NULL,
                           pago_estado = CASE WHEN pago_estado = 'cancelado' THEN 'al_dia' ELSE pago_estado END
         WHERE id = NEW.cuenta_id AND cancelado_en IS NOT NULL;
    END IF;
    RETURN NULL;
END $function$;

-- A15
CREATE OR REPLACE FUNCTION public.fn_cobros_lanzamiento()
 RETURNS trigger
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public'
AS $function$
DECLARE v_meses integer;
BEGIN
    IF NEW.estado = 'pagado' AND (TG_OP = 'INSERT' OR OLD.estado IS DISTINCT FROM 'pagado')
       AND cobros_es_suscripcion(NEW.periodo, NEW.plan_empresa, NEW.paquete_empresa)   -- 086 (A15)
       AND NOT EXISTS (SELECT 1 FROM cobros x WHERE x.cuenta_id = NEW.cuenta_id AND x.estado = 'pagado' AND x.id <> NEW.id
                          AND cobros_es_suscripcion(x.periodo, x.plan_empresa, x.paquete_empresa)) THEN
        SELECT lanzamiento_meses INTO v_meses FROM plataforma_ajustes WHERE id = 1;
        UPDATE cuentas
           SET lanzamiento_hasta = GREATEST(lanzamiento_hasta, (NEW.periodo_desde + make_interval(months => COALESCE(v_meses, 12)))::date - 1)
         WHERE id = NEW.cuenta_id AND lanzamiento_hasta IS NOT NULL;
    END IF;
    RETURN NULL;
END $function$;

-- A13 / A11
CREATE OR REPLACE FUNCTION public.fn_cobros_cambio_plan()
 RETURNS trigger
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public'
AS $function$
BEGIN
    IF NEW.estado = 'pagado' AND (TG_OP = 'INSERT' OR OLD.estado IS DISTINCT FROM 'pagado') THEN
        IF NEW.plan_nuevo IS NOT NULL THEN
            UPDATE empresas SET plan = NEW.plan_nuevo, plan_programado = NULL, plan_programado_desde = NULL
             WHERE id = NEW.plan_empresa AND planes_orden(plan) < planes_orden(NEW.plan_nuevo);
        END IF;
        IF cobros_es_suscripcion(NEW.periodo, NEW.plan_empresa, NEW.paquete_empresa) THEN   -- 086 (A11)
            UPDATE cobros_pendientes SET cobro_id = NEW.id, cobrado_en = now()
             WHERE cuenta_id = NEW.cuenta_id AND cobro_id IS NULL AND anulado_en IS NULL
               AND creado_en <= NEW.creado_en;
        END IF;
    ELSIF TG_OP = 'UPDATE' AND NEW.estado IN ('anulado', 'devuelto') AND OLD.estado = 'pagado' THEN   -- 086 (A13): también devuelto
        UPDATE cobros_pendientes SET cobro_id = NULL, cobrado_en = NULL WHERE cobro_id = NEW.id;
    END IF;
    RETURN NULL;
END $function$;

-- A13
CREATE OR REPLACE FUNCTION public.cobros_confirmar(p_id uuid, p_modo text, p_referencia text, p_estado text, p_monto numeric)
 RETURNS text
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public'
AS $function$
DECLARE
    x cobros%ROWTYPE;
BEGIN
    IF p_id IS NOT NULL THEN
        SELECT * INTO x FROM cobros WHERE id = p_id AND modo = p_modo FOR UPDATE;
    ELSE
        SELECT * INTO x FROM cobros WHERE referencia = p_referencia AND modo = p_modo FOR UPDATE;
    END IF;
    IF NOT FOUND THEN RETURN 'no_es_nuestro'; END IF;

    IF p_estado = 'pagado' THEN
        IF x.estado = 'pagado' THEN RETURN 'ya_estaba'; END IF;
        IF NOT (x.estado = 'procesando' OR (x.estado = 'rechazado' AND x.error_codigo = 'sin_respuesta')) THEN
            RETURN 'estado_' || x.estado;
        END IF;
        IF p_monto IS DISTINCT FROM x.monto THEN RETURN 'monto_distinto'; END IF;
        UPDATE cobros SET estado = 'pagado', pagado_en = now(), referencia = p_referencia,
                          error_codigo = NULL, error_mensaje = NULL
         WHERE id = x.id;
        PERFORM cobros_aplicar_cuenta(x.cuenta_id);
        RETURN 'pagado';
    ELSIF p_estado = 'devuelto' THEN
        IF x.estado = 'devuelto' THEN RETURN 'ya_estaba'; END IF;
        IF x.estado <> 'pagado' THEN RETURN 'estado_' || x.estado; END IF;
        UPDATE cobros SET estado = 'devuelto', notas = left(COALESCE(notas || ' · ', '') || 'Devuelto en Culqi', 500)
         WHERE id = x.id;
        PERFORM cobros_aplicar_cuenta(x.cuenta_id);   -- 086 (A13): el próximo cobro vuelve a su fecha
        RETURN 'devuelto';
    END IF;
    RETURN 'sin_cambio';
END $function$;

-- A1
CREATE OR REPLACE FUNCTION public.planes_resumen_cuenta(p_cuenta uuid)
 RETURNS jsonb
 LANGUAGE plpgsql
 STABLE SECURITY DEFINER
 SET search_path TO 'public'
AS $function$
DECLARE
    c        cuentas%ROWTYPE;
    a        plataforma_ajustes%ROWTYPE;
    v_oferta boolean;
    v_usados integer;
    v_n      integer;
    v_base   numeric := 0;
    v_baser  numeric := 0;
    v_extra  numeric := 0;
    v_extrar numeric := 0;
    v_total  numeric;
    v_estado text;
    v_estado_enc text;
    v_total_enc  numeric;
    v_aj     cuenta_ajustes%ROWTYPE;
    v_emp    jsonb;
    v_ajs    jsonb;
BEGIN
    IF NOT ctx_es_super()
       AND p_cuenta IS DISTINCT FROM (SELECT u.cuenta_id FROM usuarios u WHERE u.id = ctx_usuario()) THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: Esa cuenta no es tuya.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    SELECT * INTO c FROM cuentas WHERE id = p_cuenta;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT * INTO a FROM plataforma_ajustes WHERE id = 1;
    -- 079: la oferta de hoy, o la que la cuenta guarda por haber entrado en ella (12 meses).
    v_oferta := planes_en_oferta() OR COALESCE(c.lanzamiento_hasta >= current_date, false);

    SELECT COALESCE(jsonb_agg(jsonb_build_object(
               'id', e.id, 'nombre', e.nombre, 'ruc', e.ruc, 'plan', e.plan,
               'efectivo', plan_efectivo(e.id), 'efectivoEncendido', plan_efectivo_base(e.id),
               'uso', planes_uso(e.id),
               'pruebaHasta', e.prueba_hasta, 'pruebaMotivo', e.prueba_motivo,
               'esDelEstudio', e.es_del_estudio, 'pausada', e.pausada_en IS NOT NULL,
               'suspendida', e.suspendida_en IS NOT NULL,
               -- 083: el plan que empieza en el próximo cobro (bajó de plan).
               'planProgramado', e.plan_programado, 'planProgramadoDesde', e.plan_programado_desde)
             ORDER BY e.es_del_estudio DESC, e.nombre), '[]'::jsonb)
      INTO v_emp
      FROM empresas e WHERE e.cuenta_id = p_cuenta AND e.activo;

    SELECT COALESCE(jsonb_agg(jsonb_build_object(
               'id', x.id, 'tipo', x.tipo, 'plan', x.plan_codigo, 'meses', x.meses, 'precio', x.precio,
               'hasta', x.hasta, 'motivo', x.motivo, 'creadoEn', x.creado_en,
               'anuladoEn', x.anulado_en, 'anuladoMotivo', x.anulado_motivo,
               'vigente', x.anulado_en IS NULL AND x.hasta >= current_date)
             ORDER BY x.creado_en DESC), '[]'::jsonb)
      INTO v_ajs
      FROM cuenta_ajustes x WHERE x.cuenta_id = p_cuenta AND x.activo;

    -- Lo que costaría, sin tratos especiales.
    IF c.tipo = 'negocio' THEN
        SELECT COALESCE(sum(CASE WHEN v_oferta THEN COALESCE(p.precio_oferta, p.precio_regular) ELSE p.precio_regular END), 0),
               COALESCE(sum(p.precio_regular), 0)
          INTO v_base, v_baser
          FROM empresas e JOIN planes p ON p.codigo = planes_plan_cobro(e.id)
         WHERE e.cuenta_id = p_cuenta AND e.activo AND e.pausada_en IS NULL;
        v_usados := (SELECT count(*) FROM empresas e WHERE e.cuenta_id = p_cuenta AND e.activo AND e.pausada_en IS NULL);
        v_n := v_usados;
    ELSE
        v_usados := planes_cupos_usados(p_cuenta);
        v_n := GREATEST(COALESCE(c.cartera_cupos, 0), v_usados, a.cartera_minimo);   -- 086 (A1): nunca menos de las que usa
        v_base  := planes_precio_cartera(v_n, NOT v_oferta);
        v_baser := planes_precio_cartera(v_n, true);
        SELECT COALESCE(sum(CASE WHEN v_oferta THEN COALESCE(p.cartera_extra_oferta, p.cartera_extra_regular) ELSE p.cartera_extra_regular END), 0),
               COALESCE(sum(p.cartera_extra_regular), 0)
          INTO v_extra, v_extrar
          FROM empresas e JOIN planes p ON p.codigo = planes_plan_cobro(e.id)
         WHERE e.cuenta_id = p_cuenta AND e.activo AND e.pausada_en IS NULL;
    END IF;
    v_total := v_base + v_extra;

    -- El trato especial vigente que manda (el más reciente de cada tipo).
    v_estado := 'al_dia';
    IF c.acceso_total THEN
        v_estado := 'acceso_total'; v_total := 0;
    ELSIF c.prueba_hasta > now() THEN
        v_estado := 'prueba'; v_total := 0;
    ELSE
        SELECT * INTO v_aj FROM cuenta_ajustes x
         WHERE x.cuenta_id = p_cuenta AND x.activo AND x.anulado_en IS NULL AND x.hasta >= current_date
         ORDER BY CASE x.tipo WHEN 'ampliar_prueba' THEN 1 WHEN 'cortesia' THEN 2 WHEN 'meses_gratis' THEN 3
                              WHEN 'precio_especial' THEN 4 ELSE 5 END, x.creado_en DESC
         LIMIT 1;
        IF FOUND THEN
            CASE v_aj.tipo
                WHEN 'ampliar_prueba'  THEN v_estado := 'prueba';       v_total := 0;
                WHEN 'cortesia'        THEN v_estado := 'cortesia';     v_total := 0;
                WHEN 'meses_gratis'    THEN v_estado := 'meses_gratis'; v_total := 0;
                WHEN 'precio_especial' THEN v_estado := 'precio_especial'; v_total := v_aj.precio;
                ELSE NULL;   -- plan_mejor: paga lo suyo y ve el plan mejor
            END CASE;
        END IF;
    END IF;

    -- 070: lo de arriba es lo que pasaría con los planes encendidos. Apagados,
    -- nadie paga: se dice «apagado» y se guarda lo otro para enseñarlo.
    v_estado_enc := v_estado;
    v_total_enc  := v_total;
    IF NOT planes_encendidos() THEN
        v_estado := 'apagado'; v_total := 0;
    END IF;

    RETURN jsonb_build_object(
        'id', c.id, 'tipo', c.tipo, 'titular', c.titular_nombre, 'estadoCuenta', c.estado,
        'dueno', (SELECT jsonb_build_object('nombre', u.nombre, 'correo', u.correo)
                    FROM usuarios u WHERE u.cuenta_id = c.id AND u.nivel_cuenta = 'dueno' AND u.activo
                   ORDER BY u.creado_en LIMIT 1),
        'estadoEncendido', v_estado_enc, 'totalEncendido', v_total_enc,
        'creadaEn', c.creado_en, 'origen', c.origen,
        'encendidos', planes_encendidos(), 'enOferta', v_oferta, 'ofertaHasta', a.oferta_hasta,
        'lanzamientoHasta', c.lanzamiento_hasta, 'ofertaGeneral', planes_en_oferta(),
        'canceladoEn', c.cancelado_en, 'canceladoMotivo', c.cancelado_motivo, 'pagoEstado', c.pago_estado,
        'activoHasta', cobros_fecha_cobro(c.id) - 1,
        'planPrueba', COALESCE(a.plan_prueba, 'max'),
        'pruebaHasta', c.prueba_hasta, 'accesoTotal', c.acceso_total, 'accesoTotalMotivo', c.acceso_total_motivo,
        'cupos', c.cartera_cupos, 'usados', v_usados, 'cobradas', v_n, 'minimo', a.cartera_minimo,
        'empresas', v_emp, 'ajustes', v_ajs,
        -- 083: diferencias por subir de plan que se suman al próximo cobro.
        'pendientes', (SELECT COALESCE(sum(x.monto), 0) FROM cobros_pendientes x
                        WHERE x.cuenta_id = c.id AND x.cobro_id IS NULL AND x.anulado_en IS NULL),
        'estado', v_estado,
        'mensual', jsonb_build_object('base', v_base, 'extras', v_extra, 'total', v_total,
                                      'regular', v_baser + v_extrar,
                                      'anual', round(v_total * (12 - a.anual_meses_gratis), 2)));
END $function$;

-- A9 / A6
CREATE OR REPLACE FUNCTION public.cobros_por_cobrar(p_modo text)
 RETURNS jsonb
 LANGUAGE plpgsql
 STABLE SECURITY DEFINER
 SET search_path TO 'public'
AS $function$
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
           AND c.cancelado_en IS NULL            -- 081: canceló: no se le cobra más
           AND cobros_fecha_cobro(c.id) <= current_date
           AND NOT EXISTS (SELECT 1 FROM cobros x WHERE x.cuenta_id = c.id AND x.estado = 'procesando')
           -- 086 (A9): un cobro sin respuesta de Culqi: 24 horas sin reintentar (por si sí cobró).
           AND NOT EXISTS (SELECT 1 FROM cobros x WHERE x.cuenta_id = c.id AND x.estado = 'rechazado'
                              AND x.error_codigo = 'sin_respuesta' AND x.creado_en > now() - interval '24 hours')
           -- Un intento por día como mucho.
           AND NOT EXISTS (SELECT 1 FROM cobros x WHERE x.cuenta_id = c.id AND x.automatico
                              AND (x.creado_en AT TIME ZONE 'America/Lima')::date = (now() AT TIME ZONE 'America/Lima')::date)   -- 086 (A6)
           -- El día del cobro, los días de reintento, o si nunca se intentó en este periodo.
           AND ((current_date - cobros_fecha_cobro(c.id)) = 0
                OR (current_date - cobros_fecha_cobro(c.id)) = ANY (k.reintento_dias)
                OR NOT EXISTS (SELECT 1 FROM cobros x WHERE x.cuenta_id = c.id AND x.automatico
                                  AND x.periodo_desde = cobros_fecha_cobro(c.id)))
           -- Pasada la gracia ya no se insiste solo: paga cuando entre.
           AND (current_date - cobros_fecha_cobro(c.id)) <= COALESCE((SELECT max(d) FROM unnest(k.reintento_dias) d), 0) + k.gracia_dias
        LIMIT 200) q);
END $function$;

-- A3
CREATE OR REPLACE FUNCTION public.cobros_preparar(p_modo text, p_periodo text, p_tarjeta uuid)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public'
AS $function$
DECLARE
    v_cuenta uuid := cobros_mi_cuenta();
    k        cobros_ajustes%ROWTYPE;
    d        cobros_datos%ROWTYPE;
    t        cobros_tarjetas%ROWTYPE;
    m        jsonb;
    v_id     uuid;
BEGIN
    IF v_cuenta IS NULL THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo el dueño de la cuenta (o un colaborador administrador) paga.';
    END IF;
    -- 086 (A3): dos clics o dos pestañas: el segundo espera aquí al primero.
    PERFORM 1 FROM cuentas WHERE id = v_cuenta FOR UPDATE;
    IF EXISTS (SELECT 1 FROM cobros WHERE cuenta_id = v_cuenta AND estado = 'pagado' AND periodo = p_periodo
                  AND creado_en > now() - interval '2 minutes') THEN
        RAISE EXCEPTION 'COBRO_EN_CURSO: Acabas de pagar hace un momento. Revisa «Mis pagos» antes de volver a pagar.';
    END IF;
    SELECT * INTO k FROM cobros_ajustes WHERE id = 1;
    IF p_modo NOT IN ('simulado', 'prueba', 'real') OR NOT k.acepta_tarjeta THEN
        RAISE EXCEPTION 'COBRO_MODO: Los pagos con tarjeta están apagados por ahora.';
    END IF;
    SELECT * INTO d FROM cobros_datos WHERE cuenta_id = v_cuenta;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'COBRO_DATOS: Primero completa los datos de tu comprobante (RUC o DNI).';
    END IF;
    SELECT * INTO t FROM cobros_tarjetas WHERE id = p_tarjeta AND quitada_en IS NULL;
    IF NOT FOUND OR t.cuenta_id <> v_cuenta OR t.modo <> p_modo THEN
        RAISE EXCEPTION 'COBRO_DATOS: Elige una de tus tarjetas guardadas.';
    END IF;

    -- Uno que se quedó colgado hace más de 10 minutos ya no cuenta como en
    -- curso: queda «rechazado — sin respuesta». Si la pasarela sí cobró, su
    -- aviso lo pasa a pagado (cobros_confirmar).
    UPDATE cobros SET estado = 'rechazado', error_codigo = 'sin_respuesta',
                      error_mensaje = 'No hubo respuesta de la pasarela. Si te llegó el cargo, se confirmará solo.'
     WHERE cuenta_id = v_cuenta AND estado = 'procesando' AND creado_en < now() - interval '10 minutes';
    IF EXISTS (SELECT 1 FROM cobros WHERE cuenta_id = v_cuenta AND estado = 'procesando') THEN
        RAISE EXCEPTION 'COBRO_EN_CURSO: Ya hay un pago en curso. Espera unos segundos.';
    END IF;

    m := cobros_monto(v_cuenta, p_periodo, p_modo IN ('simulado', 'prueba'));
    IF (m->>'nada')::boolean THEN
        RAISE EXCEPTION 'COBRO_NADA: %', m->>'motivo';
    END IF;

    INSERT INTO cobros (cuenta_id, modo, medio, tarjeta_id, monto, concepto, periodo, periodo_desde, periodo_hasta, estado,
                        comprador_tipo_doc, comprador_doc, comprador_nombre, comprador_direccion, comprador_correo,
                        comprobante_tipo)
    VALUES (v_cuenta, p_modo, 'tarjeta', t.id, (m->>'monto')::numeric, m->>'concepto', p_periodo,
            (m->>'desde')::date, (m->>'hasta')::date, 'procesando',
            d.tipo_doc, d.numero, d.nombre, d.direccion, d.correo,
            CASE d.tipo_doc WHEN 'ruc' THEN '01' ELSE '03' END)
    RETURNING id INTO v_id;

    RETURN jsonb_build_object('id', v_id, 'cuenta', v_cuenta, 'monto', (m->>'monto')::numeric, 'concepto', m->>'concepto',
        'correo', d.correo, 'tarjeta', t.tarjeta_ref, 'cliente', t.cliente_ref,
        'desde', m->>'desde', 'hasta', m->>'hasta');
END $function$;

-- A3 / A8
CREATE OR REPLACE FUNCTION public.planes_cambio_preparar(p_empresa uuid, p_plan text, p_modo text, p_tarjeta uuid)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public'
AS $function$
DECLARE
    v_cuenta uuid := cobros_mi_cuenta();
    q  jsonb;
    k  cobros_ajustes%ROWTYPE;
    d  cobros_datos%ROWTYPE;
    t  cobros_tarjetas%ROWTYPE;
    v_id uuid;
BEGIN
    IF v_cuenta IS NULL THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo el dueño de la cuenta (o un colaborador administrador) paga.';
    END IF;
    -- 086 (A3/A8): primero el candado, después la cotización (el segundo clic
    -- ve el plan ya subido y no cobra otra vez).
    PERFORM 1 FROM cuentas WHERE id = v_cuenta FOR UPDATE;
    PERFORM 1 FROM empresas WHERE id = p_empresa FOR UPDATE;
    q := planes_cotizar_cambio(p_empresa, p_plan);
    IF q->>'accion' <> 'cobrar' THEN
        RAISE EXCEPTION 'COBRO_NADA: Este cambio no necesita pago.';
    END IF;
    IF (q->>'alProximo')::boolean THEN
        RAISE EXCEPTION 'COBRO_NADA: La diferencia es menor a S/ 3: se suma a tu próximo cobro.';
    END IF;
    SELECT * INTO k FROM cobros_ajustes WHERE id = 1;
    IF p_modo NOT IN ('simulado', 'prueba', 'real') OR NOT k.acepta_tarjeta THEN
        RAISE EXCEPTION 'COBRO_MODO: Los pagos con tarjeta están apagados por ahora.';
    END IF;
    SELECT * INTO d FROM cobros_datos WHERE cuenta_id = v_cuenta;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'COBRO_DATOS: Primero completa los datos de tu comprobante (RUC o DNI) en «Mis pagos».';
    END IF;
    SELECT * INTO t FROM cobros_tarjetas WHERE id = p_tarjeta AND quitada_en IS NULL;
    IF NOT FOUND OR t.cuenta_id <> v_cuenta OR t.modo <> p_modo THEN
        RAISE EXCEPTION 'COBRO_DATOS: Elige una de tus tarjetas guardadas.';
    END IF;

    UPDATE cobros SET estado = 'rechazado', error_codigo = 'sin_respuesta',
                      error_mensaje = 'No hubo respuesta de la pasarela. Si te llegó el cargo, se confirmará solo.'
     WHERE cuenta_id = v_cuenta AND estado = 'procesando' AND creado_en < now() - interval '10 minutes';
    IF EXISTS (SELECT 1 FROM cobros WHERE cuenta_id = v_cuenta AND estado = 'procesando') THEN
        RAISE EXCEPTION 'COBRO_EN_CURSO: Ya hay un pago en curso. Espera unos segundos.';
    END IF;

    INSERT INTO cobros (cuenta_id, modo, medio, tarjeta_id, monto, concepto, periodo, periodo_desde, periodo_hasta, estado,
                        comprador_tipo_doc, comprador_doc, comprador_nombre, comprador_direccion, comprador_correo,
                        comprobante_tipo, plan_empresa, plan_nuevo)
    VALUES (v_cuenta, p_modo, 'tarjeta', t.id, (q->>'monto')::numeric, q->>'concepto', 'otro',
            current_date, (q->>'hasta')::date, 'procesando',
            d.tipo_doc, d.numero, d.nombre, d.direccion, d.correo,
            CASE d.tipo_doc WHEN 'ruc' THEN '01' ELSE '03' END, p_empresa, p_plan)
    RETURNING id INTO v_id;

    RETURN jsonb_build_object('id', v_id, 'cuenta', v_cuenta, 'monto', (q->>'monto')::numeric, 'concepto', q->>'concepto',
        'correo', d.correo, 'tarjeta', t.tarjeta_ref, 'cliente', t.cliente_ref);
END $function$;

-- A8
CREATE OR REPLACE FUNCTION public.planes_cambiar(p_empresa uuid, p_plan text, p_al_proximo boolean)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public'
AS $function$
DECLARE
    q jsonb;
    v_cuenta uuid := planes_mi_cuenta_admin();
BEGIN
    -- 086 (A8): el candado ANTES de cotizar (dos clics: una sola diferencia).
    PERFORM 1 FROM empresas WHERE id = p_empresa FOR UPDATE;
    q := planes_cotizar_cambio(p_empresa, p_plan);   -- revisa la cuenta y el plan
    CASE q->>'accion'
        WHEN 'igual' THEN
            NULL;
        WHEN 'deshacer' THEN
            UPDATE empresas SET plan_programado = NULL, plan_programado_desde = NULL WHERE id = p_empresa;
        WHEN 'inmediato' THEN
            UPDATE empresas SET plan = p_plan, plan_programado = NULL, plan_programado_desde = NULL WHERE id = p_empresa;
        WHEN 'programado' THEN
            UPDATE empresas SET plan_programado = p_plan, plan_programado_desde = (q->>'desde')::date WHERE id = p_empresa;
        WHEN 'cobrar' THEN
            IF NOT COALESCE(p_al_proximo, false) THEN
                RAISE EXCEPTION 'PLAN_PAGAR: Para subir de plan hay que pagar la diferencia (S/ %).', q->>'monto';
            END IF;
            INSERT INTO cobros_pendientes (cuenta_id, empresa_id, monto, concepto, creado_por)
            VALUES (v_cuenta, p_empresa, (q->>'monto')::numeric, q->>'concepto', ctx_usuario());
            UPDATE empresas SET plan = p_plan, plan_programado = NULL, plan_programado_desde = NULL WHERE id = p_empresa;
    END CASE;
    RETURN q;
END $function$;

-- A4 / A7 / A11 / A12
CREATE OR REPLACE FUNCTION public.planes_cotizar_cambio(p_empresa uuid, p_plan text)
 RETURNS jsonb
 LANGUAGE plpgsql
 STABLE SECURITY DEFINER
 SET search_path TO 'public'
AS $function$
DECLARE
    v_cuenta  uuid := planes_mi_cuenta_admin();
    e         empresas%ROWTYPE;
    c         cuentas%ROWTYPE;
    a         plataforma_ajustes%ROWTYPE;
    r         jsonb;
    v_oferta  boolean;
    v_antes   numeric;
    v_despues numeric;
    v_dif     numeric;
    v_monto   numeric := 0;
    v_dias    integer := 0;
    v_hasta   date;
    v_desde   date;
    v_per     text;
    v_total   integer;
    pp        record;
BEGIN
    IF v_cuenta IS NULL THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: Solo el dueño de la cuenta (o un colaborador administrador) elige el plan.';
    END IF;
    IF p_plan IS NULL OR planes_orden(p_plan) IS NULL THEN
        RAISE EXCEPTION 'PLAN_NO_EXISTE: Ese plan no existe.';
    END IF;
    SELECT * INTO e FROM empresas WHERE id = p_empresa AND cuenta_id = v_cuenta AND activo;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'PLAN_NO_EXISTE: Esa empresa no es de tu cuenta.';
    END IF;
    SELECT * INTO c FROM cuentas WHERE id = v_cuenta;
    SELECT * INTO a FROM plataforma_ajustes WHERE id = 1;

    IF p_plan = e.plan THEN
        -- 086 (A4): si el periodo del plan menor ya se pagó, no se deshace.
        IF e.plan_programado IS NOT NULL AND EXISTS (
            SELECT 1 FROM cobros x WHERE x.cuenta_id = v_cuenta AND x.estado = 'pagado'
               AND cobros_es_suscripcion(x.periodo, x.plan_empresa, x.paquete_empresa)
               AND x.periodo_desde >= e.plan_programado_desde) THEN
            RAISE EXCEPTION 'PLAN_NO_PUEDE: Ya pagaste el periodo que empieza el % con el Plan %. Cuando empiece, podrás volver a subir pagando solo la diferencia.',
                to_char(e.plan_programado_desde, 'DD/MM/YYYY'), initcap(e.plan_programado);
        END IF;
        RETURN jsonb_build_object('accion', CASE WHEN e.plan_programado IS NULL THEN 'igual' ELSE 'deshacer' END,
                                  'actual', e.plan, 'nuevo', p_plan, 'programado', e.plan_programado,
                                  'desde', e.plan_programado_desde);
    END IF;

    -- Sin un periodo pagado corriendo: cambia ya, sin cobrar (como antes).
    r := planes_resumen_cuenta(v_cuenta);
    -- 086 (A12): el mes gratis o la cortesía ya no son atajo si hay un periodo
    -- pagado (incluso por adelantado): se cobra la diferencia de ese periodo.
    IF NOT planes_encendidos() OR c.acceso_total OR e.prueba_hasta > now()
       OR e.pausada_en IS NOT NULL
       OR (r->>'estado') NOT IN ('al_dia', 'precio_especial', 'prueba', 'cortesia', 'meses_gratis')
       OR NOT EXISTS (SELECT 1 FROM cobros x WHERE x.cuenta_id = v_cuenta AND x.estado = 'pagado'
                         AND cobros_es_suscripcion(x.periodo, x.plan_empresa, x.paquete_empresa) AND x.periodo_hasta >= current_date)
       OR ((c.prueba_hasta > now() OR (r->>'estado') IN ('prueba', 'cortesia', 'meses_gratis'))
           AND NOT EXISTS (SELECT 1 FROM cobros x WHERE x.cuenta_id = v_cuenta AND x.estado = 'pagado'
                              AND cobros_es_suscripcion(x.periodo, x.plan_empresa, x.paquete_empresa) AND x.periodo_desde > current_date)) THEN
        RETURN jsonb_build_object('accion', 'inmediato', 'actual', e.plan, 'nuevo', p_plan);
    END IF;

    -- Hasta cuándo ya pagó.
    SELECT max(x.periodo_hasta) INTO v_hasta FROM cobros x
     WHERE x.cuenta_id = v_cuenta AND x.estado = 'pagado' AND cobros_es_suscripcion(x.periodo, x.plan_empresa, x.paquete_empresa);

    -- BAJAR: en el próximo cobro.
    IF planes_orden(p_plan) < planes_orden(e.plan) THEN
        RETURN jsonb_build_object('accion', 'programado', 'actual', e.plan, 'nuevo', p_plan,
                                  'desde', v_hasta + 1, 'pagadoHasta', v_hasta);
    END IF;

    -- SUBIR: la diferencia al mes entre el plan que paga y el nuevo.
    v_oferta := planes_en_oferta() OR COALESCE(c.lanzamiento_hasta >= current_date, false);
    IF c.tipo = 'negocio' THEN
        SELECT CASE WHEN v_oferta THEN COALESCE(p.precio_oferta, p.precio_regular) ELSE p.precio_regular END
          INTO v_antes FROM planes p WHERE p.codigo = e.plan;
        SELECT CASE WHEN v_oferta THEN COALESCE(p.precio_oferta, p.precio_regular) ELSE p.precio_regular END
          INTO v_despues FROM planes p WHERE p.codigo = p_plan;
    ELSE
        SELECT CASE WHEN v_oferta THEN COALESCE(p.cartera_extra_oferta, p.cartera_extra_regular) ELSE p.cartera_extra_regular END
          INTO v_antes FROM planes p WHERE p.codigo = e.plan;
        SELECT CASE WHEN v_oferta THEN COALESCE(p.cartera_extra_oferta, p.cartera_extra_regular) ELSE p.cartera_extra_regular END
          INTO v_despues FROM planes p WHERE p.codigo = p_plan;
    END IF;
    v_dif := COALESCE(v_despues, 0) - COALESCE(v_antes, 0);

    -- 086 (A7): una subida de esta empresa cuyo pago espera respuesta de Culqi.
    IF EXISTS (SELECT 1 FROM cobros x WHERE x.plan_empresa = p_empresa
                  AND (x.estado = 'procesando'
                       OR (x.estado = 'rechazado' AND x.error_codigo = 'sin_respuesta' AND x.creado_en > now() - interval '24 hours'))) THEN
        RAISE EXCEPTION 'PLAN_EN_CURSO: Hay un pago de subida de esta empresa esperando la respuesta de Culqi. Si se cobró, el plan sube solo; si no, podrás intentarlo de nuevo en unas horas.';
    END IF;

    -- Con precio especial paga lo acordado: subir no cuesta más.
    IF (r->>'estado') = 'precio_especial' OR v_dif <= 0 THEN
        RETURN jsonb_build_object('accion', 'inmediato', 'actual', e.plan, 'nuevo', p_plan);
    END IF;

    -- Por cada periodo pagado que sigue corriendo (puede haber adelantado el
    -- siguiente), los días que quedan de él.
    FOR pp IN
        SELECT x.periodo, x.periodo_desde, x.periodo_hasta
          FROM cobros x
         WHERE x.cuenta_id = v_cuenta AND x.estado = 'pagado' AND cobros_es_suscripcion(x.periodo, x.plan_empresa, x.paquete_empresa)
           AND x.periodo_hasta >= current_date
         ORDER BY x.periodo_desde
    LOOP
        v_desde := GREATEST(pp.periodo_desde, current_date);
        v_total := pp.periodo_hasta - pp.periodo_desde + 1;
        v_dias  := v_dias + (pp.periodo_hasta - v_desde + 1);
        v_monto := v_monto + v_dif * cobros_factor_meses(pp.periodo, pp.periodo_desde, pp.periodo_hasta)
                                   * (pp.periodo_hasta - v_desde + 1) / v_total;   -- 086 (A11): también pagos de varios meses
        v_per := pp.periodo;
    END LOOP;
    v_monto := round(v_monto, 2);
    IF v_monto <= 0 THEN
        RETURN jsonb_build_object('accion', 'inmediato', 'actual', e.plan, 'nuevo', p_plan);
    END IF;

    RETURN jsonb_build_object(
        'accion', 'cobrar', 'actual', e.plan, 'nuevo', p_plan, 'empresa', e.nombre,
        'monto', v_monto, 'diferenciaMensual', v_dif, 'dias', v_dias,
        'diasPeriodo', (SELECT x2.periodo_hasta - x2.periodo_desde + 1 FROM cobros x2
                         WHERE x2.cuenta_id = v_cuenta AND x2.estado = 'pagado'
                           AND cobros_es_suscripcion(x2.periodo, x2.plan_empresa, x2.paquete_empresa)
                           AND x2.periodo_hasta >= current_date ORDER BY x2.periodo_desde LIMIT 1),
        'desde', current_date, 'hasta', v_hasta, 'periodo', v_per,
        'mesesGratisAnual', a.anual_meses_gratis,
        'minimo', 3.00, 'alProximo', v_monto < 3.00,
        'nuevoMensual', COALESCE((r->'mensual'->>'total')::numeric, 0) + v_dif,
        'concepto', left('Diferencia por cambio a Plan ' || initcap(p_plan) || ' — ' || e.nombre || ' ('
                         || to_char(current_date, 'DD/MM/YYYY') || ' al ' || to_char(v_hasta, 'DD/MM/YYYY') || ')', 300));
END $function$;

-- A3 (paquete)
CREATE OR REPLACE FUNCTION public.comprobantes_paquete_preparar(p_empresa uuid, p_modo text, p_tarjeta uuid)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'public'
AS $function$
DECLARE
    v_cuenta uuid := cobros_mi_cuenta();
    q   jsonb;
    k   cobros_ajustes%ROWTYPE;
    d   cobros_datos%ROWTYPE;
    t   cobros_tarjetas%ROWTYPE;
    v_id uuid;
    v_monto numeric;
    v_cant integer;
    v_nombre text;
BEGIN
    IF v_cuenta IS NULL OR NOT EXISTS (SELECT 1 FROM empresas WHERE id = p_empresa AND cuenta_id = v_cuenta) THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo el dueño de la cuenta (o un colaborador administrador) compra paquetes.';
    END IF;
    -- 086 (A3): dos clics: el segundo espera y no compra otro paquete sin querer.
    PERFORM 1 FROM cuentas WHERE id = v_cuenta FOR UPDATE;
    IF EXISTS (SELECT 1 FROM cobros WHERE paquete_empresa = p_empresa AND estado = 'pagado'
                  AND creado_en > now() - interval '2 minutes') THEN
        RAISE EXCEPTION 'COBRO_EN_CURSO: Acabas de comprar un paquete para esta empresa. Revisa tu saldo antes de comprar otro.';
    END IF;
    q := comprobantes_tope(p_empresa);
    IF q IS NULL OR NOT (q->>'aplica')::boolean THEN
        RAISE EXCEPTION 'COBRO_NADA: Esta empresa no tiene tope de comprobantes: no necesita paquetes.';
    END IF;
    SELECT * INTO k FROM cobros_ajustes WHERE id = 1;
    IF p_modo NOT IN ('simulado', 'prueba', 'real') OR NOT k.acepta_tarjeta THEN
        RAISE EXCEPTION 'COBRO_MODO: Los pagos con tarjeta están apagados por ahora.';
    END IF;
    SELECT * INTO d FROM cobros_datos WHERE cuenta_id = v_cuenta;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'COBRO_DATOS: Primero completa los datos de tu comprobante (RUC o DNI) en «Mis pagos».';
    END IF;
    SELECT * INTO t FROM cobros_tarjetas WHERE id = p_tarjeta AND quitada_en IS NULL;
    IF NOT FOUND OR t.cuenta_id <> v_cuenta OR t.modo <> p_modo THEN
        RAISE EXCEPTION 'COBRO_DATOS: Elige una de tus tarjetas guardadas.';
    END IF;

    UPDATE cobros SET estado = 'rechazado', error_codigo = 'sin_respuesta',
                      error_mensaje = 'No hubo respuesta de la pasarela. Si te llegó el cargo, se confirmará solo.'
     WHERE cuenta_id = v_cuenta AND estado = 'procesando' AND creado_en < now() - interval '10 minutes';
    IF EXISTS (SELECT 1 FROM cobros WHERE cuenta_id = v_cuenta AND estado = 'procesando') THEN
        RAISE EXCEPTION 'COBRO_EN_CURSO: Ya hay un pago en curso. Espera unos segundos.';
    END IF;

    v_monto := (q->'paquete'->>'precio')::numeric;
    v_cant  := (q->'paquete'->>'cantidad')::integer;
    v_nombre := (SELECT COALESCE(razon_social, nombre, '') FROM empresas WHERE id = p_empresa);
    INSERT INTO cobros (cuenta_id, modo, medio, tarjeta_id, monto, concepto, periodo, periodo_desde, periodo_hasta, estado,
                        comprador_tipo_doc, comprador_doc, comprador_nombre, comprador_direccion, comprador_correo,
                        comprobante_tipo, paquete_empresa, paquete_cantidad)
    VALUES (v_cuenta, p_modo, 'tarjeta', t.id, v_monto,
            left('Paquete de ' || to_char(v_cant, 'FM999G999G999') || ' comprobantes extra · ' || v_nombre, 300),
            'otro', current_date, current_date, 'procesando',
            d.tipo_doc, d.numero, d.nombre, d.direccion, d.correo,
            CASE d.tipo_doc WHEN 'ruc' THEN '01' ELSE '03' END, p_empresa, v_cant)
    RETURNING id INTO v_id;

    RETURN jsonb_build_object('id', v_id, 'cuenta', v_cuenta, 'monto', v_monto, 'cantidad', v_cant,
        'concepto', (SELECT concepto FROM cobros WHERE id = v_id),
        'correo', d.correo, 'tarjeta', t.tarjeta_ref, 'cliente', t.cliente_ref);
END $function$;


-- -----------------------------------------------------------------------------
-- A1: no se pueden poner menos cupos que las empresas activas.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION planes_fijar_cupos(p_cupos integer) RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := planes_mi_cuenta_admin();
    v_min    integer := (SELECT cartera_minimo FROM plataforma_ajustes WHERE id = 1);
    v_usados integer;
BEGIN
    IF v_cuenta IS NULL OR (SELECT tipo FROM cuentas WHERE id = v_cuenta) <> 'profesional' THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: Los cupos son de la Cuenta Profesional, y los cambia su dueño o un colaborador administrador.';
    END IF;
    IF p_cupos IS NULL OR p_cupos < v_min OR p_cupos > 10000 THEN
        RAISE EXCEPTION 'PLAN_CUPOS: El mínimo de la Cartera es % empresas.', v_min;
    END IF;
    v_usados := planes_cupos_usados(v_cuenta);
    IF p_cupos < v_usados THEN   -- 086 (A1)
        RAISE EXCEPTION 'PLAN_CUPOS: Tienes % empresas activas en tu Cartera: los cupos no pueden ser menos. Pausa o retira empresas para bajar.', v_usados;
    END IF;
    PERFORM planes_cupos_sumar_diferencia(v_cuenta, p_cupos);
    UPDATE cuentas SET cartera_cupos = p_cupos WHERE id = v_cuenta;
    RETURN p_cupos;
END $$;

-- -----------------------------------------------------------------------------
-- A5 / A11: la diferencia de cupos, periodo por periodo (cada cobro sabe
-- cuántas empresas pagó), también en pagos de varios meses.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION planes_cupos_diferencia(p_cuenta uuid, p_cupos integer) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    c        cuentas%ROWTYPE;
    a        plataforma_ajustes%ROWTYPE;
    r        jsonb;
    v_oferta boolean;
    v_legado integer;
    v_desp   integer;
    v_base   integer;
    v_antes  integer;
    v_dif    numeric;
    v_dif1   numeric;
    v_monto  numeric := 0;
    v_dias   integer := 0;
    v_dperi  integer;
    v_hasta  date;
    v_desde  date;
    v_total  integer;
    pp       record;
BEGIN
    SELECT * INTO c FROM cuentas WHERE id = p_cuenta;
    IF NOT FOUND OR c.tipo <> 'profesional' OR p_cupos IS NULL THEN
        RETURN jsonb_build_object('cobrar', false);
    END IF;
    SELECT * INTO a FROM plataforma_ajustes WHERE id = 1;
    IF NOT planes_encendidos() OR c.acceso_total OR c.prueba_hasta > now() THEN
        RETURN jsonb_build_object('cobrar', false);
    END IF;
    r := planes_resumen_cuenta(p_cuenta);
    IF (r->>'estado') IS DISTINCT FROM 'al_dia' THEN
        RETURN jsonb_build_object('cobrar', false);
    END IF;

    -- Para cobros de antes de la 086 (sin cupos_cubiertos): como antes.
    v_legado := GREATEST(COALESCE(c.cartera_cupos, 0), planes_cupos_usados(p_cuenta), a.cartera_minimo);
    IF c.cartera_cubiertos_hasta >= current_date THEN
        v_legado := GREATEST(v_legado, COALESCE(c.cartera_cupos_cubiertos, 0));
    END IF;
    v_desp := GREATEST(p_cupos, a.cartera_minimo);
    v_oferta := planes_en_oferta() OR COALESCE(c.lanzamiento_hasta >= current_date, false);

    FOR pp IN
        SELECT x.periodo, x.periodo_desde, x.periodo_hasta, x.cupos_cubiertos FROM cobros x
         WHERE x.cuenta_id = p_cuenta AND x.estado = 'pagado'
           AND cobros_es_suscripcion(x.periodo, x.plan_empresa, x.paquete_empresa)
           AND x.periodo_hasta >= current_date
         ORDER BY x.periodo_desde
    LOOP
        v_base := GREATEST(COALESCE(pp.cupos_cubiertos, v_legado), a.cartera_minimo);
        v_antes := LEAST(COALESCE(v_antes, v_base), v_base);
        CONTINUE WHEN v_desp <= v_base;
        v_dif := planes_precio_cartera(v_desp, NOT v_oferta) - planes_precio_cartera(v_base, NOT v_oferta);
        CONTINUE WHEN v_dif <= 0;
        v_dif1  := COALESCE(v_dif1, v_dif);
        v_desde := GREATEST(pp.periodo_desde, current_date);
        v_total := pp.periodo_hasta - pp.periodo_desde + 1;
        v_dperi := COALESCE(v_dperi, v_total);
        v_dias  := v_dias + (pp.periodo_hasta - v_desde + 1);
        v_monto := v_monto + v_dif * cobros_factor_meses(pp.periodo, pp.periodo_desde, pp.periodo_hasta)
                                   * (pp.periodo_hasta - v_desde + 1) / v_total;
        v_hasta := GREATEST(COALESCE(v_hasta, pp.periodo_hasta), pp.periodo_hasta);
    END LOOP;
    v_monto := round(v_monto, 2);
    IF v_hasta IS NULL OR v_monto <= 0 THEN
        RETURN jsonb_build_object('cobrar', false, 'cubiertas', COALESCE(v_antes, v_legado));
    END IF;

    RETURN jsonb_build_object(
        'cobrar', true, 'monto', v_monto, 'antes', v_antes, 'despues', v_desp,
        'diferenciaMensual', v_dif1, 'dias', v_dias, 'diasPeriodo', v_dperi,
        'desde', current_date, 'hasta', v_hasta,
        'concepto', left('Diferencia por ampliar la Cartera de ' || v_antes || ' a ' || v_desp || ' empresas ('
                         || to_char(current_date, 'DD/MM/YYYY') || ' al ' || to_char(v_hasta, 'DD/MM/YYYY') || ')', 300));
END $$;

CREATE OR REPLACE FUNCTION planes_cupos_sumar_diferencia(p_cuenta uuid, p_cupos integer) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    q jsonb;
BEGIN
    PERFORM 1 FROM cuentas WHERE id = p_cuenta FOR UPDATE;   -- dos pestañas a la vez: una espera
    q := planes_cupos_diferencia(p_cuenta, p_cupos);
    IF (q->>'cobrar')::boolean THEN
        INSERT INTO cobros_pendientes (cuenta_id, monto, concepto, creado_por)
        VALUES (p_cuenta, (q->>'monto')::numeric, q->>'concepto', ctx_usuario());
        -- 086 (A5): cada periodo pagado que sigue corriendo queda cubierto hasta ese número.
        UPDATE cobros x SET cupos_cubiertos = GREATEST(COALESCE(x.cupos_cubiertos, 0), (q->>'despues')::integer)
         WHERE x.cuenta_id = p_cuenta AND x.estado = 'pagado'
           AND cobros_es_suscripcion(x.periodo, x.plan_empresa, x.paquete_empresa)
           AND x.periodo_hasta >= current_date;
        UPDATE cuentas SET cartera_cupos_cubiertos = (q->>'despues')::integer,
                           cartera_cubiertos_hasta = (q->>'hasta')::date
         WHERE id = p_cuenta;
    END IF;
    RETURN q;
END $$;

-- Al crear un cobro de suscripción de una Cartera: cuántas empresas paga.
CREATE OR REPLACE FUNCTION fn_cobros_cupos_al_crear() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NEW.cupos_cubiertos IS NULL
       AND cobros_es_suscripcion(NEW.periodo, NEW.plan_empresa, NEW.paquete_empresa)
       AND (SELECT tipo FROM cuentas WHERE id = NEW.cuenta_id) = 'profesional' THEN
        NEW.cupos_cubiertos := (SELECT GREATEST(COALESCE(c.cartera_cupos, 0), planes_cupos_usados(c.id),
                                                (SELECT cartera_minimo FROM plataforma_ajustes WHERE id = 1))
                                  FROM cuentas c WHERE c.id = NEW.cuenta_id);
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS b_cobros_cupos_al_crear ON cobros;
CREATE TRIGGER b_cobros_cupos_al_crear BEFORE INSERT ON cobros
    FOR EACH ROW EXECUTE FUNCTION fn_cobros_cupos_al_crear();

-- El de la 084 alargaba lo cubierto hasta el mes pagado por adelantado (A5): fuera.
DROP TRIGGER IF EXISTS z_cobros_cupos_cubiertos ON cobros;

-- -----------------------------------------------------------------------------
-- A14: el tope en UN solo disparador (antes de guardar). Gasta el paquete al
-- emitir pasado el tope y lo devuelve si SUNAT rechaza.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_documentos_tope() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE t record;
BEGIN
    IF NEW.emitido_en IS NULL AND NEW.estado IN ('encolado', 'aceptado', 'observado', 'rechazado', 'anulado') THEN
        NEW.emitido_en := now();
    END IF;
    IF NEW.tipo IN ('01', '03') AND NOT NEW.gasto_paquete
       AND NEW.estado IN ('encolado', 'aceptado', 'observado')
       AND (TG_OP = 'INSERT' OR OLD.estado NOT IN ('encolado', 'aceptado', 'observado', 'anulado')) THEN
        t := comprobantes_tope_de(NEW.empresa_id);
        IF t.tope IS NOT NULL AND comprobantes_emitidos_mes(NEW.empresa_id) + 1 > t.tope THEN
            UPDATE empresas SET paquete_saldo = paquete_saldo - 1 WHERE id = NEW.empresa_id AND paquete_saldo > 0;
            IF FOUND THEN NEW.gasto_paquete := true; END IF;
        END IF;
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.estado = 'rechazado' AND OLD.gasto_paquete THEN
        UPDATE empresas SET paquete_saldo = paquete_saldo + 1 WHERE id = NEW.empresa_id;
        NEW.gasto_paquete := false;
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS z_documentos_gasta_paquete ON documentos;
DROP TRIGGER IF EXISTS b_documentos_emitido_en ON documentos;
DROP TRIGGER IF EXISTS b_documentos_tope ON documentos;
CREATE TRIGGER b_documentos_tope BEFORE INSERT OR UPDATE OF estado ON documentos
    FOR EACH ROW EXECUTE FUNCTION fn_documentos_tope();

-- -----------------------------------------------------------------------------
-- A9: los cobros que quedaron «procesando» (Culqi no respondió).
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION cobros_limpiar_colgados() RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE n integer;
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    UPDATE cobros SET estado = 'rechazado', error_codigo = 'sin_respuesta',
                      error_mensaje = 'No hubo respuesta de la pasarela. Si te llegó el cargo, se confirmará solo.'
     WHERE estado = 'procesando' AND creado_en < now() - interval '10 minutes';
    GET DIAGNOSTICS n = ROW_COUNT;
    RETURN n;
END $$;

-- -----------------------------------------------------------------------------
-- DUEÑO Y PERMISOS
-- -----------------------------------------------------------------------------
DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY[
        'cobros_factor_meses(text, date, date)', 'planes_fijar_cupos(integer)', 'planes_cupos_diferencia(uuid, integer)',
        'planes_cupos_sumar_diferencia(uuid, integer)', 'fn_cobros_cupos_al_crear()', 'fn_documentos_tope()',
        'cobros_limpiar_colgados()', 'comprobantes_paquete_preparar(uuid, text, uuid)']
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        IF f IN ('planes_fijar_cupos(integer)', 'cobros_limpiar_colgados()',
                 'comprobantes_paquete_preparar(uuid, text, uuid)') THEN
            EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
        END IF;
    END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION cobros_es_suscripcion(text, uuid, uuid) TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('086_arreglos_revision_cobros')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '086: arreglos de la revisión QA de cobros y planes (A1–A15). Nada se borró.';
END $$;