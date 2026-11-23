-- =============================================================================
-- 096 — PREMIO POR INVITAR CONTADORES (e281, 22 nov 2026)
--
-- Cada Cuenta Profesional tiene su enlace para invitar a otros contadores
-- (/registro?tipo=profesional&invita=CODIGO). El contador nuevo queda anotado
-- como invitado por quien lo trajo (también puede escribir el código en su Plan
-- Cartera durante su primer mes).
--
-- EL PREMIO LLEGA SOLO DESPUÉS DE QUE EL NUEVO PAGÓ N MESES (así nunca se
-- regala antes de cobrar): quien invitó recibe M meses gratis, como un
-- «descuento» de 100 % con fecha de fin y tope de empresas (el mismo trato
-- especial de la 095), que se ve en Planes y cobros › Ajustar. Tope de meses
-- de premio al año por cuenta. Lo revisa el ciclo de cobros cada hora.
--
-- Todo configurable en Planes y cobros › Premios por invitar:
--   invitar_activo          encendido / apagado            (apagado al migrar)
--   invitar_premio_meses    meses gratis por cada contador         (1)
--   invitar_meses_pagados   meses que debe pagar el nuevo          (3)
--   invitar_tope_anual      máximo de meses de premio al año       (6)
--   invitar_empresas_max    hasta cuántas empresas cubre el premio (20)
--
-- Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS invitar_activo        boolean NOT NULL DEFAULT false;
ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS invitar_premio_meses  integer NOT NULL DEFAULT 1;
ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS invitar_meses_pagados integer NOT NULL DEFAULT 3;
ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS invitar_tope_anual    integer NOT NULL DEFAULT 6;
ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS invitar_empresas_max  integer NOT NULL DEFAULT 20;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ajustes_invitar') THEN
        ALTER TABLE plataforma_ajustes ADD CONSTRAINT ck_ajustes_invitar CHECK (
            invitar_premio_meses BETWEEN 1 AND 12 AND invitar_meses_pagados BETWEEN 1 AND 24
            AND invitar_tope_anual BETWEEN 0 AND 24 AND invitar_empresas_max BETWEEN 1 AND 10000);
    END IF;
END $$;

ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS codigo_invitacion text;
ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS invitado_por      uuid REFERENCES cuentas (id);
ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS invitado_en       timestamptz;
CREATE UNIQUE INDEX IF NOT EXISTS ux_cuentas_codigo_invitacion ON cuentas (lower(codigo_invitacion));
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_cuentas_invitado') THEN
        ALTER TABLE cuentas ADD CONSTRAINT ck_cuentas_invitado CHECK (invitado_por IS NULL OR invitado_por <> id);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS invitaciones_premios (
    id          uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    invitador   uuid        NOT NULL REFERENCES cuentas (id),
    invitado    uuid        NOT NULL REFERENCES cuentas (id),
    meses       integer     NOT NULL,
    hasta       date        NOT NULL,
    ajuste_id   uuid        REFERENCES cuenta_ajustes (id),
    creado_en   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_invitaciones_premios_invitado UNIQUE (invitado),
    CONSTRAINT ck_invitaciones_premios CHECK (meses >= 1)
);
COMMENT ON TABLE invitaciones_premios IS '096: premio ganado por invitar a un contador (uno por invitado). Solo inserción.';
DO $$ BEGIN
    ALTER TABLE invitaciones_premios ENABLE ROW LEVEL SECURITY;
    ALTER TABLE invitaciones_premios FORCE ROW LEVEL SECURITY;
    DROP POLICY IF EXISTS p_invitaciones_premios ON invitaciones_premios;
    CREATE POLICY p_invitaciones_premios ON invitaciones_premios
        USING (ctx_es_super() OR invitador = (SELECT u.cuenta_id FROM usuarios u WHERE u.id = ctx_usuario()))
        WITH CHECK (ctx_es_super());
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_invitaciones_premios') THEN
        PERFORM vigilar_tabla_inmutable('invitaciones_premios');
    END IF;
END $$;
GRANT SELECT ON invitaciones_premios TO plataforma_app;

-- --- Cuántos meses pagó una cuenta (suscripción: mes, año o varios meses) ------
CREATE OR REPLACE FUNCTION invitar_meses_pagados(p_cuenta uuid) RETURNS integer
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE(sum(CASE x.periodo WHEN 'mensual' THEN 1 WHEN 'anual' THEN 12
                             ELSE GREATEST(round((x.periodo_hasta - x.periodo_desde + 1) / 30.4375), 1) END), 0)::integer
      FROM cobros x
     WHERE x.cuenta_id = p_cuenta AND x.estado = 'pagado' AND x.modo <> 'simulado'
       AND cobros_es_suscripcion(x.periodo, x.plan_empresa, x.paquete_empresa);
$$;

-- --- Mi enlace para invitar (se crea la primera vez) -------------------------
CREATE OR REPLACE FUNCTION invitar_mi_codigo() RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := (SELECT u.cuenta_id FROM usuarios u WHERE u.id = ctx_usuario() AND u.activo);
    v_cod    text;
    v_base   text;
    n        integer := 1;
BEGIN
    IF v_cuenta IS NULL OR (SELECT tipo FROM cuentas WHERE id = v_cuenta) <> 'profesional' THEN
        RAISE EXCEPTION 'PLAN_INVITAR: Invitar es de la Cuenta Profesional.';
    END IF;
    SELECT codigo_invitacion INTO v_cod FROM cuentas WHERE id = v_cuenta FOR UPDATE;
    IF v_cod IS NOT NULL THEN RETURN v_cod; END IF;
    -- Del alias del dueño (ana, ana2…); si no hay, unas letras al azar.
    SELECT lower(regexp_replace(COALESCE(u.alias, ''), '[^a-zA-Z0-9]', '', 'g')) INTO v_base
      FROM usuarios u WHERE u.cuenta_id = v_cuenta AND u.nivel_cuenta = 'dueno' AND u.activo ORDER BY u.creado_en LIMIT 1;
    IF COALESCE(length(v_base), 0) < 3 THEN v_base := 'c' || substr(md5(v_cuenta::text), 1, 6); END IF;
    v_base := left(v_base, 24);
    v_cod := v_base;
    WHILE EXISTS (SELECT 1 FROM cuentas WHERE lower(codigo_invitacion) = v_cod) LOOP
        n := n + 1; v_cod := v_base || n;
    END LOOP;
    UPDATE cuentas SET codigo_invitacion = v_cod WHERE id = v_cuenta;
    RETURN v_cod;
END $$;

-- --- «Me invitó…»: el contador nuevo anota quién lo trajo (su primer mes) -----
CREATE OR REPLACE FUNCTION invitar_anotar(p_codigo text) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := planes_mi_cuenta_admin();
    c        cuentas%ROWTYPE;
    v_inv    cuentas%ROWTYPE;
BEGIN
    IF v_cuenta IS NULL THEN RAISE EXCEPTION 'PLAN_INVITAR: Lo anota el dueño de la cuenta.'; END IF;
    SELECT * INTO c FROM cuentas WHERE id = v_cuenta FOR UPDATE;
    IF c.tipo <> 'profesional' THEN RAISE EXCEPTION 'PLAN_INVITAR: Las invitaciones son entre Cuentas Profesionales.'; END IF;
    IF c.invitado_por IS NOT NULL THEN RAISE EXCEPTION 'PLAN_INVITAR: Ya anotaste quién te invitó.'; END IF;
    IF c.creado_en < now() - interval '45 days' THEN
        RAISE EXCEPTION 'PLAN_INVITAR: El código de invitación se anota en los primeros 45 días de la cuenta.';
    END IF;
    SELECT * INTO v_inv FROM cuentas WHERE lower(codigo_invitacion) = lower(btrim(COALESCE(p_codigo, ''))) AND tipo = 'profesional';
    IF NOT FOUND THEN RAISE EXCEPTION 'PLAN_INVITAR: Ese código no existe. Revisa que esté bien escrito.'; END IF;
    IF v_inv.id = v_cuenta THEN RAISE EXCEPTION 'PLAN_INVITAR: No puedes invitarte a ti mismo.'; END IF;
    IF v_inv.invitado_por = v_cuenta THEN RAISE EXCEPTION 'PLAN_INVITAR: Esa cuenta ya fue invitada por ti.'; END IF;
    UPDATE cuentas SET invitado_por = v_inv.id, invitado_en = now() WHERE id = v_cuenta;
    RETURN v_inv.titular_nombre;
END $$;

-- --- Lo que ve quien invita ----------------------------------------------------
CREATE OR REPLACE FUNCTION invitar_mio() RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := (SELECT u.cuenta_id FROM usuarios u WHERE u.id = ctx_usuario() AND u.activo);
    a plataforma_ajustes%ROWTYPE;
    c cuentas%ROWTYPE;
BEGIN
    IF v_cuenta IS NULL THEN RETURN NULL; END IF;
    SELECT * INTO a FROM plataforma_ajustes WHERE id = 1;
    SELECT * INTO c FROM cuentas WHERE id = v_cuenta;
    RETURN jsonb_build_object(
        'activo', a.invitar_activo, 'premioMeses', a.invitar_premio_meses, 'mesesPagados', a.invitar_meses_pagados,
        'topeAnual', a.invitar_tope_anual, 'empresasMax', a.invitar_empresas_max,
        'codigo', c.codigo_invitacion,
        'invitadoPor', (SELECT x.titular_nombre FROM cuentas x WHERE x.id = c.invitado_por),
        'puedeAnotar', c.tipo = 'profesional' AND c.invitado_por IS NULL AND c.creado_en >= now() - interval '45 days',
        'ganadosAnio', (SELECT COALESCE(sum(p.meses), 0) FROM invitaciones_premios p
                         WHERE p.invitador = v_cuenta AND p.creado_en > now() - interval '365 days'),
        'invitados', COALESCE((SELECT jsonb_agg(jsonb_build_object(
                         'nombre', x.titular_nombre, 'desde', x.invitado_en,
                         'pagados', LEAST(invitar_meses_pagados(x.id), a.invitar_meses_pagados),
                         'premio', (SELECT jsonb_build_object('meses', p.meses, 'hasta', p.hasta, 'en', p.creado_en)
                                      FROM invitaciones_premios p WHERE p.invitado = x.id))
                       ORDER BY x.invitado_en DESC)
                     FROM cuentas x WHERE x.invitado_por = v_cuenta), '[]'::jsonb));
END $$;

-- --- EL PREMIO: lo revisa el ciclo de cobros (cada hora) ----------------------
CREATE OR REPLACE FUNCTION invitar_dar_premios() RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    a      plataforma_ajustes%ROWTYPE;
    r      record;
    v_n    integer := 0;
    v_desde date;
    v_hasta date;
    v_meses integer;
    v_aj    uuid;
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'PLAN_INVITAR: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    SELECT * INTO a FROM plataforma_ajustes WHERE id = 1;
    IF NOT a.invitar_activo THEN RETURN 0; END IF;
    FOR r IN
        SELECT x.id AS invitado, x.invitado_por AS invitador, x.titular_nombre AS nombre
          FROM cuentas x
         WHERE x.invitado_por IS NOT NULL
           AND NOT EXISTS (SELECT 1 FROM invitaciones_premios p WHERE p.invitado = x.id)
           AND invitar_meses_pagados(x.id) >= a.invitar_meses_pagados
           AND (SELECT i.activo AND i.estado = 'activa' AND i.cancelado_en IS NULL FROM cuentas i WHERE i.id = x.invitado_por)
         ORDER BY x.invitado_en
         LIMIT 200
    LOOP
        PERFORM 1 FROM cuentas WHERE id = r.invitador FOR UPDATE;
        -- Tope de meses de premio al año.
        v_meses := LEAST(a.invitar_premio_meses,
                         a.invitar_tope_anual - COALESCE((SELECT sum(p.meses) FROM invitaciones_premios p
                                                          WHERE p.invitador = r.invitador AND p.creado_en > now() - interval '365 days'), 0));
        IF v_meses <= 0 THEN CONTINUE; END IF;
        -- Desde su próximo cobro (o detrás de un premio que ya tenga).
        v_desde := GREATEST(COALESCE(cobros_fecha_cobro(r.invitador), current_date), current_date,
                            COALESCE((SELECT max(p.hasta) + 1 FROM invitaciones_premios p WHERE p.invitador = r.invitador AND p.hasta >= current_date), current_date));
        v_hasta := (v_desde + make_interval(months => v_meses))::date - 1;
        INSERT INTO cuenta_ajustes (cuenta_id, tipo, porcentaje, empresas_max, hasta, motivo)
        VALUES (r.invitador, 'descuento', 100, a.invitar_empresas_max, v_hasta,
                left('Premio por invitar a ' || COALESCE(r.nombre, 'un contador') || ': ' || v_meses || ' mes' || CASE WHEN v_meses = 1 THEN '' ELSE 'es' END || ' gratis', 500))
        RETURNING id INTO v_aj;
        INSERT INTO invitaciones_premios (invitador, invitado, meses, hasta, ajuste_id)
        VALUES (r.invitador, r.invitado, v_meses, v_hasta, v_aj);
        v_n := v_n + 1;
    END LOOP;
    RETURN v_n;
END $$;

-- --- La lista del super admin -----------------------------------------------
CREATE OR REPLACE FUNCTION invitar_lista_admin() RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE a plataforma_ajustes%ROWTYPE;
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'PLAN_INVITAR: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    SELECT * INTO a FROM plataforma_ajustes WHERE id = 1;
    RETURN jsonb_build_object(
        'reglas', jsonb_build_object('activo', a.invitar_activo, 'premioMeses', a.invitar_premio_meses,
                                     'mesesPagados', a.invitar_meses_pagados, 'topeAnual', a.invitar_tope_anual,
                                     'empresasMax', a.invitar_empresas_max),
        'filas', COALESCE((SELECT jsonb_agg(jsonb_build_object(
                    'invitador', i.titular_nombre, 'invitadorId', i.id, 'invitado', x.titular_nombre, 'invitadoId', x.id,
                    'desde', x.invitado_en, 'pagados', invitar_meses_pagados(x.id),
                    'premio', (SELECT jsonb_build_object('meses', p.meses, 'hasta', p.hasta, 'en', p.creado_en)
                                 FROM invitaciones_premios p WHERE p.invitado = x.id))
                  ORDER BY x.invitado_en DESC)
                  FROM cuentas x JOIN cuentas i ON i.id = x.invitado_por), '[]'::jsonb));
END $$;

DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY['invitar_meses_pagados(uuid)', 'invitar_mi_codigo()', 'invitar_anotar(text)', 'invitar_mio()',
                             'invitar_dar_premios()', 'invitar_lista_admin()']
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
    END LOOP;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('096_invitar_contadores')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '096: premio por invitar contadores (apagado: se enciende en Planes y cobros › Premios por invitar). Nada se borró.';
END $$;