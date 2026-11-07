-- =============================================================================
-- 079 — LA OFERTA, CON INTERRUPTOR, Y EL PRECIO DE LANZAMIENTO POR 12 MESES
-- (7 nov 2026, e218)
--
-- ANTES: la oferta valía mientras no pasara «oferta_hasta», y si esa fecha
-- quedaba vacía, la oferta era PARA SIEMPRE. Al terminar, TODOS pasaban al
-- precio real, también quien había entrado con el de lanzamiento.
--
-- AHORA:
--   · plataforma_ajustes.oferta_activa: el interruptor. La oferta vale si está
--     encendido Y no pasó la fecha (sin fecha: hasta que se apague).
--   · cuentas.lanzamiento_hasta: quien se registra mientras hay oferta conserva
--     el precio de lanzamiento N meses PAGADOS (lanzamiento_meses, 12 por
--     defecto), aunque la oferta termine: al crearse, desde que termina su mes
--     gratis; y con su PRIMER PAGO, desde el día que cubre ese pago. El super
--     administrador la amplía o la quita.
--   · planes_resumen_cuenta (y con ella cobros_monto): usa el precio de
--     lanzamiento si hay oferta hoy o si la cuenta lo guarda.
--
-- Nada se borra. Se puede correr dos veces.
-- =============================================================================
BEGIN;

ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS oferta_activa     boolean NOT NULL DEFAULT true;
ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS lanzamiento_meses integer NOT NULL DEFAULT 12;
ALTER TABLE cuentas            ADD COLUMN IF NOT EXISTS lanzamiento_hasta date;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ajustes_lanzamiento_meses') THEN
        ALTER TABLE plataforma_ajustes ADD CONSTRAINT ck_ajustes_lanzamiento_meses CHECK (lanzamiento_meses BETWEEN 0 AND 60);
    END IF;
END $$;

-- La oferta de hoy: encendida y sin pasar la fecha.
CREATE OR REPLACE FUNCTION planes_en_oferta() RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE((SELECT oferta_activa AND (oferta_hasta IS NULL OR current_date <= oferta_hasta)
                       FROM plataforma_ajustes WHERE id = 1), false);
$$;

-- Al crear una cuenta durante la oferta: guarda el precio de lanzamiento N meses.
CREATE OR REPLACE FUNCTION fn_cuentas_lanzamiento() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v_meses integer; v_dias integer;
BEGIN
    IF NEW.lanzamiento_hasta IS NULL AND planes_en_oferta() THEN
        SELECT lanzamiento_meses, dias_prueba INTO v_meses, v_dias FROM plataforma_ajustes WHERE id = 1;
        IF COALESCE(v_meses, 0) > 0 THEN
            -- N meses pagando: empiezan cuando termina su mes gratis.
            NEW.lanzamiento_hasta := ((now() AT TIME ZONE 'America/Lima')::date + COALESCE(v_dias, 0)
                                      + make_interval(months => v_meses))::date - 1;
        END IF;
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS b_cuentas_lanzamiento ON cuentas;
CREATE TRIGGER b_cuentas_lanzamiento BEFORE INSERT ON cuentas FOR EACH ROW EXECUTE FUNCTION fn_cuentas_lanzamiento();

-- Con su PRIMER PAGO, los N meses se cuentan desde el día que cubre ese pago
-- (por si pagó tarde: el año de lanzamiento es un año pagando, completo).
CREATE OR REPLACE FUNCTION fn_cobros_lanzamiento() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v_meses integer;
BEGIN
    IF NEW.estado = 'pagado' AND (TG_OP = 'INSERT' OR OLD.estado IS DISTINCT FROM 'pagado')
       AND NOT EXISTS (SELECT 1 FROM cobros x WHERE x.cuenta_id = NEW.cuenta_id AND x.estado = 'pagado' AND x.id <> NEW.id) THEN
        SELECT lanzamiento_meses INTO v_meses FROM plataforma_ajustes WHERE id = 1;
        UPDATE cuentas
           SET lanzamiento_hasta = GREATEST(lanzamiento_hasta, (NEW.periodo_desde + make_interval(months => COALESCE(v_meses, 12)))::date - 1)
         WHERE id = NEW.cuenta_id AND lanzamiento_hasta IS NOT NULL;
    END IF;
    RETURN NULL;
END $$;
ALTER FUNCTION fn_cobros_lanzamiento() OWNER TO plataforma_puertas;
DROP TRIGGER IF EXISTS a_cobros_lanzamiento ON cobros;
CREATE TRIGGER a_cobros_lanzamiento AFTER INSERT OR UPDATE OF estado ON cobros FOR EACH ROW EXECUTE FUNCTION fn_cobros_lanzamiento();

-- Las cuentas que ya existen y entraron con la oferta vigente: 12 meses desde que se crearon.
UPDATE cuentas c
   SET lanzamiento_hasta = (GREATEST((c.creado_en AT TIME ZONE 'America/Lima')::date, (c.prueba_hasta AT TIME ZONE 'America/Lima')::date)
                            + interval '12 months')::date - 1
 WHERE c.lanzamiento_hasta IS NULL AND planes_en_oferta();

-- El super administrador amplía, acorta o quita el precio de lanzamiento de una cuenta.
CREATE OR REPLACE FUNCTION planes_fijar_lanzamiento(p_cuenta uuid, p_hasta date) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'PLAN_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    UPDATE cuentas SET lanzamiento_hasta = p_hasta WHERE id = p_cuenta;
    IF NOT FOUND THEN RAISE EXCEPTION 'PLAN_NO_PUEDE: No existe esa cuenta.' USING ERRCODE = 'no_data_found'; END IF;
END $$;

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
        'lanzamientoHasta', c.lanzamiento_hasta, 'ofertaGeneral', planes_en_oferta(),
        'planPrueba', COALESCE(a.plan_prueba, 'max'),
        'pruebaHasta', c.prueba_hasta, 'accesoTotal', c.acceso_total, 'accesoTotalMotivo', c.acceso_total_motivo,
        'cupos', c.cartera_cupos, 'usados', v_usados, 'cobradas', v_n, 'minimo', a.cartera_minimo,
        'empresas', v_emp, 'ajustes', v_ajs,
        'estado', v_estado,
        'mensual', jsonb_build_object('base', v_base, 'extras', v_extra, 'total', v_total,
                                      'regular', v_baser + v_extrar,
                                      'anual', round(v_total * (12 - a.anual_meses_gratis), 2)));
END $$;

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
    FOREACH f IN ARRAY ARRAY['planes_en_oferta()', 'planes_fijar_lanzamiento(uuid, date)', 'planes_resumen_cuenta(uuid)', 'planes_catalogo()'] LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
    END LOOP;
END $$;
ALTER FUNCTION fn_cuentas_lanzamiento() OWNER TO plataforma_puertas;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('079_oferta_y_lanzamiento')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '079: la oferta con interruptor y el precio de lanzamiento por 12 meses para quien entra en ella. Nada se borró.';
END $$;