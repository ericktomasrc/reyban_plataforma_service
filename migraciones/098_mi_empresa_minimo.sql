-- =============================================================================
-- 098 — MI EMPRESA SOLO CON 2 O MÁS EN LA CARTERA (e283, 27 nov 2026)
--
-- La empresa del propio estudio («Mi empresa», Cuenta Profesional) se activa
-- cuando su cartera llega al mínimo (plataforma_ajustes.cartera_minimo, 2).
--
--   · sin_activar: nunca llegó al mínimo → no se abre ni emite (candado).
--   · activa:      tiene el mínimo o más → Básico gratis (y Pro/Max pagando
--                  solo la diferencia, como en la 095).
--   · aviso:       bajó del mínimo después de haberlo tenido → sigue emitiendo
--                  unos días (mi_empresa_dias_aviso, 7) con aviso.
--   · no_emite:    pasados esos días → ve y descarga todo, pero no emite.
--                  Vuelve sola a «activa» al tener otra vez el mínimo.
--   Las empresas de los clientes (la cartera) NO se tocan: pagan el mínimo.
--   Con los planes apagados o con acceso total no se bloquea nada.
--
--   · mi_empresa_meses_gratis: 0 = Básico gratis sin fecha de fin (así viene).
--     Con N > 0, pasados N meses desde que se activó, Mi empresa se cobra
--     como una empresa más de la cartera.
--   · cuentas.cartera_minimo_desde (primera vez con el mínimo) y
--     cuentas.cartera_bajo_desde (desde cuándo está por debajo): los pone un
--     disparador en empresas (agregar, pausar, reanudar, marcar como suya).
--
-- Se puede correr dos veces. Nada se borra.
-- =============================================================================

BEGIN;

ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS mi_empresa_meses_gratis integer NOT NULL DEFAULT 0;
ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS mi_empresa_dias_aviso integer NOT NULL DEFAULT 7;
ALTER TABLE plataforma_ajustes DROP CONSTRAINT IF EXISTS ck_ajustes_mi_empresa;
ALTER TABLE plataforma_ajustes ADD CONSTRAINT ck_ajustes_mi_empresa
    CHECK (mi_empresa_meses_gratis BETWEEN 0 AND 120 AND mi_empresa_dias_aviso BETWEEN 0 AND 60);
COMMENT ON COLUMN plataforma_ajustes.mi_empresa_meses_gratis IS '098: meses de Básico gratis para Mi empresa (0 = sin fecha de fin).';
COMMENT ON COLUMN plataforma_ajustes.mi_empresa_dias_aviso IS '098: días de aviso antes de que Mi empresa deje de emitir si la cartera baja del mínimo.';

ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS cartera_minimo_desde timestamptz;
ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS cartera_bajo_desde timestamptz;
COMMENT ON COLUMN cuentas.cartera_minimo_desde IS '098: la primera vez que la cartera llegó al mínimo (se activó Mi empresa).';
COMMENT ON COLUMN cuentas.cartera_bajo_desde IS '098: desde cuándo la cartera está por debajo del mínimo (después de haberlo tenido).';

-- -----------------------------------------------------------------------------
-- RECALCULAR (lo llama el disparador). Solo Cuenta Profesional.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION estudio_recalcular(p_cuenta uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_min integer := (SELECT cartera_minimo FROM plataforma_ajustes WHERE id = 1);
    v_n   integer;
BEGIN
    IF p_cuenta IS NULL OR NOT EXISTS (SELECT 1 FROM cuentas WHERE id = p_cuenta AND tipo = 'profesional') THEN
        RETURN;
    END IF;
    v_n := planes_cupos_usados(p_cuenta);
    IF v_n >= v_min THEN
        UPDATE cuentas SET cartera_minimo_desde = COALESCE(cartera_minimo_desde, now()), cartera_bajo_desde = NULL
         WHERE id = p_cuenta AND (cartera_minimo_desde IS NULL OR cartera_bajo_desde IS NOT NULL);
    ELSE
        UPDATE cuentas SET cartera_bajo_desde = now()
         WHERE id = p_cuenta AND cartera_minimo_desde IS NOT NULL AND cartera_bajo_desde IS NULL;
    END IF;
END $$;
REVOKE ALL ON FUNCTION estudio_recalcular(uuid) FROM PUBLIC;

CREATE OR REPLACE FUNCTION fn_estudio_recalcular() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.cuenta_id IS DISTINCT FROM OLD.cuenta_id THEN
        PERFORM estudio_recalcular(OLD.cuenta_id);
    END IF;
    PERFORM estudio_recalcular(NEW.cuenta_id);
    RETURN NULL;
END $$;

DROP TRIGGER IF EXISTS estudio_recalcular ON empresas;
CREATE TRIGGER estudio_recalcular
    AFTER INSERT OR UPDATE OF activo, pausada_en, es_del_estudio, cuenta_id ON empresas
    FOR EACH ROW EXECUTE FUNCTION fn_estudio_recalcular();

-- Si el super admin cambia el mínimo, se recalculan todas las Cuentas Profesionales.
CREATE OR REPLACE FUNCTION fn_estudio_recalcular_todas() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    PERFORM estudio_recalcular(c.id) FROM cuentas c WHERE c.tipo = 'profesional';
    RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS estudio_recalcular_todas ON plataforma_ajustes;
CREATE TRIGGER estudio_recalcular_todas
    AFTER UPDATE OF cartera_minimo ON plataforma_ajustes
    FOR EACH ROW WHEN (NEW.cartera_minimo IS DISTINCT FROM OLD.cartera_minimo)
    EXECUTE FUNCTION fn_estudio_recalcular_todas();

-- Las cuentas de hoy: con el mínimo, Mi empresa queda activa desde ahora.
UPDATE cuentas c SET cartera_minimo_desde = now()
 WHERE c.tipo = 'profesional' AND c.cartera_minimo_desde IS NULL
   AND planes_cupos_usados(c.id) >= (SELECT cartera_minimo FROM plataforma_ajustes WHERE id = 1);

-- -----------------------------------------------------------------------------
-- EL ESTADO DE MI EMPRESA DE UNA CUENTA (para Mi empresa, Mi cartera y la
-- sesión). NULL si no es Cuenta Profesional.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION estudio_estado_interno(p_cuenta uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    c       cuentas%ROWTYPE;
    a       plataforma_ajustes%ROWTYPE;
    v_n     integer;
    v_est   uuid;
    v_estado text;
    v_hasta timestamptz;
BEGIN
    SELECT * INTO c FROM cuentas WHERE id = p_cuenta;
    IF NOT FOUND OR c.tipo <> 'profesional' THEN RETURN NULL; END IF;
    SELECT * INTO a FROM plataforma_ajustes WHERE id = 1;
    v_n := planes_cupos_usados(p_cuenta);
    SELECT e.id INTO v_est FROM empresas e WHERE e.cuenta_id = p_cuenta AND e.activo AND e.es_del_estudio LIMIT 1;

    IF NOT planes_encendidos() OR c.acceso_total OR v_n >= a.cartera_minimo THEN
        v_estado := 'activa';
    ELSIF c.cartera_minimo_desde IS NULL THEN
        v_estado := 'sin_activar';
    ELSE
        v_hasta := COALESCE(c.cartera_bajo_desde, now()) + make_interval(days => a.mi_empresa_dias_aviso);
        v_estado := CASE WHEN now() < v_hasta THEN 'aviso' ELSE 'no_emite' END;
    END IF;

    RETURN jsonb_build_object(
        'estado', v_estado, 'tiene', v_n, 'minimo', a.cartera_minimo, 'faltan', GREATEST(a.cartera_minimo - v_n, 0),
        'empresaId', v_est, 'hasta', v_hasta, 'diasAviso', a.mi_empresa_dias_aviso,
        'mesesGratis', a.mi_empresa_meses_gratis, 'activadaEn', c.cartera_minimo_desde,
        'gratisHasta', CASE WHEN a.mi_empresa_meses_gratis > 0 AND c.cartera_minimo_desde IS NOT NULL
                            THEN c.cartera_minimo_desde + make_interval(months => a.mi_empresa_meses_gratis) END,
        'encendidos', planes_encendidos());
END $$;
REVOKE ALL ON FUNCTION estudio_estado_interno(uuid) FROM PUBLIC;

-- Lo mismo, solo para la propia cuenta (o el super admin).
CREATE OR REPLACE FUNCTION estudio_estado_cuenta(p_cuenta uuid) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT CASE WHEN ctx_es_super()
                  OR p_cuenta = (SELECT u.cuenta_id FROM usuarios u WHERE u.id = ctx_usuario())
                THEN estudio_estado_interno(p_cuenta) END
$$;
REVOKE ALL ON FUNCTION estudio_estado_cuenta(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION estudio_estado_cuenta(uuid) TO plataforma_app;

-- -----------------------------------------------------------------------------
-- ¿ESTA EMPRESA ES «MI EMPRESA» Y NO PUEDE EMITIR? Devuelve el estado
-- (sin_activar | no_emite) o NULL si puede. Lo usa el middleware al emitir y
-- al abrir la empresa.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION estudio_bloquea(p_empresa uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid;
    r jsonb;
BEGIN
    SELECT e.cuenta_id INTO v_cuenta FROM empresas e WHERE e.id = p_empresa AND e.es_del_estudio AND e.activo;
    IF v_cuenta IS NULL THEN RETURN NULL; END IF;
    r := estudio_estado_interno(v_cuenta);
    IF r IS NULL OR r->>'estado' NOT IN ('sin_activar', 'no_emite') THEN RETURN NULL; END IF;
    RETURN r;
END $$;
REVOKE ALL ON FUNCTION estudio_bloquea(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION estudio_bloquea(uuid) TO plataforma_app;


-- -----------------------------------------------------------------------------
-- LO QUE PAGA UNA CUENTA (095), con Mi empresa cobrada si se acabaron sus
-- meses gratis, y su estado.
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
        -- 098: pasados los meses gratis de Mi empresa (si no es «sin fin»), su
        -- Básico se cobra como una empresa más de la cartera.
        IF a.mi_empresa_meses_gratis > 0 AND c.cartera_minimo_desde IS NOT NULL
           AND now() >= c.cartera_minimo_desde + make_interval(months => a.mi_empresa_meses_gratis)
           AND EXISTS (SELECT 1 FROM empresas e WHERE e.cuenta_id = p_cuenta AND e.activo AND e.pausada_en IS NULL AND e.es_del_estudio) THEN
            v_extra_est := v_extra_est + planes_precio_cartera(v_n + 1, NOT v_oferta) - planes_precio_cartera(v_n, NOT v_oferta);
            v_extrar := v_extrar + planes_precio_cartera(v_n + 1, true) - planes_precio_cartera(v_n, true);
        END IF;
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
        -- 098: Mi empresa (Cuenta Profesional): activa, sin_activar, aviso o no_emite.
        'estudio', estudio_estado_interno(c.id),
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


-- planes_catalogo (085): con las reglas de Mi empresa.
CREATE OR REPLACE FUNCTION planes_catalogo() RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT jsonb_build_object(
            'encendidos', planes_encendidos(),
            'enOferta', planes_en_oferta(),
            'reglas', (SELECT jsonb_build_object(
                'diasPrueba', a.dias_prueba, 'pruebaCarteraEmpresas', a.prueba_cartera_empresas,
                'rucGratisActivo', a.ruc_gratis_activo, 'rucGratisTope', a.ruc_gratis_tope,
                'carteraMinimo', a.cartera_minimo, 'ofertaHasta', a.oferta_hasta, 'ofertaActiva', a.oferta_activa,
                'lanzamientoMeses', a.lanzamiento_meses,
                'anualMesesGratis', a.anual_meses_gratis, 'planPrueba', a.plan_prueba,
                'paqueteCantidad', a.paquete_cantidad, 'paquetePrecioRegular', a.paquete_precio_regular,
                'paquetePrecioOferta', a.paquete_precio_oferta,
                -- 098: Mi empresa (Cuenta Profesional).
                'miEmpresaMesesGratis', a.mi_empresa_meses_gratis, 'miEmpresaDiasAviso', a.mi_empresa_dias_aviso) FROM plataforma_ajustes a WHERE a.id = 1),
            'planes', (SELECT jsonb_agg(jsonb_build_object(
                'codigo', p.codigo, 'nombre', p.nombre, 'orden', p.orden, 'paraQuien', p.para_quien,
                'precioRegular', p.precio_regular, 'precioOferta', p.precio_oferta,
                'sucursalesMax', p.sucursales_max,
                'carteraExtraRegular', p.cartera_extra_regular, 'carteraExtraOferta', p.cartera_extra_oferta,
                'topeRegular', p.tope_regular, 'topeOferta', p.tope_oferta,
                'funciones', (SELECT COALESCE(jsonb_agg(jsonb_build_object('menu', f.menu, 'nombre', f.nombre)
                                ORDER BY f.menu_orden, f.orden), '[]'::jsonb)
                                FROM funciones f
                               WHERE f.activo AND NOT f.oculta AND f.plan_minimo = p.codigo))
                ORDER BY p.orden) FROM planes p),
            'tramos', (SELECT jsonb_agg(jsonb_build_object('desde', t.desde, 'hasta', t.hasta,
                        'precioOferta', t.precio_oferta, 'precioRegular', t.precio_regular) ORDER BY t.desde)
                         FROM cartera_tramos t WHERE t.activo));
$$;

-- Las funciones con SECURITY DEFINER son del dueño de las puertas (027).
DO $$ BEGIN
    ALTER FUNCTION estudio_recalcular(uuid) OWNER TO plataforma_puertas;
    ALTER FUNCTION fn_estudio_recalcular() OWNER TO plataforma_puertas;
    ALTER FUNCTION fn_estudio_recalcular_todas() OWNER TO plataforma_puertas;
    ALTER FUNCTION estudio_estado_interno(uuid) OWNER TO plataforma_puertas;
    ALTER FUNCTION estudio_estado_cuenta(uuid) OWNER TO plataforma_puertas;
    ALTER FUNCTION estudio_bloquea(uuid) OWNER TO plataforma_puertas;
    ALTER FUNCTION planes_resumen_cuenta(uuid) OWNER TO plataforma_puertas;
    ALTER FUNCTION planes_catalogo() OWNER TO plataforma_puertas;
    REVOKE ALL ON FUNCTION estudio_recalcular(uuid) FROM PUBLIC;
    REVOKE ALL ON FUNCTION estudio_estado_interno(uuid) FROM PUBLIC;
    GRANT EXECUTE ON FUNCTION estudio_estado_cuenta(uuid) TO plataforma_app;
    GRANT EXECUTE ON FUNCTION estudio_bloquea(uuid) TO plataforma_app;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('098_mi_empresa_minimo')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '098: Mi empresa se activa con 2 o más empresas en la cartera. Nada se borró.';
END $$;