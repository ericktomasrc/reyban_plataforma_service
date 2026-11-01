-- =============================================================================
-- 071 — PLANES: QUÉ SE PRUEBA EN EL MES GRATIS, QUÉ USÓ Y QUÉ LE CONVIENE
--       (e202, 1 nov 2026)
--
--   · plataforma_ajustes.plan_prueba: qué ve una empresa en su mes gratis
--     (Básico, Pro o Max). Empieza en Max: todos prueban todo. Lo cambia el
--     super administrador en «Mes gratis y otras reglas».
--   · planes_uso(empresa): qué usó de lo que no trae el Básico (inventario,
--     compras, caja, lotes, series, almacenes) y el plan que le conviene.
--   · planes_avisos_prueba(): las empresas de mi cuenta cuyo mes gratis
--     termina en 7 días o menos (para la campanita del dueño).
--   · planes_resumen_cuenta: + lo que usó cada empresa y el plan de prueba.
--
-- Solo agrega una columna y reemplaza funciones. Nada se borra. Se puede
-- correr dos veces.
-- =============================================================================

BEGIN;

ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS plan_prueba text NOT NULL DEFAULT 'max';
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_plataforma_ajustes_plan_prueba') THEN
        ALTER TABLE plataforma_ajustes ADD CONSTRAINT ck_plataforma_ajustes_plan_prueba
            CHECK (plan_prueba IN ('basico', 'pro', 'max'));
    END IF;
END $$;
COMMENT ON COLUMN plataforma_ajustes.plan_prueba IS '071: qué ve una empresa en su mes gratis (Básico, Pro o Max).';

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
    IF v_prueba > now() THEN RETURN COALESCE((SELECT plan_prueba FROM plataforma_ajustes WHERE id = 1), 'max'); END IF;
    IF EXISTS (SELECT 1 FROM cuenta_ajustes a
                WHERE a.cuenta_id = v_cuenta AND a.activo AND a.anulado_en IS NULL
                  AND a.tipo = 'ampliar_prueba' AND a.hasta >= current_date) THEN
        RETURN COALESCE((SELECT plan_prueba FROM plataforma_ajustes WHERE id = 1), 'max');
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

-- Lo que usó una empresa de lo que no trae el Básico, y el plan que le conviene.
CREATE OR REPLACE FUNCTION planes_uso(p_empresa uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_inv    boolean;
    v_comp   boolean;
    v_pos    boolean;
    v_lotes  boolean;
    v_series boolean;
    v_alm    boolean;
    v_usos   text[] := ARRAY[]::text[];
BEGIN
    IF NOT ctx_es_super() AND p_empresa IS DISTINCT FROM ctx_empresa()
       AND NOT EXISTS (SELECT 1 FROM empresas e JOIN usuarios u ON u.cuenta_id = e.cuenta_id
                        WHERE e.id = p_empresa AND u.id = ctx_usuario()) THEN
        RETURN NULL;
    END IF;
    v_inv    := EXISTS (SELECT 1 FROM inv_operaciones WHERE empresa_id = p_empresa AND activo);
    v_comp   := EXISTS (SELECT 1 FROM compras        WHERE empresa_id = p_empresa AND activo);
    v_pos    := EXISTS (SELECT 1 FROM pos_ventas     WHERE empresa_id = p_empresa);
    v_lotes  := EXISTS (SELECT 1 FROM inv_lotes      WHERE empresa_id = p_empresa AND lote <> '');
    v_series := EXISTS (SELECT 1 FROM inv_series     WHERE empresa_id = p_empresa AND activo);
    v_alm    := EXISTS (SELECT 1 FROM inv_almacenes  WHERE empresa_id = p_empresa AND activo);
    IF v_inv    THEN v_usos := v_usos || 'Inventario'; END IF;
    IF v_comp   THEN v_usos := v_usos || 'Compras y gastos'; END IF;
    IF v_pos    THEN v_usos := v_usos || 'Punto de venta'; END IF;
    IF v_lotes  THEN v_usos := v_usos || 'Lotes y vencimientos'; END IF;
    IF v_series THEN v_usos := v_usos || 'Números de serie'; END IF;
    IF v_alm    THEN v_usos := v_usos || 'Almacenes'; END IF;
    RETURN jsonb_build_object(
        'usos', to_jsonb(v_usos),
        'recomendado', CASE WHEN v_pos OR v_lotes OR v_series OR v_alm THEN 'max'
                            WHEN v_inv OR v_comp THEN 'pro' ELSE 'basico' END);
END $$;

-- Las empresas de mi cuenta cuyo mes gratis termina en 7 días o menos (solo
-- para quien elige el plan: el dueño o un colaborador administrador).
CREATE OR REPLACE FUNCTION planes_avisos_prueba() RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE(jsonb_agg(jsonb_build_object(
               'id', e.id, 'nombre', COALESCE(e.nombre, e.razon_social, ''), 'hasta', e.prueba_hasta,
               'plan', e.plan, 'recomendado', planes_uso(e.id)->>'recomendado', 'usos', planes_uso(e.id)->'usos')
             ORDER BY e.prueba_hasta), '[]'::jsonb)
      FROM empresas e JOIN cuentas c ON c.id = e.cuenta_id
     WHERE e.cuenta_id = planes_mi_cuenta_admin() AND e.activo AND NOT c.acceso_total
       AND e.prueba_hasta > now() AND e.prueba_hasta <= now() + interval '7 days';
$$;

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
               'uso', planes_uso(e.id),
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
        'planPrueba', COALESCE(a.plan_prueba, 'max'),
        'pruebaHasta', c.prueba_hasta, 'accesoTotal', c.acceso_total, 'accesoTotalMotivo', c.acceso_total_motivo,
        'cupos', c.cartera_cupos, 'usados', v_usados, 'cobradas', v_n, 'minimo', a.cartera_minimo,
        'empresas', v_emp, 'ajustes', v_ajs,
        'estado', v_estado,
        'mensual', jsonb_build_object('base', v_base, 'extras', v_extra, 'total', v_total,
                                      'regular', v_baser + v_extrar,
                                      'anual', round(v_total * (12 - a.anual_meses_gratis), 2)));
END $$;

-- El catálogo, con el plan de prueba.
CREATE OR REPLACE FUNCTION planes_catalogo() RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT jsonb_build_object(
            'encendidos', planes_encendidos(),
            'enOferta', planes_en_oferta(),
            'reglas', (SELECT jsonb_build_object(
                'diasPrueba', a.dias_prueba, 'pruebaCarteraEmpresas', a.prueba_cartera_empresas,
                'rucGratisActivo', a.ruc_gratis_activo, 'rucGratisTope', a.ruc_gratis_tope,
                'carteraMinimo', a.cartera_minimo, 'ofertaHasta', a.oferta_hasta,
                'anualMesesGratis', a.anual_meses_gratis, 'planPrueba', a.plan_prueba) FROM plataforma_ajustes a WHERE a.id = 1),
            'planes', (SELECT jsonb_agg(jsonb_build_object(
                'codigo', p.codigo, 'nombre', p.nombre, 'orden', p.orden, 'paraQuien', p.para_quien,
                'precioRegular', p.precio_regular, 'precioOferta', p.precio_oferta,
                'sucursalesMax', p.sucursales_max,
                'carteraExtraRegular', p.cartera_extra_regular, 'carteraExtraOferta', p.cartera_extra_oferta,
                'funciones', (SELECT COALESCE(jsonb_agg(jsonb_build_object('menu', f.menu, 'nombre', f.nombre)
                                ORDER BY f.menu_orden, f.orden), '[]'::jsonb)
                                FROM funciones f
                               WHERE f.activo AND NOT f.oculta AND f.plan_minimo = p.codigo))
                ORDER BY p.orden) FROM planes p),
            'tramos', (SELECT jsonb_agg(jsonb_build_object('desde', t.desde, 'hasta', t.hasta,
                        'precioOferta', t.precio_oferta, 'precioRegular', t.precio_regular) ORDER BY t.desde)
                         FROM cartera_tramos t WHERE t.activo));
$$;

DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY['planes_catalogo()', 'plan_efectivo_base(uuid)', 'planes_uso(uuid)', 'planes_avisos_prueba()', 'planes_resumen_cuenta(uuid)']
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
    END LOOP;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('071_plan_de_prueba')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '071: qué se prueba en el mes gratis (hoy: %), qué usó y qué le conviene. Nada se borró.',
        (SELECT plan_prueba FROM plataforma_ajustes WHERE id = 1);
END $$;