-- =============================================================================
-- 085 — TOPE DE COMPROBANTES AL MES Y PAQUETES EXTRA (8 nov 2026, e234)
--
-- Cada empresa (cada RUC) puede emitir hasta un tope de facturas y boletas al
-- mes, según su plan. Todo automático, sin que la plataforma haga nada:
--
--   · Tope por plan, normal y de lanzamiento (quien entró en la oferta lo
--     conserva igual que el precio). Valores iniciales:
--         Básico 3,000 (lanzamiento 4,000) · Pro 4,500 (6,000) · Max 9,000 (12,000)
--   · Avisos al 80 %, 90 % y 95 % y al llegar (campanita y correo, una vez cada uno).
--   · Al llegar, no emite facturas ni boletas nuevas hasta el 1 del mes que
--     viene, salvo que compre un PAQUETE EXTRA (+1,000 por S/ 10, normal S/ 15).
--     El paquete se cobra a su tarjeta y suma saldo al quedar pagado.
--   · El saldo del paquete NO VENCE: se gasta solo cuando ya se pasó el tope
--     del mes, y lo que sobra pasa al mes siguiente.
--   · No cuentan: notas de crédito y de débito, guías, rechazados ni borradores.
--     No se bloquean las notas de crédito ni las anulaciones.
--   · Sin tope: planes apagados, acceso total, y la empresa de la plataforma.
--   · Se cuenta en el ambiente actual de la empresa (beta y producción aparte)
--     y por el día en que se emitió (hora de Lima).
--
-- Se puede correr dos veces. Nada se borra.
-- =============================================================================
BEGIN;

-- -----------------------------------------------------------------------------
-- 1. LO QUE SE CONFIGURA
-- -----------------------------------------------------------------------------
ALTER TABLE planes ADD COLUMN IF NOT EXISTS tope_regular integer;
ALTER TABLE planes ADD COLUMN IF NOT EXISTS tope_oferta  integer;
ALTER TABLE planes DROP CONSTRAINT IF EXISTS ck_planes_tope;
ALTER TABLE planes ADD CONSTRAINT ck_planes_tope
    CHECK ((tope_regular IS NULL OR tope_regular BETWEEN 1 AND 10000000)
       AND (tope_oferta  IS NULL OR tope_oferta  BETWEEN 1 AND 10000000));
COMMENT ON COLUMN planes.tope_regular IS '085: facturas y boletas al mes por empresa (precio normal). NULL = sin tope.';
COMMENT ON COLUMN planes.tope_oferta  IS '085: el tope con precio de lanzamiento. NULL = el normal.';
UPDATE planes SET tope_regular = 3000, tope_oferta = 4000  WHERE codigo = 'basico' AND tope_regular IS NULL;
UPDATE planes SET tope_regular = 4500, tope_oferta = 6000  WHERE codigo = 'pro'    AND tope_regular IS NULL;
UPDATE planes SET tope_regular = 9000, tope_oferta = 12000 WHERE codigo = 'max'    AND tope_regular IS NULL;

ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS paquete_cantidad       integer       NOT NULL DEFAULT 1000;
ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS paquete_precio_regular numeric(10,2) NOT NULL DEFAULT 15;
ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS paquete_precio_oferta  numeric(10,2) NOT NULL DEFAULT 10;
ALTER TABLE plataforma_ajustes DROP CONSTRAINT IF EXISTS ck_plataforma_ajustes_paquete;
ALTER TABLE plataforma_ajustes ADD CONSTRAINT ck_plataforma_ajustes_paquete
    CHECK (paquete_cantidad BETWEEN 1 AND 1000000
       AND paquete_precio_regular BETWEEN 3 AND 100000 AND paquete_precio_oferta BETWEEN 3 AND 100000);

-- -----------------------------------------------------------------------------
-- 2. LO QUE SE GUARDA
-- -----------------------------------------------------------------------------
ALTER TABLE empresas ADD COLUMN IF NOT EXISTS paquete_saldo integer NOT NULL DEFAULT 0;
ALTER TABLE empresas DROP CONSTRAINT IF EXISTS ck_empresas_paquete_saldo;
ALTER TABLE empresas ADD CONSTRAINT ck_empresas_paquete_saldo CHECK (paquete_saldo >= 0);
COMMENT ON COLUMN empresas.paquete_saldo IS '085: comprobantes de paquetes extra que le quedan (no vencen).';

-- Cuándo salió de verdad (para contar el mes): lo pone la base al emitir.
ALTER TABLE documentos ADD COLUMN IF NOT EXISTS emitido_en timestamptz;
COMMENT ON COLUMN documentos.emitido_en IS '085: cuándo se entregó al servicio (lo pone la base). Para el tope del mes.';
CREATE INDEX IF NOT EXISTS ix_documentos_tope ON documentos (empresa_id, emitido_en)
    WHERE tipo IN ('01', '03');

ALTER TABLE cobros ADD COLUMN IF NOT EXISTS paquete_empresa  uuid REFERENCES empresas (id);
ALTER TABLE cobros ADD COLUMN IF NOT EXISTS paquete_cantidad integer;
ALTER TABLE cobros DROP CONSTRAINT IF EXISTS ck_cobros_paquete;
ALTER TABLE cobros ADD CONSTRAINT ck_cobros_paquete
    CHECK ((paquete_empresa IS NULL) = (paquete_cantidad IS NULL)
       AND (paquete_empresa IS NULL OR (periodo = 'otro' AND paquete_cantidad > 0)));

ALTER TABLE cobros_avisos DROP CONSTRAINT IF EXISTS ck_cobros_avisos_tipo;
ALTER TABLE cobros_avisos ADD CONSTRAINT ck_cobros_avisos_tipo
    CHECK (tipo IN ('previo', 'pagado', 'rechazado', 'gracia', 'no_emite', 'cancelado', 'tope'));

-- -----------------------------------------------------------------------------
-- 3. CUÁNTOS LLEVA Y CUÁNTOS PUEDE
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION comprobantes_mes_inicio() RETURNS timestamptz
LANGUAGE sql STABLE AS $$
    SELECT (date_trunc('month', now() AT TIME ZONE 'America/Lima')) AT TIME ZONE 'America/Lima';
$$;

CREATE OR REPLACE FUNCTION comprobantes_emitidos_mes(p_empresa uuid) RETURNS integer
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT count(*)::integer
      FROM documentos d JOIN empresas e ON e.id = d.empresa_id
     WHERE d.empresa_id = p_empresa
       AND d.tipo IN ('01', '03')
       AND d.estado IN ('encolado', 'aceptado', 'observado', 'anulado')
       AND d.emitido_en >= comprobantes_mes_inicio()
       AND COALESCE(d.ambiente, e.ambiente, 'beta') = COALESCE(e.ambiente, 'beta');
$$;

-- El tope de este mes (NULL = sin tope) y si es el de lanzamiento. Interna.
CREATE OR REPLACE FUNCTION comprobantes_tope_de(p_empresa uuid, OUT tope integer, OUT tope_normal integer, OUT oferta boolean)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid;
    v_plan   text;
    p        planes%ROWTYPE;
BEGIN
    oferta := false;
    IF NOT planes_encendidos() THEN RETURN; END IF;
    IF p_empresa IS NOT DISTINCT FROM (SELECT empresa_propia FROM plataforma_ajustes WHERE id = 1) THEN RETURN; END IF;
    SELECT cuenta_id INTO v_cuenta FROM empresas WHERE id = p_empresa;
    IF v_cuenta IS NULL THEN RETURN; END IF;
    v_plan := plan_efectivo(p_empresa);
    SELECT * INTO p FROM planes WHERE codigo = v_plan;
    IF NOT FOUND THEN RETURN; END IF;               -- 'todo' (acceso total): sin tope
    oferta := planes_en_oferta()
              OR COALESCE((SELECT lanzamiento_hasta >= current_date FROM cuentas WHERE id = v_cuenta), false);
    tope_normal := p.tope_regular;
    tope := CASE WHEN oferta THEN COALESCE(p.tope_oferta, p.tope_regular) ELSE p.tope_regular END;
END $$;

-- Todo lo de una empresa: lo que lleva, su tope, su saldo y el paquete.
CREATE OR REPLACE FUNCTION comprobantes_tope(p_empresa uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    t       record;
    a       plataforma_ajustes%ROWTYPE;
    v_n     integer;
    v_saldo integer;
    v_disp  integer;
    v_cap   integer;
BEGIN
    IF NOT ctx_es_super() AND p_empresa IS DISTINCT FROM ctx_empresa()
       AND NOT EXISTS (SELECT 1 FROM empresas e JOIN usuarios u ON u.cuenta_id = e.cuenta_id
                        WHERE e.id = p_empresa AND u.id = ctx_usuario() AND u.activo) THEN
        RETURN NULL;
    END IF;
    t := comprobantes_tope_de(p_empresa);
    IF t.tope IS NULL THEN
        RETURN jsonb_build_object('aplica', false);
    END IF;
    SELECT * INTO a FROM plataforma_ajustes WHERE id = 1;
    v_n     := comprobantes_emitidos_mes(p_empresa);
    v_saldo := (SELECT paquete_saldo FROM empresas WHERE id = p_empresa);
    v_disp  := GREATEST(t.tope - v_n, 0) + v_saldo;
    v_cap   := v_n + v_disp;
    RETURN jsonb_build_object(
        'aplica', true,
        'emitidos', v_n, 'tope', t.tope, 'topeNormal', t.tope_normal, 'oferta', t.oferta,
        'saldo', v_saldo, 'disponible', v_disp,
        'porcentaje', CASE WHEN v_cap = 0 THEN 100 ELSE floor(v_n * 100.0 / v_cap)::integer END,
        'bloqueado', v_disp <= 0,
        'reinicia', ((date_trunc('month', now() AT TIME ZONE 'America/Lima') + interval '1 month')::date),
        'paquete', jsonb_build_object('cantidad', a.paquete_cantidad,
                       'precio', CASE WHEN t.oferta THEN a.paquete_precio_oferta ELSE a.paquete_precio_regular END,
                       'precioNormal', a.paquete_precio_regular));
END $$;

-- La API lo pregunta antes de emitir: NULL si puede; si no, lo de arriba.
-- Con un documento, solo las facturas y boletas se frenan (las notas no).
CREATE OR REPLACE FUNCTION comprobantes_tope_bloquea(p_empresa uuid, p_documento uuid) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE q jsonb;
BEGIN
    IF p_documento IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM documentos WHERE id = p_documento AND empresa_id = p_empresa AND tipo IN ('01', '03')) THEN
        RETURN NULL;
    END IF;
    q := comprobantes_tope(p_empresa);
    IF q IS NULL OR NOT (q->>'aplica')::boolean OR NOT (q->>'bloqueado')::boolean THEN
        RETURN NULL;
    END IF;
    RETURN q;
END $$;

-- -----------------------------------------------------------------------------
-- 4. AL EMITIR: la hora de salida y, pasado el tope, se gasta el saldo.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION fn_documentos_emitido_en() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.emitido_en IS NULL AND NEW.estado IN ('encolado', 'aceptado', 'observado', 'rechazado', 'anulado') THEN
        NEW.emitido_en := now();
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS b_documentos_emitido_en ON documentos;
CREATE TRIGGER b_documentos_emitido_en BEFORE INSERT OR UPDATE OF estado ON documentos
    FOR EACH ROW EXECUTE FUNCTION fn_documentos_emitido_en();

CREATE OR REPLACE FUNCTION fn_documentos_gasta_paquete() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE t record;
BEGIN
    IF NEW.tipo IN ('01', '03') AND NEW.emitido_en IS NOT NULL
       AND (TG_OP = 'INSERT' OR OLD.emitido_en IS NULL)
       AND NEW.estado IN ('encolado', 'aceptado', 'observado') THEN
        t := comprobantes_tope_de(NEW.empresa_id);
        IF t.tope IS NOT NULL AND comprobantes_emitidos_mes(NEW.empresa_id) > t.tope THEN
            UPDATE empresas SET paquete_saldo = paquete_saldo - 1
             WHERE id = NEW.empresa_id AND paquete_saldo > 0;
        END IF;
    END IF;
    RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS z_documentos_gasta_paquete ON documentos;
CREATE TRIGGER z_documentos_gasta_paquete AFTER INSERT OR UPDATE OF estado ON documentos
    FOR EACH ROW EXECUTE FUNCTION fn_documentos_gasta_paquete();

-- -----------------------------------------------------------------------------
-- 5. COMPRAR UN PAQUETE (con la tarjeta, como «Mis pagos › Pagar»)
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION comprobantes_paquete_preparar(p_empresa uuid, p_modo text, p_tarjeta uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
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
END $$;

-- Al quedar pagado, suma su saldo (también si lo confirma después el aviso de
-- Culqi). Si un pago se anula, lo quita (sin bajar de cero).
CREATE OR REPLACE FUNCTION fn_cobros_paquete() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NEW.paquete_empresa IS NULL THEN RETURN NULL; END IF;
    IF NEW.estado = 'pagado' AND (TG_OP = 'INSERT' OR OLD.estado IS DISTINCT FROM 'pagado') THEN
        UPDATE empresas SET paquete_saldo = paquete_saldo + NEW.paquete_cantidad WHERE id = NEW.paquete_empresa;
    ELSIF TG_OP = 'UPDATE' AND OLD.estado = 'pagado' AND NEW.estado IS DISTINCT FROM 'pagado' THEN
        UPDATE empresas SET paquete_saldo = GREATEST(paquete_saldo - NEW.paquete_cantidad, 0) WHERE id = NEW.paquete_empresa;
    END IF;
    RETURN NULL;
END $$;
DROP TRIGGER IF EXISTS z_cobros_paquete ON cobros;
CREATE TRIGGER z_cobros_paquete AFTER INSERT OR UPDATE OF estado ON cobros
    FOR EACH ROW EXECUTE FUNCTION fn_cobros_paquete();

-- -----------------------------------------------------------------------------
-- 6. PARA LA PLATAFORMA: el uso de todas y los avisos que faltan mandar.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION comprobantes_uso_empresas() RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'PLAN_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN (SELECT COALESCE(jsonb_object_agg(e.id, jsonb_build_object(
                'emitidos', (q->>'emitidos')::integer, 'tope', (q->>'tope')::integer, 'saldo', (q->>'saldo')::integer,
                'porcentaje', (q->>'porcentaje')::integer, 'bloqueado', (q->>'bloqueado')::boolean,
                'paquetes', (SELECT count(*) FROM cobros x WHERE x.paquete_empresa = e.id AND x.estado = 'pagado'))), '{}'::jsonb)
              FROM empresas e CROSS JOIN LATERAL (SELECT comprobantes_tope(e.id) AS q) z
             WHERE e.activo AND (q->>'aplica')::boolean);
END $$;

-- Las empresas que llegaron a un nivel de aviso este mes y aún no se les
-- escribió (cobros_avisos: tipo 'tope', clave empresa:mes:nivel).
CREATE OR REPLACE FUNCTION comprobantes_avisos_pendientes() RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v_mes text := to_char(now() AT TIME ZONE 'America/Lima', 'YYYY-MM');
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN (SELECT COALESCE(jsonb_agg(x), '[]'::jsonb) FROM (
        SELECT jsonb_build_object('cuenta', e.cuenta_id, 'empresa', e.id,
                   'nombre', COALESCE(e.razon_social, e.nombre, ''), 'nivel', n.nivel,
                   'clave', e.id || ':' || v_mes || ':' || n.nivel,
                   'emitidos', (q->>'emitidos')::integer, 'capacidad', (q->>'emitidos')::integer + (q->>'disponible')::integer,
                   'disponible', (q->>'disponible')::integer, 'reinicia', q->>'reinicia', 'paquete', q->'paquete') AS x
          FROM empresas e
          CROSS JOIN LATERAL (SELECT comprobantes_tope(e.id) AS q) z
          CROSS JOIN LATERAL (SELECT max(v) AS nivel FROM unnest(ARRAY[80, 90, 95, 100]) v
                               WHERE (q->>'porcentaje')::integer >= v) n
         WHERE e.activo AND e.cuenta_id IS NOT NULL AND (q->>'aplica')::boolean AND n.nivel IS NOT NULL
           AND NOT EXISTS (SELECT 1 FROM cobros_avisos v WHERE v.cuenta_id = e.cuenta_id AND v.tipo = 'tope'
                              AND v.clave = e.id || ':' || v_mes || ':' || n.nivel)
    ) s);
END $$;

-- planes_catalogo: con los topes y el paquete (para la página de precios y Mi plan).
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
                'paquetePrecioOferta', a.paquete_precio_oferta) FROM plataforma_ajustes a WHERE a.id = 1),
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

-- -----------------------------------------------------------------------------
-- 7. DUEÑO Y PERMISOS
-- -----------------------------------------------------------------------------
DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY[
        'comprobantes_emitidos_mes(uuid)', 'comprobantes_tope_de(uuid)', 'comprobantes_tope(uuid)',
        'comprobantes_tope_bloquea(uuid, uuid)', 'fn_documentos_gasta_paquete()',
        'comprobantes_paquete_preparar(uuid, text, uuid)', 'fn_cobros_paquete()',
        'comprobantes_uso_empresas()', 'comprobantes_avisos_pendientes()', 'planes_catalogo()']
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        IF f IN ('comprobantes_tope(uuid)', 'comprobantes_tope_bloquea(uuid, uuid)',
                 'comprobantes_paquete_preparar(uuid, text, uuid)', 'comprobantes_uso_empresas()',
                 'comprobantes_avisos_pendientes()', 'planes_catalogo()') THEN
            EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
        END IF;
    END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION comprobantes_mes_inicio() TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('085_tope_de_comprobantes')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '085: tope de comprobantes al mes por empresa, avisos y paquetes extra que no vencen. Nada se borró.';
END $$;