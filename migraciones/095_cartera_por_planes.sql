-- =============================================================================
-- 095 — PLAN CARTERA POR PLANES Y DESCUENTO EN % (e280, 22 nov 2026)
--
-- La Cuenta Profesional elige CUÁNTAS empresas de cada plan compra («2 Básico
-- y 1 Pro») y después las reparte a sus empresas.
--
--   · cuentas.cartera_cupos      el total de empresas (como antes)
--   · cuentas.cartera_cupos_pro  cuántas de ellas en Pro      (nuevo)
--   · cuentas.cartera_cupos_max  cuántas en Max               (nuevo)
--
-- Pro y Max se pagan por cupo comprado o por empresa asignada, lo que sea
-- mayor (comprar un Pro y no usarlo se paga igual). Subir una empresa a un
-- plan con cupo libre ya pagado no cobra nada. La empresa del estudio («Mi
-- empresa») no usa cupos: Básico gratis y, si sube, solo la diferencia.
--
-- Los precios no cambian de lugar: el precio por empresa de Básico son los
-- tramos de la Cartera y el de Pro/Max es Básico + su subida. La pantalla
-- de Planes y cobros los muestra como «precio por empresa de cada plan».
--
-- Un trato especial nuevo: «descuento» en % con fecha de fin y, en la
-- Cartera, un tope de empresas (ej.: Socio fundador, 50 % hasta 20).
--
-- Al migrar, cada Cartera queda con tantos cupos Pro y Max como empresas
-- tiene hoy en esos planes: nadie paga distinto. Nada se borra.
-- Se puede correr dos veces.
-- =============================================================================

BEGIN;

ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS cartera_cupos_pro integer NOT NULL DEFAULT 0;
ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS cartera_cupos_max integer NOT NULL DEFAULT 0;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_cuentas_cupos_planes') THEN
        ALTER TABLE cuentas ADD CONSTRAINT ck_cuentas_cupos_planes CHECK (cartera_cupos_pro >= 0 AND cartera_cupos_max >= 0);
    END IF;
END $$;

ALTER TABLE cuenta_ajustes ADD COLUMN IF NOT EXISTS porcentaje   numeric(5,2);
ALTER TABLE cuenta_ajustes ADD COLUMN IF NOT EXISTS empresas_max integer;
ALTER TABLE cuenta_ajustes DROP CONSTRAINT IF EXISTS ck_cuenta_ajustes_tipo;
ALTER TABLE cuenta_ajustes ADD CONSTRAINT ck_cuenta_ajustes_tipo
    CHECK (tipo IN ('cortesia', 'meses_gratis', 'precio_especial', 'plan_mejor', 'ampliar_prueba', 'descuento'));
ALTER TABLE cuenta_ajustes DROP CONSTRAINT IF EXISTS ck_cuenta_ajustes_datos;
ALTER TABLE cuenta_ajustes ADD CONSTRAINT ck_cuenta_ajustes_datos CHECK (
        (tipo IN ('cortesia', 'plan_mejor') AND plan_codigo IS NOT NULL)
     OR (tipo = 'meses_gratis'    AND meses BETWEEN 1 AND 24)
     OR (tipo = 'precio_especial' AND precio >= 0)
     OR (tipo = 'ampliar_prueba')
     OR (tipo = 'descuento' AND porcentaje > 0 AND porcentaje <= 100 AND (empresas_max IS NULL OR empresas_max >= 1)));

-- Cuántas empresas de la Cartera pagan ese plan (sin la del estudio ni las pausadas).
CREATE OR REPLACE FUNCTION planes_asignadas(p_cuenta uuid, p_plan text) RETURNS integer
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT count(*)::integer FROM empresas e
     WHERE e.cuenta_id = p_cuenta AND e.activo AND e.pausada_en IS NULL AND NOT e.es_del_estudio
       AND planes_plan_cobro(e.id) = p_plan;
$$;

-- Al migrar: tantos cupos Pro y Max como empresas tiene hoy en esos planes.
UPDATE cuentas c SET cartera_cupos_pro = planes_asignadas(c.id, 'pro'),
                     cartera_cupos_max = planes_asignadas(c.id, 'max')
 WHERE c.tipo = 'profesional' AND c.cartera_cupos_pro = 0 AND c.cartera_cupos_max = 0
   AND NOT EXISTS (SELECT 1 FROM migraciones_aplicadas WHERE nombre = '095_cartera_por_planes');

-- -----------------------------------------------------------------------------
-- EL RESUMEN DE LA CUENTA (086) con cupos Pro/Max, la empresa del estudio aparte y el descuento
-- -----------------------------------------------------------------------------
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
    v_extra_est  numeric := 0;   -- 095: lo que suma la empresa del estudio (subida)
    v_dsc    numeric := 0;
    v_dsc_aj cuenta_ajustes%ROWTYPE;
    v_cpro   integer;
    v_cmax   integer;
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
               'porcentaje', x.porcentaje, 'empresasMax', x.empresas_max,
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
        -- 095: Pro y Max se pagan por CUPO comprado (aunque no esté asignado)
        -- o por empresa asignada, lo que sea mayor. La empresa del estudio va
        -- aparte: Básico gratis y, si sube, solo la diferencia.
        v_cpro := GREATEST(COALESCE(c.cartera_cupos_pro, 0), planes_asignadas(p_cuenta, 'pro'));
        v_cmax := GREATEST(COALESCE(c.cartera_cupos_max, 0), planes_asignadas(p_cuenta, 'max'));
        SELECT COALESCE(sum(CASE p.codigo WHEN 'pro' THEN v_cpro WHEN 'max' THEN v_cmax ELSE 0 END
                            * CASE WHEN v_oferta THEN COALESCE(p.cartera_extra_oferta, p.cartera_extra_regular) ELSE p.cartera_extra_regular END), 0),
               COALESCE(sum(CASE p.codigo WHEN 'pro' THEN v_cpro WHEN 'max' THEN v_cmax ELSE 0 END * p.cartera_extra_regular), 0)
          INTO v_extra, v_extrar
          FROM planes p WHERE p.codigo IN ('pro', 'max');
        SELECT COALESCE(sum(CASE WHEN v_oferta THEN COALESCE(p.cartera_extra_oferta, p.cartera_extra_regular) ELSE p.cartera_extra_regular END), 0)
          INTO v_extra_est
          FROM empresas e JOIN planes p ON p.codigo = planes_plan_cobro(e.id)
         WHERE e.cuenta_id = p_cuenta AND e.activo AND e.pausada_en IS NULL AND e.es_del_estudio;
        v_extra  := v_extra + v_extra_est;
        v_extrar := v_extrar + (SELECT COALESCE(sum(p.cartera_extra_regular), 0)
                                  FROM empresas e JOIN planes p ON p.codigo = planes_plan_cobro(e.id)
                                 WHERE e.cuenta_id = p_cuenta AND e.activo AND e.pausada_en IS NULL AND e.es_del_estudio);
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

    -- 095: UN DESCUENTO EN % con fecha de fin (ej.: «Socio fundador», 50 %
    -- hasta 20 empresas). Solo si no hay otro trato que mande (cortesía,
    -- meses gratis, precio especial). En la Cartera se aplica a lo que pagan
    -- sus empresas (no a la subida de la empresa del estudio) y, con tope de
    -- empresas, en proporción: 50 % hasta 20 con 25 empresas = 50 % × 20/25.
    IF v_estado = 'al_dia' AND v_total > 0 THEN
        SELECT * INTO v_dsc_aj FROM cuenta_ajustes x
         WHERE x.cuenta_id = p_cuenta AND x.activo AND x.anulado_en IS NULL AND x.hasta >= current_date AND x.tipo = 'descuento'
         ORDER BY x.creado_en DESC LIMIT 1;
        IF FOUND THEN
            IF c.tipo = 'profesional' THEN
                v_dsc := round((v_total - v_extra_est) * v_dsc_aj.porcentaje / 100
                               * LEAST(1, COALESCE(v_dsc_aj.empresas_max, v_n)::numeric / GREATEST(v_n, 1)), 2);
            ELSE
                v_dsc := round(v_total * v_dsc_aj.porcentaje / 100, 2);
            END IF;
            v_dsc := LEAST(GREATEST(v_dsc, 0), v_total);
            v_total := v_total - v_dsc;
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
        -- 095: cupos por plan y el descuento.
        'cuposPro', COALESCE(c.cartera_cupos_pro, 0), 'cuposMax', COALESCE(c.cartera_cupos_max, 0),
        'asignadasPro', CASE WHEN c.tipo = 'profesional' THEN planes_asignadas(p_cuenta, 'pro') ELSE 0 END,
        'asignadasMax', CASE WHEN c.tipo = 'profesional' THEN planes_asignadas(p_cuenta, 'max') ELSE 0 END,
        'descuento', CASE WHEN v_dsc_aj.id IS NULL THEN NULL ELSE jsonb_build_object(
                        'porcentaje', v_dsc_aj.porcentaje, 'empresasMax', v_dsc_aj.empresas_max,
                        'hasta', v_dsc_aj.hasta, 'motivo', v_dsc_aj.motivo, 'monto', v_dsc) END,
        'mensual', jsonb_build_object('base', v_base, 'extras', v_extra, 'total', v_total, 'descuento', v_dsc,
                                      'extrasEstudio', v_extra_est,
                                      'regular', v_baser + v_extrar,
                                      'anual', round(v_total * (12 - a.anual_meses_gratis), 2)));
END $function$;

-- EL MONTO A COBRAR (083), restando el descuento
CREATE OR REPLACE FUNCTION cobros_monto(p_cuenta uuid, p_periodo text, p_prueba boolean) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    c        cuentas%ROWTYPE;
    a        plataforma_ajustes%ROWTYPE;
    k        cobros_ajustes%ROWTYPE;
    r        jsonb;
    v_desde  date;
    v_hasta  date;
    v_mes    numeric;
    v_monto  numeric;
    v_aj     cuenta_ajustes%ROWTYPE;
    v_plan   text;
    v_per    text;
    v_conc   text;
    v_adel   boolean := false;
    v_pend   numeric := 0;
BEGIN
    IF p_periodo NOT IN ('mensual', 'anual') THEN
        RAISE EXCEPTION 'COBRO_DATOS: Elige pago mensual o anual.';
    END IF;
    r := planes_resumen_cuenta(p_cuenta);       -- revisa que la cuenta sea tuya
    IF r IS NULL THEN RETURN jsonb_build_object('nada', true, 'motivo', 'La cuenta no existe.'); END IF;
    SELECT * INTO c FROM cuentas WHERE id = p_cuenta;
    SELECT * INTO a FROM plataforma_ajustes WHERE id = 1;
    SELECT * INTO k FROM cobros_ajustes WHERE id = 1;

    IF c.acceso_total THEN
        RETURN jsonb_build_object('nada', true, 'motivo', 'Tu cuenta tiene acceso total: no paga.');
    END IF;
    IF NOT planes_encendidos() AND NOT p_prueba THEN
        RETURN jsonb_build_object('nada', true, 'motivo', 'Los planes todavía no están encendidos: no se cobra nada.');
    END IF;

    -- Desde cuándo: al terminar el mes gratis, o el día del próximo cobro, u hoy.
    -- En el mes gratis se puede adelantar el primer pago (empieza cuando
    -- termina); después, solo unos días antes del próximo cobro.
    v_desde := COALESCE(c.pago_proximo, current_date);
    IF c.prueba_hasta > now() THEN
        v_desde := GREATEST(v_desde, (c.prueba_hasta AT TIME ZONE 'America/Lima')::date);
    END IF;
    IF c.pago_proximo IS NOT NULL AND v_desde = c.pago_proximo AND v_desde - current_date > k.aviso_dias THEN
        RETURN jsonb_build_object('nada', true, 'pagadoHasta', v_desde - 1,
            'motivo', 'Ya pagaste hasta el ' || to_char(v_desde - 1, 'DD/MM/YYYY') || '. Podrás adelantar el próximo pago desde el '
                      || to_char(v_desde - k.aviso_dias, 'DD/MM/YYYY') || '.');
    END IF;
    v_adel := v_desde > current_date;

    -- Un trato especial vigente en esa fecha.
    SELECT * INTO v_aj FROM cuenta_ajustes x
     WHERE x.cuenta_id = p_cuenta AND x.activo AND x.anulado_en IS NULL AND x.hasta >= v_desde
       AND x.tipo IN ('cortesia', 'meses_gratis', 'ampliar_prueba', 'precio_especial')
     ORDER BY CASE x.tipo WHEN 'ampliar_prueba' THEN 1 WHEN 'cortesia' THEN 2 WHEN 'meses_gratis' THEN 3 ELSE 4 END, x.creado_en DESC
     LIMIT 1;
    IF v_aj.id IS NOT NULL AND v_aj.tipo IN ('cortesia', 'meses_gratis', 'ampliar_prueba') THEN
        RETURN jsonb_build_object('nada', true,
            'motivo', 'Tienes ' || CASE v_aj.tipo WHEN 'cortesia' THEN 'una cortesía' WHEN 'meses_gratis' THEN 'meses gratis'
                                                  ELSE 'la prueba ampliada' END
                      || ' hasta el ' || to_char(v_aj.hasta, 'DD/MM/YYYY') || ': todavía no hay nada que pagar.');
    END IF;

    v_mes := COALESCE((r->'mensual'->>'base')::numeric, 0) + COALESCE((r->'mensual'->>'extras')::numeric, 0)
           - COALESCE((r->'mensual'->>'descuento')::numeric, 0);   -- 095: el descuento en %
    IF v_aj.id IS NOT NULL AND v_aj.tipo = 'precio_especial' THEN v_mes := v_aj.precio; END IF;
    IF v_mes IS NULL OR v_mes <= 0 THEN
        RETURN jsonb_build_object('nada', true, 'motivo', 'Con tu plan de hoy no hay nada que pagar.');
    END IF;

    IF p_periodo = 'anual' THEN
        v_monto := round(v_mes * (12 - a.anual_meses_gratis), 2);
        v_hasta := (v_desde + interval '12 months')::date - 1;
        v_per   := to_char(v_desde, 'DD/MM/YYYY') || ' al ' || to_char(v_hasta, 'DD/MM/YYYY');
    ELSE
        v_monto := round(v_mes, 2);
        v_hasta := (v_desde + interval '1 month')::date - 1;
        v_per   := cobros_mes(v_desde);
        IF extract(day FROM v_desde) <> 1 THEN
            v_per := to_char(v_desde, 'DD/MM/YYYY') || ' al ' || to_char(v_hasta, 'DD/MM/YYYY');
        END IF;
    END IF;

    IF c.tipo = 'profesional' THEN
        v_plan := 'Cartera de ' || (r->>'cobradas') || ' empresas';
        -- 095: con cupos Pro o Max, se dice cuántos.
        IF (r->>'cuposPro')::int + (r->>'asignadasPro')::int > 0 OR (r->>'cuposMax')::int + (r->>'asignadasMax')::int > 0 THEN
            v_plan := v_plan || ' (' || concat_ws(', ',
                NULLIF(GREATEST((r->>'cuposPro')::int, (r->>'asignadasPro')::int), 0) || ' Pro',
                NULLIF(GREATEST((r->>'cuposMax')::int, (r->>'asignadasMax')::int), 0) || ' Max') || ')';
        END IF;
    ELSE
        SELECT string_agg(DISTINCT p.nombre, ' + ') INTO v_plan
          FROM empresas e JOIN planes p ON p.codigo = planes_plan_cobro(e.id)
         WHERE e.cuenta_id = p_cuenta AND e.activo AND e.pausada_en IS NULL;
        v_plan := COALESCE(v_plan, 'Básico');
    END IF;
    v_conc := replace(replace(k.descripcion, '{plan}', v_plan), '{periodo}', v_per);
    IF p_periodo = 'anual' THEN v_conc := v_conc || ' (anual)'; END IF;
    IF COALESCE((r->'mensual'->>'descuento')::numeric, 0) > 0 AND (r->>'estado') = 'al_dia' THEN
        v_conc := v_conc || ' — descuento ' || to_char((r->'descuento'->>'porcentaje')::numeric, 'FM990.##') || ' %';
    END IF;

    -- 083: LAS DIFERENCIAS POR SUBIR DE PLAN que quedaron para este cobro (sin
    -- tarjeta, o menos de S/ 3). Entran todas las que ya existían; al quedar
    -- pagado, el disparador las marca con este cobro.
    v_pend := COALESCE((SELECT sum(x.monto) FROM cobros_pendientes x
                         WHERE x.cuenta_id = p_cuenta AND x.cobro_id IS NULL AND x.anulado_en IS NULL), 0);
    IF v_pend > 0 THEN
        v_monto := v_monto + v_pend;
        v_conc  := v_conc || ' + diferencia por cambio de plan S/ ' || to_char(v_pend, 'FM999990.00');
    END IF;

    RETURN jsonb_build_object(
        'nada', false, 'monto', v_monto, 'mensual', round(v_mes, 2), 'periodo', p_periodo, 'pendientes', v_pend,
        'desde', v_desde, 'hasta', v_hasta, 'concepto', left(v_conc, 300), 'adelantado', v_adel,
        'mesesGratisAnual', a.anual_meses_gratis,
        'apagados', NOT planes_encendidos());
END $$;

-- LA COTIZACIÓN DE UN CAMBIO DE PLAN (086): con cupo libre ya pagado, sube sin cobrar
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

    -- 095: en la Cartera, si ya compró un cupo libre de ese plan, sube sin pagar
    -- (ya lo paga). La empresa del estudio no usa cupos: paga su diferencia.
    IF c.tipo = 'profesional' AND NOT e.es_del_estudio AND p_plan IN ('pro', 'max')
       AND planes_orden(p_plan) > planes_orden(e.plan)
       AND planes_asignadas(v_cuenta, p_plan) < (CASE p_plan WHEN 'pro' THEN c.cartera_cupos_pro ELSE c.cartera_cupos_max END) THEN
        RETURN jsonb_build_object('accion', 'inmediato', 'actual', e.plan, 'nuevo', p_plan, 'cupoLibre', true);
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

-- -----------------------------------------------------------------------------
-- LO QUE CUESTA HOY COMPRAR MÁS CUPOS PRO O MAX (a mitad de un periodo pagado)
-- Como planes_cupos_diferencia (084/086) pero para las subidas: la diferencia
-- al mes, por los días que quedan de cada periodo pagado que sigue corriendo.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION planes_cupos_planes_diferencia(p_cuenta uuid, p_pro integer, p_max integer) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    c        cuentas%ROWTYPE;
    r        jsonb;
    v_oferta boolean;
    v_dif    numeric;
    v_monto  numeric := 0;
    v_dias   integer := 0;
    v_hasta  date;
    v_desde  date;
    v_total  integer;
    pp       record;
BEGIN
    SELECT * INTO c FROM cuentas WHERE id = p_cuenta;
    IF NOT FOUND OR c.tipo <> 'profesional' THEN RETURN jsonb_build_object('cobrar', false); END IF;
    IF NOT planes_encendidos() OR c.acceso_total OR c.prueba_hasta > now() THEN
        RETURN jsonb_build_object('cobrar', false);
    END IF;
    r := planes_resumen_cuenta(p_cuenta);
    IF (r->>'estado') IS DISTINCT FROM 'al_dia' THEN RETURN jsonb_build_object('cobrar', false); END IF;
    v_oferta := planes_en_oferta() OR COALESCE(c.lanzamiento_hasta >= current_date, false);

    SELECT COALESCE(sum(
               GREATEST(0, GREATEST(CASE p.codigo WHEN 'pro' THEN p_pro ELSE p_max END, planes_asignadas(p_cuenta, p.codigo))
                         - GREATEST(CASE p.codigo WHEN 'pro' THEN c.cartera_cupos_pro ELSE c.cartera_cupos_max END,
                                    planes_asignadas(p_cuenta, p.codigo)))
             * CASE WHEN v_oferta THEN COALESCE(p.cartera_extra_oferta, p.cartera_extra_regular) ELSE p.cartera_extra_regular END), 0)
      INTO v_dif FROM planes p WHERE p.codigo IN ('pro', 'max');
    IF v_dif <= 0 THEN RETURN jsonb_build_object('cobrar', false); END IF;

    FOR pp IN
        SELECT x.periodo, x.periodo_desde, x.periodo_hasta FROM cobros x
         WHERE x.cuenta_id = p_cuenta AND x.estado = 'pagado'
           AND cobros_es_suscripcion(x.periodo, x.plan_empresa, x.paquete_empresa)
           AND x.periodo_hasta >= current_date
         ORDER BY x.periodo_desde
    LOOP
        v_desde := GREATEST(pp.periodo_desde, current_date);
        v_total := pp.periodo_hasta - pp.periodo_desde + 1;
        v_dias  := v_dias + (pp.periodo_hasta - v_desde + 1);
        v_monto := v_monto + v_dif * cobros_factor_meses(pp.periodo, pp.periodo_desde, pp.periodo_hasta)
                                   * (pp.periodo_hasta - v_desde + 1) / v_total;
        v_hasta := GREATEST(COALESCE(v_hasta, pp.periodo_hasta), pp.periodo_hasta);
    END LOOP;
    v_monto := round(v_monto, 2);
    IF v_hasta IS NULL OR v_monto <= 0 THEN RETURN jsonb_build_object('cobrar', false); END IF;
    RETURN jsonb_build_object('cobrar', true, 'monto', v_monto, 'dias', v_dias, 'diferenciaMensual', v_dif,
        'desde', current_date, 'hasta', v_hasta,
        'concepto', left('Diferencia por sumar cupos Pro/Max a la Cartera ('
                         || to_char(current_date, 'DD/MM/YYYY') || ' al ' || to_char(v_hasta, 'DD/MM/YYYY') || ')', 300));
END $$;

-- La cotización junta (total + Pro/Max), para decirla antes de guardar.
CREATE OR REPLACE FUNCTION planes_cotizar_cupos_planes(p_cupos integer, p_pro integer, p_max integer) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := (SELECT u.cuenta_id FROM usuarios u WHERE u.id = ctx_usuario() AND u.activo);
    a jsonb;
    b jsonb;
    v_monto numeric := 0;
    v_dias  integer := 0;
BEGIN
    IF v_cuenta IS NULL THEN RAISE EXCEPTION 'PLAN_NO_PUEDE: No tienes una cartera.'; END IF;
    a := planes_cupos_diferencia(v_cuenta, p_cupos);
    b := planes_cupos_planes_diferencia(v_cuenta, p_pro, p_max);
    IF (a->>'cobrar')::boolean THEN v_monto := v_monto + (a->>'monto')::numeric; v_dias := GREATEST(v_dias, (a->>'dias')::int); END IF;
    IF (b->>'cobrar')::boolean THEN v_monto := v_monto + (b->>'monto')::numeric; v_dias := GREATEST(v_dias, (b->>'dias')::int); END IF;
    IF v_monto <= 0 THEN RETURN jsonb_build_object('cobrar', false); END IF;
    RETURN jsonb_build_object('cobrar', true, 'monto', v_monto, 'dias', v_dias);
END $$;

-- GUARDAR: cuántas empresas en total y cuántas en Pro y Max.
CREATE OR REPLACE FUNCTION planes_fijar_cupos_planes(p_cupos integer, p_pro integer, p_max integer) RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := planes_mi_cuenta_admin();
    q jsonb;
BEGIN
    IF v_cuenta IS NULL OR (SELECT tipo FROM cuentas WHERE id = v_cuenta) <> 'profesional' THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: Los cupos son de la Cuenta Profesional, y los cambia su dueño o un colaborador administrador.';
    END IF;
    IF p_pro IS NULL OR p_max IS NULL OR p_pro < 0 OR p_max < 0 THEN
        RAISE EXCEPTION 'PLAN_CUPOS: Escribe cuántas empresas quieres en Pro y en Max (0 si ninguna).';
    END IF;
    IF p_pro + p_max > COALESCE(p_cupos, 0) THEN
        RAISE EXCEPTION 'PLAN_CUPOS: Las empresas en Pro y Max no pueden ser más que el total.';
    END IF;
    IF p_pro < planes_asignadas(v_cuenta, 'pro') THEN
        RAISE EXCEPTION 'PLAN_CUPOS: Tienes % empresas en Pro: primero pasa alguna a otro plan para bajar los Pro.', planes_asignadas(v_cuenta, 'pro');
    END IF;
    IF p_max < planes_asignadas(v_cuenta, 'max') THEN
        RAISE EXCEPTION 'PLAN_CUPOS: Tienes % empresas en Max: primero pasa alguna a otro plan para bajar los Max.', planes_asignadas(v_cuenta, 'max');
    END IF;
    PERFORM 1 FROM cuentas WHERE id = v_cuenta FOR UPDATE;
    -- La diferencia de Pro/Max se calcula con los cupos de antes.
    q := planes_cupos_planes_diferencia(v_cuenta, p_pro, p_max);
    PERFORM planes_fijar_cupos(p_cupos);   -- el total, con sus reglas y su diferencia (086)
    IF (q->>'cobrar')::boolean THEN
        INSERT INTO cobros_pendientes (cuenta_id, monto, concepto, creado_por)
        VALUES (v_cuenta, (q->>'monto')::numeric, q->>'concepto', ctx_usuario());
    END IF;
    UPDATE cuentas SET cartera_cupos_pro = p_pro, cartera_cupos_max = p_max WHERE id = v_cuenta;
    RETURN p_cupos;
END $$;

-- -----------------------------------------------------------------------------
-- DUEÑO Y PERMISOS
-- -----------------------------------------------------------------------------
DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY[
        'planes_asignadas(uuid, text)', 'planes_resumen_cuenta(uuid)', 'cobros_monto(uuid, text, boolean)',
        'planes_cotizar_cambio(uuid, text)', 'planes_cupos_planes_diferencia(uuid, integer, integer)',
        'planes_cotizar_cupos_planes(integer, integer, integer)', 'planes_fijar_cupos_planes(integer, integer, integer)']
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        IF f <> 'planes_cupos_planes_diferencia(uuid, integer, integer)' THEN
            EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
        END IF;
    END LOOP;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('095_cartera_por_planes')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '095: la Cartera compra cupos por plan (Básico, Pro, Max) y hay descuento en %% con fecha de fin. Nada se borró.';
END $$;