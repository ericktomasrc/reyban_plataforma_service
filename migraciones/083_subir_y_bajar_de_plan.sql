-- =============================================================================
-- 083 — SUBIR Y BAJAR DE PLAN A MITAD DE PERIODO (8 nov 2026, e230)
--
-- Como lo hacen las suscripciones de software (Stripe, Shopify, Google
-- Workspace):
--
--   · SUBIR (Básico → Pro → Max, o una empresa de la Cartera a Pro/Max): el
--     plan nuevo empieza YA. Si ya pagó un periodo que sigue corriendo, paga
--     solo la DIFERENCIA por los días que le quedan:
--         diferencia al mes × días que quedan ÷ días del periodo
--     (en el anual, con el mismo descuento anual). Se cobra en el momento con
--     su tarjeta; el plan cambia cuando el pago queda pagado (también si lo
--     confirma después el aviso de Culqi). Sin tarjeta, o si sale menos de
--     S/ 3, el plan cambia ya y la diferencia se suma a su próximo cobro.
--   · BAJAR: no se devuelve nada; sigue con lo que pagó hasta el último día
--     pagado y el plan nuevo empieza en el próximo cobro (plan_programado).
--     Se puede deshacer hasta ese día. Al llegar el día, el ciclo de cobros
--     lo aplica (planes_aplicar_programados).
--   · Sin un periodo pagado corriendo (mes gratis, cortesía, acceso total,
--     planes apagados, todavía no pagó): cambia ya y no se cobra nada, como
--     antes.
--
--   · empresas.plan_programado / plan_programado_desde.
--   · cobros.plan_empresa / plan_nuevo: el pago de una diferencia; al quedar
--     pagado, el disparador sube la empresa.
--   · cobros_pendientes: diferencias que se suman al próximo cobro. Nada se
--     borra: al cobrarse quedan marcadas con su cobro.
--   · planes_plan_cobro(): el plan con el que se cobra el próximo periodo.
--     planes_resumen_cuenta y cobros_monto lo usan (y cobros_monto suma los
--     pendientes).
--
-- Se puede correr dos veces.
-- =============================================================================
BEGIN;

-- -----------------------------------------------------------------------------
-- 1. COLUMNAS Y TABLA
-- -----------------------------------------------------------------------------
ALTER TABLE empresas ADD COLUMN IF NOT EXISTS plan_programado       text;
ALTER TABLE empresas ADD COLUMN IF NOT EXISTS plan_programado_desde date;
ALTER TABLE empresas DROP CONSTRAINT IF EXISTS ck_empresas_plan_programado;
ALTER TABLE empresas ADD CONSTRAINT ck_empresas_plan_programado
    CHECK ((plan_programado IS NULL) = (plan_programado_desde IS NULL)
           AND (plan_programado IS NULL OR plan_programado IN ('basico', 'pro', 'max')));

ALTER TABLE cobros ADD COLUMN IF NOT EXISTS plan_empresa uuid REFERENCES empresas(id);
ALTER TABLE cobros ADD COLUMN IF NOT EXISTS plan_nuevo   text;
ALTER TABLE cobros DROP CONSTRAINT IF EXISTS ck_cobros_plan_nuevo;
ALTER TABLE cobros ADD CONSTRAINT ck_cobros_plan_nuevo
    CHECK ((plan_nuevo IS NULL) = (plan_empresa IS NULL)
           AND (plan_nuevo IS NULL OR (plan_nuevo IN ('pro', 'max') AND periodo = 'otro')));

CREATE TABLE IF NOT EXISTS cobros_pendientes (
    id          uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    cuenta_id   uuid          NOT NULL REFERENCES cuentas(id),
    empresa_id  uuid          REFERENCES empresas(id),
    monto       numeric(10,2) NOT NULL,
    concepto    text          NOT NULL,
    cobro_id    uuid          REFERENCES cobros(id),
    cobrado_en  timestamptz,
    anulado_en  timestamptz,
    anulado_motivo text,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_cobros_pendientes_monto CHECK (monto > 0 AND monto <= 100000),
    CONSTRAINT ck_cobros_pendientes_cobro CHECK ((cobro_id IS NULL) = (cobrado_en IS NULL))
);
CREATE INDEX IF NOT EXISTS ix_cobros_pendientes_cuenta ON cobros_pendientes (cuenta_id) WHERE cobro_id IS NULL;

DO $$
BEGIN
    ALTER TABLE cobros_pendientes ENABLE ROW LEVEL SECURITY;
    ALTER TABLE cobros_pendientes FORCE ROW LEVEL SECURITY;
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'cobros_pendientes' AND policyname = 'p_cobros_pendientes') THEN
        CREATE POLICY p_cobros_pendientes ON cobros_pendientes
            USING (ctx_es_super() OR cuenta_id = (SELECT u.cuenta_id FROM usuarios u WHERE u.id = ctx_usuario()))
            WITH CHECK (ctx_es_super());
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_cobros_pendientes') THEN
        PERFORM vigilar_tabla('cobros_pendientes');
    END IF;
END $$;
GRANT SELECT ON cobros_pendientes TO plataforma_app;

-- -----------------------------------------------------------------------------
-- 2. EL PLAN CON EL QUE SE COBRA EL PRÓXIMO PERIODO
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION planes_plan_cobro(p_empresa uuid) RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT CASE WHEN e.plan_programado IS NOT NULL
                 AND e.plan_programado_desde <= COALESCE(cobros_fecha_cobro(e.cuenta_id), current_date)
                THEN e.plan_programado ELSE e.plan END
      FROM empresas e WHERE e.id = p_empresa;
$$;

-- El orden de los planes (para saber si sube o baja).
CREATE OR REPLACE FUNCTION planes_orden(p text) RETURNS integer
LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE p WHEN 'basico' THEN 1 WHEN 'pro' THEN 2 WHEN 'max' THEN 3 END;
$$;

CREATE OR REPLACE FUNCTION planes_resumen_cuenta(p_cuenta uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
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
        v_n := GREATEST(COALESCE(c.cartera_cupos, v_usados), a.cartera_minimo);
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
END $$;

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

    v_mes := COALESCE((r->'mensual'->>'base')::numeric, 0) + COALESCE((r->'mensual'->>'extras')::numeric, 0);
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
    ELSE
        SELECT string_agg(DISTINCT p.nombre, ' + ') INTO v_plan
          FROM empresas e JOIN planes p ON p.codigo = planes_plan_cobro(e.id)
         WHERE e.cuenta_id = p_cuenta AND e.activo AND e.pausada_en IS NULL;
        v_plan := COALESCE(v_plan, 'Básico');
    END IF;
    v_conc := replace(replace(k.descripcion, '{plan}', v_plan), '{periodo}', v_per);
    IF p_periodo = 'anual' THEN v_conc := v_conc || ' (anual)'; END IF;

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

-- -----------------------------------------------------------------------------
-- 3. LA COTIZACIÓN: qué pasa si esta empresa cambia a ese plan (no cambia nada)
--
--   accion:
--     'igual'      ya está en ese plan (y nada programado)
--     'deshacer'   vuelve al plan que paga: se quita la bajada programada
--     'inmediato'  cambia ya y no se cobra nada (mes gratis, cortesía…)
--     'programado' baja en el próximo cobro (desde)
--     'cobrar'     sube ya pagando la diferencia (monto) — o, si no se puede
--                  cobrar ahora (sin tarjeta o menos de S/ 3), sube ya y la
--                  diferencia va al próximo cobro: eso lo decide quien aplica.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION planes_cotizar_cambio(p_empresa uuid, p_plan text) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
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
        RETURN jsonb_build_object('accion', CASE WHEN e.plan_programado IS NULL THEN 'igual' ELSE 'deshacer' END,
                                  'actual', e.plan, 'nuevo', p_plan, 'programado', e.plan_programado,
                                  'desde', e.plan_programado_desde);
    END IF;

    -- Sin un periodo pagado corriendo: cambia ya, sin cobrar (como antes).
    r := planes_resumen_cuenta(v_cuenta);
    IF NOT planes_encendidos() OR c.acceso_total OR c.prueba_hasta > now() OR e.prueba_hasta > now()
       OR e.pausada_en IS NOT NULL OR (r->>'estado') NOT IN ('al_dia', 'precio_especial')
       OR NOT EXISTS (SELECT 1 FROM cobros x WHERE x.cuenta_id = v_cuenta AND x.estado = 'pagado'
                         AND x.periodo IN ('mensual', 'anual') AND x.periodo_hasta >= current_date) THEN
        RETURN jsonb_build_object('accion', 'inmediato', 'actual', e.plan, 'nuevo', p_plan);
    END IF;

    -- Hasta cuándo ya pagó.
    SELECT max(x.periodo_hasta) INTO v_hasta FROM cobros x
     WHERE x.cuenta_id = v_cuenta AND x.estado = 'pagado' AND x.periodo IN ('mensual', 'anual');

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

    -- Con precio especial paga lo acordado: subir no cuesta más.
    IF (r->>'estado') = 'precio_especial' OR v_dif <= 0 THEN
        RETURN jsonb_build_object('accion', 'inmediato', 'actual', e.plan, 'nuevo', p_plan);
    END IF;

    -- Por cada periodo pagado que sigue corriendo (puede haber adelantado el
    -- siguiente), los días que quedan de él.
    FOR pp IN
        SELECT periodo, periodo_desde, periodo_hasta FROM cobros
         WHERE cuenta_id = v_cuenta AND estado = 'pagado' AND periodo IN ('mensual', 'anual')
           AND periodo_hasta >= current_date
         ORDER BY periodo_desde
    LOOP
        v_desde := GREATEST(pp.periodo_desde, current_date);
        v_total := pp.periodo_hasta - pp.periodo_desde + 1;
        v_dias  := v_dias + (pp.periodo_hasta - v_desde + 1);
        v_monto := v_monto + CASE WHEN pp.periodo = 'anual'
                                  THEN v_dif * (12 - a.anual_meses_gratis) * (pp.periodo_hasta - v_desde + 1) / v_total
                                  ELSE v_dif * (pp.periodo_hasta - v_desde + 1) / v_total END;
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
                         WHERE x2.cuenta_id = v_cuenta AND x2.estado = 'pagado' AND x2.periodo IN ('mensual', 'anual')
                           AND x2.periodo_hasta >= current_date ORDER BY x2.periodo_desde LIMIT 1),
        'desde', current_date, 'hasta', v_hasta, 'periodo', v_per,
        'mesesGratisAnual', a.anual_meses_gratis,
        'minimo', 3.00, 'alProximo', v_monto < 3.00,
        'nuevoMensual', COALESCE((r->'mensual'->>'total')::numeric, 0) + v_dif,
        'concepto', left('Diferencia por cambio a Plan ' || initcap(p_plan) || ' — ' || e.nombre || ' ('
                         || to_char(current_date, 'DD/MM/YYYY') || ' al ' || to_char(v_hasta, 'DD/MM/YYYY') || ')', 300));
END $$;

-- -----------------------------------------------------------------------------
-- 4. APLICAR UN CAMBIO SIN COBRAR EN EL MOMENTO
--    'inmediato', 'programado', 'deshacer', y 'cobrar' cuando no se cobra
--    ahora (p_al_proximo: sin tarjeta o menos de S/ 3): sube ya y la
--    diferencia queda para el próximo cobro.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION planes_cambiar(p_empresa uuid, p_plan text, p_al_proximo boolean) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    q jsonb := planes_cotizar_cambio(p_empresa, p_plan);   -- revisa la cuenta y el plan
    v_cuenta uuid := planes_mi_cuenta_admin();
BEGIN
    PERFORM 1 FROM empresas WHERE id = p_empresa FOR UPDATE;
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
END $$;

-- -----------------------------------------------------------------------------
-- 5. EMPEZAR EL PAGO DE UNA DIFERENCIA CON TARJETA (como cobros_preparar)
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION planes_cambio_preparar(p_empresa uuid, p_plan text, p_modo text, p_tarjeta uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := cobros_mi_cuenta();
    q  jsonb := planes_cotizar_cambio(p_empresa, p_plan);
    k  cobros_ajustes%ROWTYPE;
    d  cobros_datos%ROWTYPE;
    t  cobros_tarjetas%ROWTYPE;
    v_id uuid;
BEGIN
    IF v_cuenta IS NULL THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo el dueño de la cuenta (o un colaborador administrador) paga.';
    END IF;
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
END $$;

-- -----------------------------------------------------------------------------
-- 6. DISPARADORES DE COBROS
--    · Una diferencia que queda pagada sube la empresa (desde aquí, también
--      cuando el pago lo confirma después el aviso de Culqi).
--    · Un cobro del mes o del año que queda pagado se lleva los pendientes que
--      ya existían cuando se preparó (su monto los incluía).
--    · Un cobro anulado suelta sus pendientes (vuelven al próximo cobro).
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_cobros_cambio_plan() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NEW.estado = 'pagado' AND (TG_OP = 'INSERT' OR OLD.estado IS DISTINCT FROM 'pagado') THEN
        IF NEW.plan_nuevo IS NOT NULL THEN
            UPDATE empresas SET plan = NEW.plan_nuevo, plan_programado = NULL, plan_programado_desde = NULL
             WHERE id = NEW.plan_empresa AND planes_orden(plan) < planes_orden(NEW.plan_nuevo);
        END IF;
        IF NEW.periodo IN ('mensual', 'anual') THEN
            UPDATE cobros_pendientes SET cobro_id = NEW.id, cobrado_en = now()
             WHERE cuenta_id = NEW.cuenta_id AND cobro_id IS NULL AND anulado_en IS NULL
               AND creado_en <= NEW.creado_en;
        END IF;
    ELSIF TG_OP = 'UPDATE' AND NEW.estado = 'anulado' AND OLD.estado IS DISTINCT FROM 'anulado' THEN
        UPDATE cobros_pendientes SET cobro_id = NULL, cobrado_en = NULL WHERE cobro_id = NEW.id;
    END IF;
    RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS z_cobros_cambio_plan ON cobros;
CREATE TRIGGER z_cobros_cambio_plan AFTER INSERT OR UPDATE OF estado ON cobros
    FOR EACH ROW EXECUTE FUNCTION fn_cobros_cambio_plan();

-- -----------------------------------------------------------------------------
-- 7. LAS BAJADAS QUE YA LES TOCA (las aplica el ciclo de cobros, cada hora)
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION planes_aplicar_programados() RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v_n integer;
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'PLAN_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    UPDATE empresas SET plan = plan_programado, plan_programado = NULL, plan_programado_desde = NULL
     WHERE plan_programado IS NOT NULL AND plan_programado_desde <= current_date;
    GET DIAGNOSTICS v_n = ROW_COUNT;
    RETURN v_n;
END $$;

-- -----------------------------------------------------------------------------
-- 8. «ELEGIR» (la ruta de antes) PASA POR LAS REGLAS NUEVAS: subir con un
--    periodo pagado corriendo pide pagar (PLAN_PAGAR).
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION planes_elegir(p_empresa uuid, p_plan text) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    PERFORM planes_cambiar(p_empresa, p_plan, false);
    RETURN plan_efectivo(p_empresa);
END $$;

-- -----------------------------------------------------------------------------
-- 9. DUEÑO Y PERMISOS
-- -----------------------------------------------------------------------------
DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY[
        'planes_plan_cobro(uuid)', 'planes_resumen_cuenta(uuid)', 'cobros_monto(uuid, text, boolean)',
        'planes_cotizar_cambio(uuid, text)', 'planes_cambiar(uuid, text, boolean)',
        'planes_cambio_preparar(uuid, text, text, uuid)', 'planes_aplicar_programados()',
        'planes_elegir(uuid, text)', 'fn_cobros_cambio_plan()']
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        IF f <> 'fn_cobros_cambio_plan()' THEN
            EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
        END IF;
    END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION planes_orden(text) TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('083_subir_y_bajar_de_plan')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '083: Subir de plan paga la diferencia por los días que quedan; bajar empieza en el próximo cobro. Nada se borró.';
END $$;