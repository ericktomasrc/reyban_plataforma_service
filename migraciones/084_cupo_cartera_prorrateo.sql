-- =============================================================================
-- 084 — LA CARTERA SUBE DE CUPO A MITAD DE PERIODO (8 nov 2026, e232)
--
-- Como Google Workspace cuando se agrega un usuario: si el estudio ya pagó un
-- periodo que sigue corriendo y sube sus cupos (agrega empresas de más, o
-- cambia el número en «Mi plan»), paga solo la DIFERENCIA por los días que le
-- quedan, y se suma a su PRÓXIMO cobro (cobros_pendientes, como en 083):
--
--     (precio de B empresas − precio de A empresas) × días que quedan ÷ días del periodo
--
--   (con el precio graduado de los tramos, la oferta que tenga la cuenta y, en
--   el anual, el mismo descuento anual). No se cobra nada en el mes gratis,
--   con acceso total, cortesía, meses gratis, precio especial, planes apagados
--   o sin un periodo pagado corriendo.
--
--   · Bajar de cupos no devuelve nada (el próximo cobro ya sale con menos).
--   · Lo ya cubierto no se cobra dos veces: cuentas.cartera_cupos_cubiertos /
--     cartera_cubiertos_hasta guardan cuántas empresas ya están pagadas hasta
--     qué día (al pagar el periodo y al sumar una diferencia). Bajar y volver
--     a subir dentro del mismo periodo no cobra otra vez.
--   · planes_cotizar_cupos(n): lo que costaría (para decirlo antes de confirmar).
--   · planes_cupo_al_agregar() lleva también la «diferencia».
--
-- Se puede correr dos veces. Nada se borra.
-- =============================================================================
BEGIN;

ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS cartera_cupos_cubiertos integer;
ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS cartera_cubiertos_hasta date;
COMMENT ON COLUMN cuentas.cartera_cupos_cubiertos IS '084: empresas de la Cartera ya pagadas hasta cartera_cubiertos_hasta (no se cobran dos veces).';

-- -----------------------------------------------------------------------------
-- 1. LA COTIZACIÓN (no cambia nada). Interna: recibe la cuenta.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION planes_cupos_diferencia(p_cuenta uuid, p_cupos integer) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    c        cuentas%ROWTYPE;
    a        plataforma_ajustes%ROWTYPE;
    r        jsonb;
    v_oferta boolean;
    v_antes  integer;
    v_desp   integer;
    v_dif    numeric;
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
    r := planes_resumen_cuenta(p_cuenta);          -- revisa que la cuenta sea tuya
    IF (r->>'estado') IS DISTINCT FROM 'al_dia' THEN
        RETURN jsonb_build_object('cobrar', false);   -- cortesía, precio especial…
    END IF;

    -- Lo que ya está pagado (el cupo de hoy, o lo cubierto si es más).
    v_antes := GREATEST(COALESCE(c.cartera_cupos, planes_cupos_usados(p_cuenta)), a.cartera_minimo);
    IF c.cartera_cubiertos_hasta >= current_date THEN
        v_antes := GREATEST(v_antes, COALESCE(c.cartera_cupos_cubiertos, 0));
    END IF;
    v_desp := GREATEST(p_cupos, a.cartera_minimo);
    IF v_desp <= v_antes THEN
        RETURN jsonb_build_object('cobrar', false, 'cubiertas', v_antes);
    END IF;

    v_oferta := planes_en_oferta() OR COALESCE(c.lanzamiento_hasta >= current_date, false);
    v_dif := planes_precio_cartera(v_desp, NOT v_oferta) - planes_precio_cartera(v_antes, NOT v_oferta);
    IF v_dif <= 0 THEN
        RETURN jsonb_build_object('cobrar', false, 'cubiertas', v_antes);
    END IF;

    -- Por cada periodo pagado que sigue corriendo, los días que quedan de él.
    FOR pp IN
        SELECT periodo, periodo_desde, periodo_hasta FROM cobros
         WHERE cuenta_id = p_cuenta AND estado = 'pagado' AND periodo IN ('mensual', 'anual')
           AND periodo_hasta >= current_date
         ORDER BY periodo_desde
    LOOP
        v_desde := GREATEST(pp.periodo_desde, current_date);
        v_total := pp.periodo_hasta - pp.periodo_desde + 1;
        v_dperi := COALESCE(v_dperi, v_total);
        v_dias  := v_dias + (pp.periodo_hasta - v_desde + 1);
        v_monto := v_monto + CASE WHEN pp.periodo = 'anual'
                                  THEN v_dif * (12 - a.anual_meses_gratis) * (pp.periodo_hasta - v_desde + 1) / v_total
                                  ELSE v_dif * (pp.periodo_hasta - v_desde + 1) / v_total END;
        v_hasta := GREATEST(COALESCE(v_hasta, pp.periodo_hasta), pp.periodo_hasta);
    END LOOP;
    v_monto := round(v_monto, 2);
    IF v_hasta IS NULL OR v_monto <= 0 THEN
        RETURN jsonb_build_object('cobrar', false, 'cubiertas', v_antes);
    END IF;

    RETURN jsonb_build_object(
        'cobrar', true, 'monto', v_monto, 'antes', v_antes, 'despues', v_desp,
        'diferenciaMensual', v_dif, 'dias', v_dias, 'diasPeriodo', v_dperi,
        'desde', current_date, 'hasta', v_hasta,
        'concepto', left('Diferencia por ampliar la Cartera de ' || v_antes || ' a ' || v_desp || ' empresas ('
                         || to_char(current_date, 'DD/MM/YYYY') || ' al ' || to_char(v_hasta, 'DD/MM/YYYY') || ')', 300));
END $$;

-- La de la pantalla: la de mi cuenta.
CREATE OR REPLACE FUNCTION planes_cotizar_cupos(p_cupos integer) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := (SELECT u.cuenta_id FROM usuarios u WHERE u.id = ctx_usuario() AND u.activo);
BEGIN
    IF v_cuenta IS NULL THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: No tienes una cartera.';
    END IF;
    RETURN planes_cupos_diferencia(v_cuenta, p_cupos);
END $$;

-- -----------------------------------------------------------------------------
-- 2. SUMARLA AL PRÓXIMO COBRO (interna). Se llama ANTES de subir el cupo.
-- -----------------------------------------------------------------------------
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
        UPDATE cuentas SET cartera_cupos_cubiertos = (q->>'despues')::integer,
                           cartera_cubiertos_hasta = (q->>'hasta')::date
         WHERE id = p_cuenta;
    END IF;
    RETURN q;
END $$;

-- -----------------------------------------------------------------------------
-- 3. LOS DOS CAMINOS POR DONDE SUBE EL CUPO
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION planes_fijar_cupos(p_cupos integer) RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := planes_mi_cuenta_admin();
    v_min    integer := (SELECT cartera_minimo FROM plataforma_ajustes WHERE id = 1);
BEGIN
    IF v_cuenta IS NULL OR (SELECT tipo FROM cuentas WHERE id = v_cuenta) <> 'profesional' THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: Los cupos son de la Cuenta Profesional, y los cambia su dueño o un colaborador administrador.';
    END IF;
    IF p_cupos IS NULL OR p_cupos < v_min OR p_cupos > 10000 THEN
        RAISE EXCEPTION 'PLAN_CUPOS: El mínimo de la Cartera es % empresas.', v_min;
    END IF;
    PERFORM planes_cupos_sumar_diferencia(v_cuenta, p_cupos);   -- 084
    UPDATE cuentas SET cartera_cupos = p_cupos WHERE id = v_cuenta;
    RETURN p_cupos;
END $$;

CREATE OR REPLACE FUNCTION planes_subir_cupo() RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid;
    v_n integer;
BEGIN
    SELECT u.cuenta_id INTO v_cuenta FROM usuarios u
     WHERE u.id = ctx_usuario() AND u.nivel_cuenta IS NOT NULL;
    IF v_cuenta IS NULL THEN RAISE EXCEPTION 'PLAN_NO_PUEDE: No tienes una cartera.'; END IF;
    SELECT GREATEST(COALESCE(cartera_cupos, 0), planes_cupos_usados(v_cuenta), 1) INTO v_n
      FROM cuentas WHERE id = v_cuenta;
    PERFORM planes_cupos_sumar_diferencia(v_cuenta, v_n);         -- 084
    UPDATE cuentas SET cartera_cupos = v_n WHERE id = v_cuenta;
    RETURN v_n;
END $$;

-- Al agregar una empresa de más: también lo que costaría (diferencia).
CREATE OR REPLACE FUNCTION planes_cupo_al_agregar() RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid;
    c cuentas%ROWTYPE;
    v_usados integer;
BEGIN
    SELECT u.cuenta_id INTO v_cuenta FROM usuarios u WHERE u.id = ctx_usuario();
    IF v_cuenta IS NULL OR NOT planes_encendidos() THEN RETURN NULL; END IF;
    SELECT * INTO c FROM cuentas WHERE id = v_cuenta;
    IF c.tipo <> 'profesional' OR c.acceso_total OR c.prueba_hasta > now() OR c.cartera_cupos IS NULL THEN
        RETURN NULL;
    END IF;
    v_usados := planes_cupos_usados(v_cuenta);
    IF v_usados < c.cartera_cupos THEN RETURN NULL; END IF;
    RETURN jsonb_build_object('cupos', c.cartera_cupos, 'usados', v_usados, 'nuevos', v_usados + 1,
                              'diasQueFaltan', (date_trunc('month', now()) + interval '1 month')::date - current_date,
                              'diferencia', planes_cupos_diferencia(v_cuenta, v_usados + 1));
END $$;

-- -----------------------------------------------------------------------------
-- 4. AL PAGAR EL PERIODO: esas empresas quedan cubiertas hasta su último día.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_cobros_cupos_cubiertos() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NEW.estado = 'pagado' AND (TG_OP = 'INSERT' OR OLD.estado IS DISTINCT FROM 'pagado')
       AND NEW.periodo IN ('mensual', 'anual') AND NEW.periodo_hasta IS NOT NULL THEN
        UPDATE cuentas c
           SET cartera_cupos_cubiertos = GREATEST(
                   GREATEST(COALESCE(c.cartera_cupos, planes_cupos_usados(c.id)),
                            (SELECT a.cartera_minimo FROM plataforma_ajustes a WHERE a.id = 1)),
                   CASE WHEN c.cartera_cubiertos_hasta >= current_date THEN COALESCE(c.cartera_cupos_cubiertos, 0) ELSE 0 END),
               cartera_cubiertos_hasta = GREATEST(COALESCE(c.cartera_cubiertos_hasta, NEW.periodo_hasta), NEW.periodo_hasta)
         WHERE c.id = NEW.cuenta_id AND c.tipo = 'profesional';
    END IF;
    RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS z_cobros_cupos_cubiertos ON cobros;
CREATE TRIGGER z_cobros_cupos_cubiertos AFTER INSERT OR UPDATE OF estado ON cobros
    FOR EACH ROW EXECUTE FUNCTION fn_cobros_cupos_cubiertos();

-- -----------------------------------------------------------------------------
-- 5. DUEÑO Y PERMISOS
-- -----------------------------------------------------------------------------
DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY[
        'planes_cupos_diferencia(uuid, integer)', 'planes_cotizar_cupos(integer)',
        'planes_cupos_sumar_diferencia(uuid, integer)', 'planes_fijar_cupos(integer)',
        'planes_subir_cupo()', 'planes_cupo_al_agregar()', 'fn_cobros_cupos_cubiertos()']
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        IF f IN ('planes_cotizar_cupos(integer)', 'planes_fijar_cupos(integer)',
                 'planes_subir_cupo()', 'planes_cupo_al_agregar()') THEN
            EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
        END IF;
    END LOOP;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('084_cupo_cartera_prorrateo')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '084: Subir de cupo la Cartera a mitad de periodo suma la diferencia por los días que quedan al próximo cobro. Nada se borró.';
END $$;