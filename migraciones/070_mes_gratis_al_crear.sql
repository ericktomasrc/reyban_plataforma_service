-- =============================================================================
-- 070 — PLANES: EL MES GRATIS EMPIEZA AL CREAR LA CUENTA (e200, 31 oct 2026)
--
-- Pedido de Erick: una cuenta nueva nace en Básico CON su mes gratis, también
-- con los planes apagados (antes el mes gratis empezaba al encenderlos). Las
-- empresas de una Cartera reciben el suyo según las reglas (cuántas entran
-- gratis, RUC nuevo y su tope).
--
--   · fn_planes_cuenta_nueva y planes_prueba_empresa_nueva: ya no esperan a
--     que los planes estén encendidos.
--   · Las cuentas y empresas que nacieron sin mes gratis lo reciben hoy.
--   · plan_efectivo_base(empresa): lo que vería con los planes encendidos
--     (para «Ajustar»: «con los planes encendidos vería…»).
--   · planes_resumen_cuenta: + el dueño de la cuenta (nombre y correo), lo que
--     vería y pagaría con los planes encendidos.
--
-- Solo crea y reemplaza funciones; completa fechas vacías. Nada se borra.
-- Se puede correr dos veces.
-- =============================================================================

BEGIN;

CREATE OR REPLACE FUNCTION plan_efectivo_base(p_empresa uuid) RETURNS text
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_plan   text;
    v_prueba timestamptz;
    v_cuenta uuid;
    v_total  boolean;
    v_mejor  text;
BEGIN
    SELECT e.plan, e.prueba_hasta, e.cuenta_id, COALESCE(c.acceso_total, false)
      INTO v_plan, v_prueba, v_cuenta, v_total
      FROM empresas e LEFT JOIN cuentas c ON c.id = e.cuenta_id
     WHERE e.id = p_empresa;
    IF NOT FOUND THEN RETURN 'basico'; END IF;

    IF v_total THEN RETURN 'todo'; END IF;
    IF v_prueba > now() THEN RETURN 'max'; END IF;
    IF EXISTS (SELECT 1 FROM cuenta_ajustes a
                WHERE a.cuenta_id = v_cuenta AND a.activo AND a.anulado_en IS NULL
                  AND a.tipo = 'ampliar_prueba' AND a.hasta >= current_date) THEN
        RETURN 'max';
    END IF;

    SELECT a.plan_codigo INTO v_mejor
      FROM cuenta_ajustes a JOIN planes p ON p.codigo = a.plan_codigo
     WHERE a.cuenta_id = v_cuenta AND a.activo AND a.anulado_en IS NULL
       AND a.tipo IN ('cortesia', 'plan_mejor') AND a.hasta >= current_date
     ORDER BY p.orden DESC LIMIT 1;

    IF v_mejor IS NOT NULL AND plan_orden(v_mejor) > plan_orden(COALESCE(v_plan, 'basico')) THEN
        RETURN v_mejor;
    END IF;
    RETURN COALESCE(v_plan, 'basico');
END $$;

CREATE OR REPLACE FUNCTION plan_efectivo(p_empresa uuid) RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT CASE WHEN planes_encendidos() THEN plan_efectivo_base(p_empresa) ELSE 'todo' END;
$$;

-- Cuenta nueva: empieza su mes gratis (con los planes encendidos o apagados).
CREATE OR REPLACE FUNCTION fn_planes_cuenta_nueva() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NEW.prueba_hasta IS NULL THEN
        NEW.prueba_hasta := now() + make_interval(days => COALESCE((SELECT dias_prueba FROM plataforma_ajustes WHERE id = 1), 30));
    END IF;
    RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION planes_prueba_empresa_nueva(p_cuenta uuid, p_ruc text, p_excluir uuid)
RETURNS TABLE (hasta timestamptz, motivo text)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    a plataforma_ajustes%ROWTYPE;
    c cuentas%ROWTYPE;
BEGIN
    IF p_cuenta IS NULL THEN RETURN; END IF;
    SELECT * INTO a FROM plataforma_ajustes WHERE id = 1;
    SELECT * INTO c FROM cuentas WHERE id = p_cuenta;
    IF NOT FOUND THEN RETURN; END IF;

    IF c.prueba_hasta > now() THEN
        IF c.tipo = 'negocio'
           OR (SELECT count(*) FROM empresas e
                WHERE e.cuenta_id = p_cuenta AND e.prueba_motivo = 'cuenta'
                  AND e.id IS DISTINCT FROM p_excluir) < a.prueba_cartera_empresas THEN
            hasta := c.prueba_hasta; motivo := 'cuenta'; RETURN NEXT; RETURN;
        END IF;
    ELSIF c.tipo = 'profesional' AND a.ruc_gratis_activo AND p_ruc ~ '^[0-9]{11}$'
          AND NOT EXISTS (SELECT 1 FROM ruc_mes_gratis r WHERE r.ruc = p_ruc)
          AND (SELECT count(*) FROM ruc_mes_gratis r
                WHERE r.cuenta_id = p_cuenta AND r.motivo = 'ruc_nuevo'
                  AND r.creado_en >= date_trunc('month', now())) < a.ruc_gratis_tope THEN
        hasta := now() + make_interval(days => a.dias_prueba); motivo := 'ruc_nuevo'; RETURN NEXT; RETURN;
    END IF;
END $$;

-- Las cuentas (y sus empresas) que nacieron sin mes gratis lo reciben hoy.
CREATE OR REPLACE FUNCTION planes_dar_prueba_pendiente() RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_dias integer := COALESCE((SELECT dias_prueba FROM plataforma_ajustes WHERE id = 1), 30);
    v_c    record;
    v_e    record;
    v_n    integer := 0;
BEGIN
    FOR v_c IN SELECT id FROM cuentas WHERE prueba_hasta IS NULL LOOP
        UPDATE cuentas SET prueba_hasta = now() + make_interval(days => v_dias) WHERE id = v_c.id;
        v_n := v_n + 1;
        FOR v_e IN SELECT id, ruc FROM empresas
                    WHERE cuenta_id = v_c.id AND activo AND prueba_hasta IS NULL
                    ORDER BY creado_en LOOP
            UPDATE empresas e SET prueba_hasta = p.hasta, prueba_motivo = p.motivo
              FROM planes_prueba_empresa_nueva(v_c.id, v_e.ruc, v_e.id) p
             WHERE e.id = v_e.id AND p.hasta IS NOT NULL;
        END LOOP;
    END LOOP;
    RETURN v_n;
END $$;

-- El interruptor: al encenderlo, lo que quedara sin mes gratis y los módulos.
CREATE OR REPLACE FUNCTION fn_planes_interruptor() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NEW.planes_activos IS NOT DISTINCT FROM OLD.planes_activos THEN RETURN NULL; END IF;
    IF NEW.planes_activos THEN
        PERFORM planes_dar_prueba_pendiente();
        PERFORM planes_sincronizar_todas();
    END IF;
    RETURN NULL;
END $$;

CREATE OR REPLACE FUNCTION planes_resumen_cuenta(p_cuenta uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    c        cuentas%ROWTYPE;
    a        plataforma_ajustes%ROWTYPE;
    v_oferta boolean := planes_en_oferta();
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

    SELECT COALESCE(jsonb_agg(jsonb_build_object(
               'id', e.id, 'nombre', e.nombre, 'ruc', e.ruc, 'plan', e.plan,
               'efectivo', plan_efectivo(e.id), 'efectivoEncendido', plan_efectivo_base(e.id),
               'pruebaHasta', e.prueba_hasta, 'pruebaMotivo', e.prueba_motivo,
               'esDelEstudio', e.es_del_estudio, 'pausada', e.pausada_en IS NOT NULL,
               'suspendida', e.suspendida_en IS NOT NULL)
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
          FROM empresas e JOIN planes p ON p.codigo = e.plan
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
          FROM empresas e JOIN planes p ON p.codigo = e.plan
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
        'pruebaHasta', c.prueba_hasta, 'accesoTotal', c.acceso_total, 'accesoTotalMotivo', c.acceso_total_motivo,
        'cupos', c.cartera_cupos, 'usados', v_usados, 'cobradas', v_n, 'minimo', a.cartera_minimo,
        'empresas', v_emp, 'ajustes', v_ajs,
        'estado', v_estado,
        'mensual', jsonb_build_object('base', v_base, 'extras', v_extra, 'total', v_total,
                                      'regular', v_baser + v_extrar,
                                      'anual', round(v_total * (12 - a.anual_meses_gratis), 2)));
END $$;

DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY['plan_efectivo_base(uuid)', 'plan_efectivo(uuid)', 'fn_planes_cuenta_nueva()',
        'planes_prueba_empresa_nueva(uuid, text, uuid)', 'planes_dar_prueba_pendiente()', 'fn_planes_interruptor()',
        'planes_resumen_cuenta(uuid)']
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
    END LOOP;
    GRANT EXECUTE ON FUNCTION plan_efectivo_base(uuid) TO plataforma_app;
    GRANT EXECUTE ON FUNCTION plan_efectivo(uuid) TO plataforma_app;
    GRANT EXECUTE ON FUNCTION planes_resumen_cuenta(uuid) TO plataforma_app;
END $$;

-- Las que ya existían sin mes gratis: empieza hoy.
SELECT planes_dar_prueba_pendiente();

INSERT INTO migraciones_aplicadas (nombre) VALUES ('070_mes_gratis_al_crear')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '070: el mes gratis empieza al crear la cuenta. Nada se borró.';
END $$;