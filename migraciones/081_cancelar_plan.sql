-- =============================================================================
-- 081 — CANCELAR MI PLAN (7 nov 2026, e221)
--
-- La página de precios promete «cambia de plan o cancela cuando quieras;
-- sigues usando lo que pagaste hasta el final del periodo», e Indecopi pide
-- que darse de baja sea tan fácil como contratar.
--
--   · cuentas.cancelado_en / cancelado_motivo.
--   · planes_cancelar(motivo)  el dueño (o colaborador administrador) cancela:
--                              no se le cobra más ni recibe avisos de cobro.
--                              Sigue usando todo hasta lo que pagó (o hasta el
--                              fin de su mes gratis).
--   · Pasada esa fecha: pago_estado «cancelado» → entra, ve y descarga todo,
--     pero no emite. NADA SE BORRA.
--   · planes_reactivar()       vuelve a la normalidad (y pagar también reactiva).
--   · Arreglo: planes_uso() fallaba con empresas que usan inventario, compras
--     o caja (Mi plan no cargaba para ellas).
--
-- Se puede correr dos veces.
-- =============================================================================
BEGIN;

ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS cancelado_en     timestamptz;
ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS cancelado_motivo text;
ALTER TABLE cuentas DROP CONSTRAINT IF EXISTS ck_cuentas_pago_estado;
ALTER TABLE cuentas ADD CONSTRAINT ck_cuentas_pago_estado
    CHECK (pago_estado IN ('al_dia', 'reintento', 'gracia', 'no_emite', 'cancelado'));

ALTER TABLE cobros_avisos DROP CONSTRAINT IF EXISTS ck_cobros_avisos_tipo;
ALTER TABLE cobros_avisos ADD CONSTRAINT ck_cobros_avisos_tipo
    CHECK (tipo IN ('previo', 'pagado', 'rechazado', 'gracia', 'no_emite', 'cancelado'));

-- Cancelar (la cuenta de quien tiene la sesión). Devuelve hasta cuándo sigue activo.
CREATE OR REPLACE FUNCTION planes_cancelar(p_motivo text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v_cuenta uuid := planes_mi_cuenta_admin(); c cuentas%ROWTYPE;
BEGIN
    IF v_cuenta IS NULL THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: Solo el dueño de la cuenta (o un colaborador administrador) puede cancelar el plan.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;
    SELECT * INTO c FROM cuentas WHERE id = v_cuenta FOR UPDATE;
    IF c.cancelado_en IS NOT NULL THEN
        RAISE EXCEPTION 'PLAN_DATOS: Tu plan ya está cancelado desde el %.', to_char(c.cancelado_en AT TIME ZONE 'America/Lima', 'DD/MM/YYYY');
    END IF;
    UPDATE cuentas SET cancelado_en = now(), cancelado_motivo = left(NULLIF(trim(COALESCE(p_motivo, '')), ''), 300)
     WHERE id = v_cuenta;
    RETURN jsonb_build_object('cuenta', v_cuenta, 'hasta', cobros_fecha_cobro(v_cuenta) - 1,
                              'correo', cobros_correo_cuenta(v_cuenta));
END $$;

-- Volver a activar (antes o después de la fecha).
CREATE OR REPLACE FUNCTION planes_reactivar() RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v_cuenta uuid := planes_mi_cuenta_admin();
BEGIN
    IF v_cuenta IS NULL THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: Solo el dueño de la cuenta (o un colaborador administrador) puede reactivar el plan.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;
    UPDATE cuentas SET cancelado_en = NULL, cancelado_motivo = NULL,
                       pago_estado = CASE WHEN pago_estado = 'cancelado' THEN 'al_dia' ELSE pago_estado END
     WHERE id = v_cuenta;
END $$;

-- Pagar también reactiva (quien canceló y vuelve a pagar, sigue normal).
CREATE OR REPLACE FUNCTION fn_cobros_reactiva() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NEW.estado = 'pagado' AND (TG_OP = 'INSERT' OR OLD.estado IS DISTINCT FROM 'pagado') THEN
        UPDATE cuentas SET cancelado_en = NULL, cancelado_motivo = NULL,
                           pago_estado = CASE WHEN pago_estado = 'cancelado' THEN 'al_dia' ELSE pago_estado END
         WHERE id = NEW.cuenta_id AND cancelado_en IS NOT NULL;
    END IF;
    RETURN NULL;
END $$;
ALTER FUNCTION fn_cobros_reactiva() OWNER TO plataforma_puertas;
DROP TRIGGER IF EXISTS a_cobros_reactiva ON cobros;
CREATE TRIGGER a_cobros_reactiva AFTER INSERT OR UPDATE OF estado ON cobros FOR EACH ROW EXECUTE FUNCTION fn_cobros_reactiva();

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
           AND c.cancelado_en IS NULL            -- 081: canceló: no se le cobra más
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
        SELECT c.id, c.pago_estado, cobros_fecha_cobro(c.id) AS fecha, c.cancelado_en IS NOT NULL AS cancelado
          FROM cuentas c
         WHERE c.activo AND c.estado = 'activa'
    LOOP
        -- 081: canceló y ya pasó lo pagado: queda «cancelado» (ve todo, no emite).
        IF planes_encendidos() AND r.cancelado AND r.fecha IS NOT NULL AND r.fecha <= current_date
           AND NOT (SELECT acceso_total FROM cuentas WHERE id = r.id) THEN
            v_nuevo := 'cancelado';
        ELSIF NOT planes_encendidos() OR r.fecha IS NULL OR r.fecha >= current_date
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
                   pago_falla_desde = CASE WHEN v_nuevo IN ('al_dia', 'cancelado') THEN NULL ELSE COALESCE(pago_falla_desde, r.fecha) END
             WHERE id = r.id;
            v_cambios := v_cambios || jsonb_build_object('cuenta', r.id, 'de', r.pago_estado, 'a', v_nuevo,
                                                         'fecha', r.fecha, 'gracia', r.fecha + v_reint + k.gracia_dias);
        END IF;
    END LOOP;
    RETURN v_cambios;
END $$;

CREATE OR REPLACE FUNCTION cobros_bloquea_emision(p_empresa uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE((
        SELECT NOT c.acceso_total AND planes_encendidos()
               AND (c.pago_estado = 'cancelado'      -- 081: canceló: ve todo, no emite
                    OR (c.pago_estado = 'no_emite' AND (SELECT al_vencer FROM cobros_ajustes WHERE id = 1) = 'no_emite'))
          FROM empresas e JOIN cuentas c ON c.id = e.cuenta_id
         WHERE e.id = p_empresa), false);
$$;

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
           AND c.cancelado_en IS NULL
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
        'canceladoEn', c.cancelado_en, 'canceladoMotivo', c.cancelado_motivo, 'pagoEstado', c.pago_estado,
        'activoHasta', cobros_fecha_cobro(c.id) - 1,
        'planPrueba', COALESCE(a.plan_prueba, 'max'),
        'pruebaHasta', c.prueba_hasta, 'accesoTotal', c.acceso_total, 'accesoTotalMotivo', c.acceso_total_motivo,
        'cupos', c.cartera_cupos, 'usados', v_usados, 'cobradas', v_n, 'minimo', a.cartera_minimo,
        'empresas', v_emp, 'ajustes', v_ajs,
        'estado', v_estado,
        'mensual', jsonb_build_object('base', v_base, 'extras', v_extra, 'total', v_total,
                                      'regular', v_baser + v_extrar,
                                      'anual', round(v_total * (12 - a.anual_meses_gratis), 2)));
END $$;

-- Arreglo (de la 071): «text[] || 'Inventario'» fallaba («malformed array
-- literal») en cuanto una empresa usaba el inventario, las compras o la caja,
-- y con eso fallaban «Mi plan», el resumen de la cuenta y el aviso de cobro.
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
    IF v_inv    THEN v_usos := array_append(v_usos, 'Inventario'); END IF;
    IF v_comp   THEN v_usos := array_append(v_usos, 'Compras y gastos'); END IF;
    IF v_pos    THEN v_usos := array_append(v_usos, 'Punto de venta'); END IF;
    IF v_lotes  THEN v_usos := array_append(v_usos, 'Lotes y vencimientos'); END IF;
    IF v_series THEN v_usos := array_append(v_usos, 'Números de serie'); END IF;
    IF v_alm    THEN v_usos := array_append(v_usos, 'Almacenes'); END IF;
    RETURN jsonb_build_object(
        'usos', to_jsonb(v_usos),
        'recomendado', CASE WHEN v_pos OR v_lotes OR v_series OR v_alm THEN 'max'
                            WHEN v_inv OR v_comp THEN 'pro' ELSE 'basico' END);
END $$;

DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY['planes_uso(uuid)', 'planes_cancelar(text)', 'planes_reactivar()', 'cobros_por_cobrar(text)', 'cobros_estados_actualizar()',
                             'cobros_bloquea_emision(uuid)', 'cobros_avisos_pendientes()', 'planes_resumen_cuenta(uuid)'] LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
    END LOOP;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('081_cancelar_plan')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '081: Cancelar mi plan (sigue hasta lo pagado; después ve todo y no emite; se reactiva pagando). Nada se borró.';
END $$;