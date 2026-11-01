-- =============================================================================
-- 073 — COBROS, PRIMERA PARTE (e208, 1 nov 2026)
--
-- El pago de los planes con tarjeta (Culqi) o por transferencia. Lo acordado
-- está en «planes-y-cobros.md».
--
--   · cobros_ajustes   una sola fila: el PANEL DE PAGOS de la plataforma.
--                      El modo (apagado / simulado / culqi), los reintentos,
--                      la gracia, la empresa que emite el comprobante y sus
--                      series, los medios de pago aceptados y los datos de la
--                      cuenta bancaria para las transferencias.
--                      LAS LLAVES DE CULQI NO ESTÁN AQUÍ NI EN NINGUNA TABLA:
--                      viven solo en el .env del servidor.
--   · cobros_datos     los datos del comprobante de cada cuenta: RUC (10 o 20,
--                      factura) o DNI (boleta), nombre, dirección y correo.
--   · cobros_tarjetas  las tarjetas guardadas. NUNCA el número: solo la marca,
--                      los cuatro últimos y el código que da Culqi. Cada
--                      tarjeta es de un modo (simulado, prueba o real) y solo
--                      sirve en ese modo.
--   · cobros           cada pago o intento de pago, con su periodo y lo que
--                      hará falta para emitir el comprobante (parte 2).
--   · cobros_eventos   los avisos que llegan de Culqi (webhook), valgan o no.
--   · cuentas          + estado de pago (al día, reintento, gracia, no emite)
--                      y la fecha del próximo cobro.
--
-- EL MONTO LO CALCULA SIEMPRE LA BASE (cobros_monto), nunca la pantalla.
--
-- Solo crea. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

-- -----------------------------------------------------------------------------
-- 1. ¿ES UN RUC VÁLIDO? (11 dígitos, empieza en 10, 15, 17 o 20, y su dígito
--    verificador cuadra — el mismo cálculo que hace SUNAT).
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION ruc_valido(p text) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE AS $$
DECLARE
    w int[] := ARRAY[5,4,3,2,7,6,5,4,3,2];
    s int := 0;
    d int;
BEGIN
    IF p IS NULL OR p !~ '^(10|15|17|20)[0-9]{9}$' THEN RETURN false; END IF;
    FOR i IN 1..10 LOOP
        s := s + substr(p, i, 1)::int * w[i];
    END LOOP;
    d := 11 - (s % 11);
    IF d = 10 THEN d := 0; ELSIF d = 11 THEN d := 1; END IF;
    RETURN d = substr(p, 11, 1)::int;
END $$;

-- -----------------------------------------------------------------------------
-- 2. EL PANEL DE PAGOS (una fila)
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS cobros_ajustes (
    id                    smallint     PRIMARY KEY DEFAULT 1,
    -- apagado: nadie paga con tarjeta. simulado: tarjetas de mentira (bloqueado
    -- en producción). culqi: Culqi de verdad; prueba o real lo dice la llave.
    modo                  text         NOT NULL DEFAULT 'simulado',
    reintento_dias        integer[]    NOT NULL DEFAULT ARRAY[3, 5],
    gracia_dias           integer      NOT NULL DEFAULT 7,
    aviso_dias            integer      NOT NULL DEFAULT 3,
    al_vencer             text         NOT NULL DEFAULT 'no_emite',
    emisora_empresa       uuid         REFERENCES empresas(id),
    serie_factura         text,
    serie_boleta          text,
    enviar_correo         boolean      NOT NULL DEFAULT true,
    descripcion           text         NOT NULL DEFAULT 'Plan {plan} — {periodo}',
    acepta_tarjeta        boolean      NOT NULL DEFAULT true,
    acepta_transferencia  boolean      NOT NULL DEFAULT false,
    acepta_yape           boolean      NOT NULL DEFAULT false,
    transferencia_datos   text,
    correo_pagos          text,
    ultimo_evento_en      timestamptz,
    prueba_en             timestamptz,
    prueba_ok             boolean,
    prueba_detalle        text,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_cobros_ajustes_una  CHECK (id = 1),
    CONSTRAINT ck_cobros_ajustes_modo CHECK (modo IN ('apagado', 'simulado', 'culqi')),
    CONSTRAINT ck_cobros_ajustes_reintentos CHECK (cardinality(reintento_dias) <= 5),
    CONSTRAINT ck_cobros_ajustes_gracia CHECK (gracia_dias BETWEEN 0 AND 30),
    CONSTRAINT ck_cobros_ajustes_aviso  CHECK (aviso_dias BETWEEN 0 AND 15),
    CONSTRAINT ck_cobros_ajustes_vencer CHECK (al_vencer IN ('no_emite', 'solo_aviso')),
    CONSTRAINT ck_cobros_ajustes_sf CHECK (serie_factura IS NULL OR serie_factura ~ '^F[A-Z0-9]{3}$'),
    CONSTRAINT ck_cobros_ajustes_sb CHECK (serie_boleta  IS NULL OR serie_boleta  ~ '^B[A-Z0-9]{3}$'),
    CONSTRAINT ck_cobros_ajustes_desc CHECK (length(btrim(descripcion)) BETWEEN 5 AND 120),
    CONSTRAINT ck_cobros_ajustes_banco CHECK (transferencia_datos IS NULL OR length(transferencia_datos) <= 600),
    CONSTRAINT ck_cobros_ajustes_correo CHECK (correo_pagos IS NULL OR correo_pagos ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$')
);
INSERT INTO cobros_ajustes (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

-- -----------------------------------------------------------------------------
-- 3. LOS DATOS DEL COMPROBANTE DE CADA CUENTA
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS cobros_datos (
    id                   uuid  NOT NULL UNIQUE DEFAULT gen_random_uuid(),
    cuenta_id            uuid  PRIMARY KEY REFERENCES cuentas(id),
    tipo_doc             text  NOT NULL,
    numero               text  NOT NULL,
    nombre               text  NOT NULL,
    direccion            text,
    correo               text  NOT NULL,
    telefono             text,
    culqi_cliente_prueba text,
    culqi_cliente_real   text,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_cobros_datos_doc CHECK (
        (tipo_doc = 'ruc' AND ruc_valido(numero)) OR (tipo_doc = 'dni' AND numero ~ '^[0-9]{8}$')),
    CONSTRAINT ck_cobros_datos_nombre CHECK (length(btrim(nombre)) BETWEEN 2 AND 200),
    CONSTRAINT ck_cobros_datos_dir CHECK (direccion IS NULL OR length(direccion) <= 300),
    -- La factura necesita dirección; la boleta no.
    CONSTRAINT ck_cobros_datos_dir_ruc CHECK (tipo_doc <> 'ruc' OR length(btrim(COALESCE(direccion, ''))) >= 5),
    CONSTRAINT ck_cobros_datos_correo CHECK (correo ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$' AND length(correo) <= 200),
    CONSTRAINT ck_cobros_datos_tel CHECK (telefono IS NULL OR telefono ~ '^[0-9+ ]{6,20}$')
);

-- -----------------------------------------------------------------------------
-- 4. LAS TARJETAS GUARDADAS (nunca el número)
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS cobros_tarjetas (
    id           uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    cuenta_id    uuid        NOT NULL REFERENCES cuentas(id),
    modo         text        NOT NULL,
    cliente_ref  text,
    tarjeta_ref  text        NOT NULL,
    marca        text        NOT NULL,
    ultimos4     text        NOT NULL,
    principal    boolean     NOT NULL DEFAULT false,
    quitada_en   timestamptz,
    quitada_por  uuid,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_cobros_tarjetas_modo CHECK (modo IN ('simulado', 'prueba', 'real')),
    CONSTRAINT ck_cobros_tarjetas_ult4 CHECK (ultimos4 ~ '^[0-9]{4}$'),
    CONSTRAINT ck_cobros_tarjetas_marca CHECK (length(marca) BETWEEN 2 AND 40),
    CONSTRAINT ck_cobros_tarjetas_ref CHECK (length(tarjeta_ref) BETWEEN 4 AND 100)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_cobros_tarjetas_ref ON cobros_tarjetas (modo, tarjeta_ref);
CREATE UNIQUE INDEX IF NOT EXISTS ux_cobros_tarjetas_principal ON cobros_tarjetas (cuenta_id, modo)
    WHERE principal AND quitada_en IS NULL;
CREATE INDEX IF NOT EXISTS ix_cobros_tarjetas_cuenta ON cobros_tarjetas (cuenta_id, modo);

-- -----------------------------------------------------------------------------
-- 5. LOS PAGOS
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS cobros (
    id                    uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    numero                bigint       GENERATED ALWAYS AS IDENTITY,
    cuenta_id             uuid         NOT NULL REFERENCES cuentas(id),
    modo                  text         NOT NULL,
    medio                 text         NOT NULL,
    tarjeta_id            uuid         REFERENCES cobros_tarjetas(id),
    monto                 numeric(10,2) NOT NULL,
    moneda                text         NOT NULL DEFAULT 'PEN',
    concepto              text         NOT NULL,
    periodo               text         NOT NULL,
    periodo_desde         date         NOT NULL,
    periodo_hasta         date         NOT NULL,
    estado                text         NOT NULL,
    referencia            text,
    error_codigo          text,
    error_mensaje         text,
    pagado_en             timestamptz,
    comprador_tipo_doc    text,
    comprador_doc         text,
    comprador_nombre      text,
    comprador_direccion   text,
    comprador_correo      text,
    comprobante_tipo      text,
    comprobante_estado    text         NOT NULL DEFAULT 'pendiente',
    comprobante_documento uuid,
    comprobante_numero    text,
    registrado_por        uuid,
    notas                 text,
    anulado_en            timestamptz,
    anulado_por           uuid,
    anulado_motivo        text,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_cobros_modo   CHECK (modo IN ('simulado', 'prueba', 'real', 'manual')),
    CONSTRAINT ck_cobros_medio  CHECK (medio IN ('tarjeta', 'transferencia', 'deposito', 'yape', 'efectivo', 'otro')),
    CONSTRAINT ck_cobros_monto  CHECK (monto > 0 AND monto <= 100000),
    CONSTRAINT ck_cobros_moneda CHECK (moneda = 'PEN'),
    CONSTRAINT ck_cobros_periodo CHECK (periodo IN ('mensual', 'anual', 'otro') AND periodo_hasta >= periodo_desde),
    CONSTRAINT ck_cobros_estado CHECK (estado IN ('procesando', 'pagado', 'rechazado', 'devuelto', 'anulado')),
    CONSTRAINT ck_cobros_pagado CHECK (estado NOT IN ('pagado', 'devuelto') OR pagado_en IS NOT NULL),
    CONSTRAINT ck_cobros_anulado CHECK ((estado = 'anulado') = (anulado_en IS NOT NULL)
                                        AND (anulado_en IS NULL OR length(btrim(COALESCE(anulado_motivo, ''))) >= 3)),
    CONSTRAINT ck_cobros_tarjeta CHECK (medio <> 'tarjeta' OR modo = 'manual' OR tarjeta_id IS NOT NULL),
    CONSTRAINT ck_cobros_manual CHECK (modo <> 'manual' OR registrado_por IS NOT NULL),
    CONSTRAINT ck_cobros_comp_tipo CHECK (comprobante_tipo IS NULL OR comprobante_tipo IN ('01', '03')),
    CONSTRAINT ck_cobros_comp_estado CHECK (comprobante_estado IN ('pendiente', 'emitido', 'error', 'no_aplica')),
    CONSTRAINT ck_cobros_concepto CHECK (length(btrim(concepto)) BETWEEN 3 AND 200),
    CONSTRAINT ck_cobros_notas CHECK (notas IS NULL OR length(notas) <= 500)
);
CREATE INDEX IF NOT EXISTS ix_cobros_cuenta ON cobros (cuenta_id, creado_en DESC);
CREATE INDEX IF NOT EXISTS ix_cobros_creado ON cobros (creado_en DESC);
-- Un solo pago en curso por cuenta: dos clics seguidos no cobran dos veces.
CREATE UNIQUE INDEX IF NOT EXISTS ux_cobros_en_curso ON cobros (cuenta_id) WHERE estado = 'procesando';
-- El código de Culqi no se repite.
CREATE UNIQUE INDEX IF NOT EXISTS ux_cobros_referencia ON cobros (modo, referencia)
    WHERE referencia IS NOT NULL AND modo IN ('prueba', 'real');

-- -----------------------------------------------------------------------------
-- 6. LOS AVISOS DE CULQI (webhook), valgan o no
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS cobros_eventos (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    recibido_en timestamptz NOT NULL DEFAULT now(),
    proveedor   text        NOT NULL DEFAULT 'culqi',
    tipo        text,
    referencia  text,
    valido      boolean     NOT NULL,
    aplicado    boolean     NOT NULL DEFAULT false,
    resultado   text,
    detalle     text,
    ip          text,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,
    CONSTRAINT ck_cobros_eventos_detalle CHECK (detalle IS NULL OR length(detalle) <= 4000)
);
CREATE INDEX IF NOT EXISTS ix_cobros_eventos_fecha ON cobros_eventos (recibido_en DESC);

-- -----------------------------------------------------------------------------
-- 7. EL ESTADO DE PAGO DE LA CUENTA
--    al_dia · reintento (falló la tarjeta, se reintenta) · gracia (días para
--    ponerse al día) · no_emite (puede entrar y ver todo, pero no emitir).
--    Lo mueve el cobro diario (parte 2); el pago lo devuelve a al_dia.
-- -----------------------------------------------------------------------------
ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS pago_estado      text    NOT NULL DEFAULT 'al_dia';
ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS pago_proximo     date;
ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS pago_falla_desde date;
ALTER TABLE cuentas ADD COLUMN IF NOT EXISTS pago_intentos    integer NOT NULL DEFAULT 0;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_cuentas_pago_estado') THEN
        ALTER TABLE cuentas ADD CONSTRAINT ck_cuentas_pago_estado
            CHECK (pago_estado IN ('al_dia', 'reintento', 'gracia', 'no_emite'));
    END IF;
END $$;

-- -----------------------------------------------------------------------------
-- 8. AISLAMIENTO Y BITÁCORA
--    El panel y los avisos: solo la plataforma. Datos, tarjetas y pagos: cada
--    cuenta ve los suyos. Escribir: solo las funciones de abajo (nadie, ni el
--    super administrador, toca estas tablas a mano desde la API).
-- -----------------------------------------------------------------------------
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['cobros_ajustes', 'cobros_eventos'] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = t AND policyname = 'p_' || t) THEN
            EXECUTE format('CREATE POLICY %I ON %I USING (ctx_es_super()) WITH CHECK (ctx_es_super())', 'p_' || t, t);
        END IF;
    END LOOP;

    FOREACH t IN ARRAY ARRAY['cobros_datos', 'cobros_tarjetas', 'cobros'] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = t AND policyname = 'p_' || t) THEN
            EXECUTE format($p$CREATE POLICY %I ON %I
                USING (ctx_es_super() OR cuenta_id = (SELECT u.cuenta_id FROM usuarios u WHERE u.id = ctx_usuario()))
                WITH CHECK (ctx_es_super())$p$, 'p_' || t, t);
        END IF;
    END LOOP;

    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_cobros_ajustes') THEN
        PERFORM vigilar_tabla('cobros_ajustes');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_cobros_datos') THEN
        PERFORM vigilar_tabla('cobros_datos', ARRAY['culqi_cliente_prueba', 'culqi_cliente_real']);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_cobros_tarjetas') THEN
        PERFORM vigilar_tabla('cobros_tarjetas', ARRAY['cliente_ref', 'tarjeta_ref']);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_cobros_eventos') THEN
        PERFORM vigilar_tabla('cobros_eventos', ARRAY['detalle']);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_cobros') THEN
        PERFORM vigilar_tabla('cobros');
    END IF;
END $$;

-- Solo leer: escribir es cosa de las funciones (y nada se borra).
GRANT SELECT ON cobros_ajustes, cobros_datos, cobros_tarjetas, cobros, cobros_eventos TO plataforma_app;

INSERT INTO permisos (codigo, area, descripcion) VALUES
    ('plataforma.cobros_configurar', 'plataforma', 'Planes y cobros: el panel de pagos (modo, reintentos, gracia, empresa que emite y series)'),
    ('plataforma.cobros_registrar',  'plataforma', 'Planes y cobros: registrar pagos por transferencia o depósito, y anularlos')
ON CONFLICT (codigo) DO NOTHING;
UPDATE permisos SET descripcion = 'Planes y cobros: ver las cuentas, su plan y sus pagos'
 WHERE codigo = 'plataforma.planes_ver';

-- -----------------------------------------------------------------------------
-- 9. LAS FUNCIONES
-- -----------------------------------------------------------------------------

-- El nombre del mes, para el concepto («noviembre 2026»).
CREATE OR REPLACE FUNCTION cobros_mes(p date) RETURNS text
LANGUAGE sql IMMUTABLE AS $$
    SELECT (ARRAY['enero','febrero','marzo','abril','mayo','junio','julio','agosto',
                  'septiembre','octubre','noviembre','diciembre'])[extract(month FROM p)::int]
           || ' ' || extract(year FROM p)::int;
$$;

-- El modo guardado en el panel (apagado / simulado / culqi). La API le suma
-- las llaves del .env para saber el modo de verdad.
CREATE OR REPLACE FUNCTION cobros_modo_guardado() RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE((SELECT modo FROM cobros_ajustes WHERE id = 1), 'apagado');
$$;

-- La cuenta del usuario de la sesión, si puede pagar (dueño o colaborador
-- administrador). La misma regla que para elegir el plan.
CREATE OR REPLACE FUNCTION cobros_mi_cuenta() RETURNS uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT planes_mi_cuenta_admin();
$$;

-- CUÁNTO TOCA PAGAR, desde cuándo y hasta cuándo. Es la única fuente del
-- monto: la pantalla lo muestra, el cobro lo usa, y nadie más lo calcula.
--   p_periodo: mensual | anual.
--   p_prueba:  true en los modos simulado y prueba (sin dinero real): deja
--              probar el pago aunque los planes estén apagados.
-- Si no hay nada que pagar, responde con 'nada' y el motivo (no es un error).
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
          FROM empresas e JOIN planes p ON p.codigo = e.plan
         WHERE e.cuenta_id = p_cuenta AND e.activo AND e.pausada_en IS NULL;
        v_plan := COALESCE(v_plan, 'Básico');
    END IF;
    v_conc := replace(replace(k.descripcion, '{plan}', v_plan), '{periodo}', v_per);
    IF p_periodo = 'anual' THEN v_conc := v_conc || ' (anual)'; END IF;

    RETURN jsonb_build_object(
        'nada', false, 'monto', v_monto, 'mensual', round(v_mes, 2), 'periodo', p_periodo,
        'desde', v_desde, 'hasta', v_hasta, 'concepto', left(v_conc, 200), 'adelantado', v_adel,
        'mesesGratisAnual', a.anual_meses_gratis,
        'apagados', NOT planes_encendidos());
END $$;

-- LO QUE VE LA CUENTA en «Mis pagos». p_modo es el modo en que está la
-- plataforma ahora (lo decide la API con las llaves del .env).
CREATE OR REPLACE FUNCTION cobros_mio(p_modo text) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := (SELECT u.cuenta_id FROM usuarios u WHERE u.id = ctx_usuario() AND u.activo);
    v_admin  boolean := cobros_mi_cuenta() IS NOT NULL;
    v_prueba boolean := p_modo IN ('simulado', 'prueba');
    k        cobros_ajustes%ROWTYPE;
    c        cuentas%ROWTYPE;
BEGIN
    IF v_cuenta IS NULL THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Tu usuario no es de una cuenta.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    SELECT * INTO k FROM cobros_ajustes WHERE id = 1;
    SELECT * INTO c FROM cuentas WHERE id = v_cuenta;
    RETURN jsonb_build_object(
        'modo', p_modo,
        'puedePagar', v_admin,
        'medios', jsonb_build_object('tarjeta', k.acepta_tarjeta AND p_modo IN ('simulado', 'prueba', 'real'),
                                     'transferencia', k.acepta_transferencia, 'yape', k.acepta_yape),
        'transferencia', CASE WHEN k.acepta_transferencia THEN k.transferencia_datos END,
        'correoPagos', k.correo_pagos,
        'estado', jsonb_build_object('pago', c.pago_estado, 'proximo', c.pago_proximo, 'fallaDesde', c.pago_falla_desde,
                                     'pruebaHasta', c.prueba_hasta, 'graciaDias', k.gracia_dias,
                                     'reintentos', to_jsonb(k.reintento_dias)),
        'datos', (SELECT jsonb_build_object('tipoDoc', d.tipo_doc, 'numero', d.numero, 'nombre', d.nombre,
                                            'direccion', d.direccion, 'correo', d.correo, 'telefono', d.telefono)
                    FROM cobros_datos d WHERE d.cuenta_id = v_cuenta),
        'sugerido', (SELECT jsonb_build_object('tipoDoc', CASE c.titular_tipo WHEN 'juridica' THEN 'ruc' ELSE 'dni' END,
                                               'numero', c.titular_documento, 'nombre', c.titular_nombre,
                                               'correo', u.correo, 'telefono', u.celular)
                       FROM usuarios u WHERE u.id = ctx_usuario()),
        'tarjetas', (SELECT COALESCE(jsonb_agg(jsonb_build_object('id', t.id, 'marca', t.marca, 'ultimos4', t.ultimos4,
                                                                  'principal', t.principal, 'creadaEn', t.creado_en)
                                               ORDER BY t.principal DESC, t.creado_en DESC), '[]'::jsonb)
                       FROM cobros_tarjetas t
                      WHERE t.cuenta_id = v_cuenta AND t.modo = p_modo AND t.quitada_en IS NULL),
        'mensual', CASE WHEN v_admin THEN cobros_monto(v_cuenta, 'mensual', v_prueba) END,
        'anual',   CASE WHEN v_admin THEN cobros_monto(v_cuenta, 'anual', v_prueba) END,
        'pagos', (SELECT COALESCE(jsonb_agg(jsonb_build_object(
                          'id', x.id, 'numero', x.numero, 'fecha', COALESCE(x.pagado_en, x.creado_en), 'monto', x.monto,
                          'concepto', x.concepto, 'medio', x.medio, 'modo', x.modo, 'estado', x.estado,
                          'desde', x.periodo_desde, 'hasta', x.periodo_hasta, 'error', x.error_mensaje,
                          'tarjeta', (SELECT t.marca || ' •••• ' || t.ultimos4 FROM cobros_tarjetas t WHERE t.id = x.tarjeta_id),
                          'comprobante', x.comprobante_estado, 'comprobanteNumero', x.comprobante_numero)
                        ORDER BY x.creado_en DESC), '[]'::jsonb)
                    FROM (SELECT * FROM cobros WHERE cuenta_id = v_cuenta ORDER BY creado_en DESC LIMIT 60) x));
END $$;

-- Guardar los datos del comprobante (RUC → factura, DNI → boleta).
CREATE OR REPLACE FUNCTION cobros_guardar_datos(p_tipo text, p_numero text, p_nombre text, p_direccion text,
                                                p_correo text, p_telefono text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := cobros_mi_cuenta();
    v_num    text := regexp_replace(COALESCE(p_numero, ''), '\s', '', 'g');
BEGIN
    IF v_cuenta IS NULL THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo el dueño de la cuenta (o un colaborador administrador) cambia los datos de pago.';
    END IF;
    IF p_tipo NOT IN ('ruc', 'dni') THEN
        RAISE EXCEPTION 'COBRO_DATOS: Elige RUC (factura) o DNI (boleta).';
    END IF;
    IF p_tipo = 'ruc' AND NOT ruc_valido(v_num) THEN
        RAISE EXCEPTION 'COBRO_DATOS: Ese RUC no es válido. Revisa los 11 números (empieza en 10 o 20).';
    END IF;
    IF p_tipo = 'dni' AND v_num !~ '^[0-9]{8}$' THEN
        RAISE EXCEPTION 'COBRO_DATOS: El DNI tiene 8 números.';
    END IF;
    IF length(btrim(COALESCE(p_nombre, ''))) < 2 THEN
        RAISE EXCEPTION 'COBRO_DATOS: Escribe el nombre o la razón social.';
    END IF;
    IF p_tipo = 'ruc' AND length(btrim(COALESCE(p_direccion, ''))) < 5 THEN
        RAISE EXCEPTION 'COBRO_DATOS: La factura necesita la dirección fiscal.';
    END IF;
    IF COALESCE(p_correo, '') !~ '^[^@\s]+@[^@\s]+\.[^@\s]+$' THEN
        RAISE EXCEPTION 'COBRO_DATOS: Escribe un correo válido: ahí llega el comprobante.';
    END IF;
    IF NULLIF(btrim(COALESCE(p_telefono, '')), '') IS NOT NULL AND btrim(p_telefono) !~ '^[0-9+ ]{6,20}$' THEN
        RAISE EXCEPTION 'COBRO_DATOS: El teléfono solo lleva números.';
    END IF;

    INSERT INTO cobros_datos (cuenta_id, tipo_doc, numero, nombre, direccion, correo, telefono)
    VALUES (v_cuenta, p_tipo, v_num, btrim(p_nombre), NULLIF(btrim(COALESCE(p_direccion, '')), ''),
            lower(btrim(p_correo)), NULLIF(btrim(COALESCE(p_telefono, '')), ''))
    ON CONFLICT (cuenta_id) DO UPDATE
       SET tipo_doc = EXCLUDED.tipo_doc, numero = EXCLUDED.numero, nombre = EXCLUDED.nombre,
           direccion = EXCLUDED.direccion, correo = EXCLUDED.correo, telefono = EXCLUDED.telefono;
END $$;

-- Lo que la API necesita para hablar con Culqi: los datos del comprobante y
-- el cliente de Culqi de la cuenta en ese modo (si ya existe).
CREATE OR REPLACE FUNCTION cobros_para_pasarela(p_modo text) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := cobros_mi_cuenta();
    d        cobros_datos%ROWTYPE;
BEGIN
    IF v_cuenta IS NULL THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo el dueño de la cuenta (o un colaborador administrador) paga.';
    END IF;
    SELECT * INTO d FROM cobros_datos WHERE cuenta_id = v_cuenta;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'COBRO_DATOS: Primero completa los datos de tu comprobante (RUC o DNI).';
    END IF;
    RETURN jsonb_build_object('cuenta', v_cuenta, 'tipoDoc', d.tipo_doc, 'numero', d.numero, 'nombre', d.nombre,
        'direccion', d.direccion, 'correo', d.correo, 'telefono', d.telefono,
        'cliente', CASE p_modo WHEN 'prueba' THEN d.culqi_cliente_prueba WHEN 'real' THEN d.culqi_cliente_real END);
END $$;

CREATE OR REPLACE FUNCTION cobros_fijar_cliente(p_modo text, p_ref text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v_cuenta uuid := cobros_mi_cuenta();
BEGIN
    IF v_cuenta IS NULL THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo el dueño de la cuenta paga.'; END IF;
    IF p_modo = 'prueba' THEN
        UPDATE cobros_datos SET culqi_cliente_prueba = p_ref WHERE cuenta_id = v_cuenta;
    ELSIF p_modo = 'real' THEN
        UPDATE cobros_datos SET culqi_cliente_real = p_ref WHERE cuenta_id = v_cuenta;
    END IF;
END $$;

-- Guardar una tarjeta (lo que ya aceptó la pasarela). La primera queda como
-- principal.
CREATE OR REPLACE FUNCTION cobros_guardar_tarjeta(p_modo text, p_cliente text, p_tarjeta text, p_marca text,
                                                  p_ultimos4 text) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := cobros_mi_cuenta();
    v_id     uuid;
    v_hay    boolean;
BEGIN
    IF v_cuenta IS NULL THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo el dueño de la cuenta (o un colaborador administrador) guarda tarjetas.'; END IF;
    IF p_modo NOT IN ('simulado', 'prueba', 'real') THEN RAISE EXCEPTION 'COBRO_MODO: Los pagos con tarjeta están apagados.'; END IF;
    IF (SELECT count(*) FROM cobros_tarjetas WHERE cuenta_id = v_cuenta AND modo = p_modo AND quitada_en IS NULL) >= 5 THEN
        RAISE EXCEPTION 'COBRO_DATOS: Tienes 5 tarjetas guardadas. Quita una para agregar otra.';
    END IF;
    v_hay := EXISTS (SELECT 1 FROM cobros_tarjetas WHERE cuenta_id = v_cuenta AND modo = p_modo AND quitada_en IS NULL AND principal);
    INSERT INTO cobros_tarjetas (cuenta_id, modo, cliente_ref, tarjeta_ref, marca, ultimos4, principal)
    VALUES (v_cuenta, p_modo, p_cliente, p_tarjeta, COALESCE(NULLIF(btrim(p_marca), ''), 'Tarjeta'), p_ultimos4, NOT v_hay)
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;

CREATE OR REPLACE FUNCTION cobros_quitar_tarjeta(p_id uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := cobros_mi_cuenta();
    t        cobros_tarjetas%ROWTYPE;
BEGIN
    SELECT * INTO t FROM cobros_tarjetas WHERE id = p_id AND quitada_en IS NULL;
    IF v_cuenta IS NULL OR NOT FOUND OR t.cuenta_id <> v_cuenta THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Esa tarjeta no es de tu cuenta.';
    END IF;
    UPDATE cobros_tarjetas SET quitada_en = now(), quitada_por = ctx_usuario(), principal = false WHERE id = p_id;
    -- Si era la principal, pasa a serlo la más nueva que quede.
    IF t.principal THEN
        UPDATE cobros_tarjetas SET principal = true
         WHERE id = (SELECT id FROM cobros_tarjetas WHERE cuenta_id = v_cuenta AND modo = t.modo AND quitada_en IS NULL
                      ORDER BY creado_en DESC LIMIT 1);
    END IF;
END $$;

CREATE OR REPLACE FUNCTION cobros_tarjeta_principal(p_id uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := cobros_mi_cuenta();
    t        cobros_tarjetas%ROWTYPE;
BEGIN
    SELECT * INTO t FROM cobros_tarjetas WHERE id = p_id AND quitada_en IS NULL;
    IF v_cuenta IS NULL OR NOT FOUND OR t.cuenta_id <> v_cuenta THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Esa tarjeta no es de tu cuenta.';
    END IF;
    UPDATE cobros_tarjetas SET principal = false WHERE cuenta_id = v_cuenta AND modo = t.modo AND principal AND id <> p_id;
    UPDATE cobros_tarjetas SET principal = true WHERE id = p_id;
END $$;

-- Pone a la cuenta al día según lo que tiene pagado.
CREATE OR REPLACE FUNCTION cobros_aplicar_cuenta(p_cuenta uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v_hasta date;
BEGIN
    SELECT max(periodo_hasta) INTO v_hasta FROM cobros WHERE cuenta_id = p_cuenta AND estado = 'pagado';
    UPDATE cuentas
       SET pago_proximo = CASE WHEN v_hasta IS NULL THEN pago_proximo ELSE v_hasta + 1 END,
           pago_estado = CASE WHEN v_hasta IS NOT NULL AND v_hasta >= current_date THEN 'al_dia' ELSE pago_estado END,
           pago_falla_desde = CASE WHEN v_hasta IS NOT NULL AND v_hasta >= current_date THEN NULL ELSE pago_falla_desde END,
           pago_intentos = CASE WHEN v_hasta IS NOT NULL AND v_hasta >= current_date THEN 0 ELSE pago_intentos END
     WHERE id = p_cuenta;
END $$;

-- EMPEZAR UN PAGO CON TARJETA. Calcula el monto, toma una foto de los datos
-- del comprobante y deja el pago «procesando». Devuelve lo que la API manda a
-- la pasarela. Un solo pago en curso por cuenta (índice ux_cobros_en_curso).
CREATE OR REPLACE FUNCTION cobros_preparar(p_modo text, p_periodo text, p_tarjeta uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := cobros_mi_cuenta();
    k        cobros_ajustes%ROWTYPE;
    d        cobros_datos%ROWTYPE;
    t        cobros_tarjetas%ROWTYPE;
    m        jsonb;
    v_id     uuid;
BEGIN
    IF v_cuenta IS NULL THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo el dueño de la cuenta (o un colaborador administrador) paga.';
    END IF;
    SELECT * INTO k FROM cobros_ajustes WHERE id = 1;
    IF p_modo NOT IN ('simulado', 'prueba', 'real') OR NOT k.acepta_tarjeta THEN
        RAISE EXCEPTION 'COBRO_MODO: Los pagos con tarjeta están apagados por ahora.';
    END IF;
    SELECT * INTO d FROM cobros_datos WHERE cuenta_id = v_cuenta;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'COBRO_DATOS: Primero completa los datos de tu comprobante (RUC o DNI).';
    END IF;
    SELECT * INTO t FROM cobros_tarjetas WHERE id = p_tarjeta AND quitada_en IS NULL;
    IF NOT FOUND OR t.cuenta_id <> v_cuenta OR t.modo <> p_modo THEN
        RAISE EXCEPTION 'COBRO_DATOS: Elige una de tus tarjetas guardadas.';
    END IF;

    -- Uno que se quedó colgado hace más de 10 minutos ya no cuenta como en
    -- curso: queda «rechazado — sin respuesta». Si la pasarela sí cobró, su
    -- aviso lo pasa a pagado (cobros_confirmar).
    UPDATE cobros SET estado = 'rechazado', error_codigo = 'sin_respuesta',
                      error_mensaje = 'No hubo respuesta de la pasarela. Si te llegó el cargo, se confirmará solo.'
     WHERE cuenta_id = v_cuenta AND estado = 'procesando' AND creado_en < now() - interval '10 minutes';
    IF EXISTS (SELECT 1 FROM cobros WHERE cuenta_id = v_cuenta AND estado = 'procesando') THEN
        RAISE EXCEPTION 'COBRO_EN_CURSO: Ya hay un pago en curso. Espera unos segundos.';
    END IF;

    m := cobros_monto(v_cuenta, p_periodo, p_modo IN ('simulado', 'prueba'));
    IF (m->>'nada')::boolean THEN
        RAISE EXCEPTION 'COBRO_NADA: %', m->>'motivo';
    END IF;

    INSERT INTO cobros (cuenta_id, modo, medio, tarjeta_id, monto, concepto, periodo, periodo_desde, periodo_hasta, estado,
                        comprador_tipo_doc, comprador_doc, comprador_nombre, comprador_direccion, comprador_correo,
                        comprobante_tipo)
    VALUES (v_cuenta, p_modo, 'tarjeta', t.id, (m->>'monto')::numeric, m->>'concepto', p_periodo,
            (m->>'desde')::date, (m->>'hasta')::date, 'procesando',
            d.tipo_doc, d.numero, d.nombre, d.direccion, d.correo,
            CASE d.tipo_doc WHEN 'ruc' THEN '01' ELSE '03' END)
    RETURNING id INTO v_id;

    RETURN jsonb_build_object('id', v_id, 'cuenta', v_cuenta, 'monto', (m->>'monto')::numeric, 'concepto', m->>'concepto',
        'correo', d.correo, 'tarjeta', t.tarjeta_ref, 'cliente', t.cliente_ref,
        'desde', m->>'desde', 'hasta', m->>'hasta');
END $$;

-- EL RESULTADO DEL PAGO. Lo llama la API con lo que respondió la pasarela.
-- Solo pasa de «procesando» a pagado o rechazado.
CREATE OR REPLACE FUNCTION cobros_resultado(p_id uuid, p_ok boolean, p_referencia text, p_codigo text, p_mensaje text)
RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    x cobros%ROWTYPE;
BEGIN
    SELECT * INTO x FROM cobros WHERE id = p_id FOR UPDATE;
    IF NOT FOUND OR (NOT ctx_es_super() AND x.cuenta_id IS DISTINCT FROM cobros_mi_cuenta()) THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Ese pago no es de tu cuenta.';
    END IF;
    IF x.estado <> 'procesando' THEN
        RETURN jsonb_build_object('estado', x.estado);
    END IF;
    IF p_ok THEN
        UPDATE cobros SET estado = 'pagado', pagado_en = now(), referencia = p_referencia,
                          error_codigo = NULL, error_mensaje = NULL
         WHERE id = p_id;
        PERFORM cobros_aplicar_cuenta(x.cuenta_id);
    ELSE
        UPDATE cobros SET estado = 'rechazado', referencia = p_referencia,
                          error_codigo = left(p_codigo, 60), error_mensaje = left(p_mensaje, 300)
         WHERE id = p_id;
    END IF;
    RETURN jsonb_build_object('estado', CASE WHEN p_ok THEN 'pagado' ELSE 'rechazado' END,
                              'proximo', (SELECT pago_proximo FROM cuentas WHERE id = x.cuenta_id));
END $$;

-- LO QUE CONFIRMA CULQI POR SU AVISO (webhook). La API solo la llama DESPUÉS
-- de preguntarle a Culqi, con la llave secreta, cómo está de verdad ese
-- cargo: nunca con lo que dice el aviso.
--   pagado:   un pago procesando o rechazado «sin respuesta» pasa a pagado si
--             el monto cuadra.
--   devuelto: un pago pagado pasa a devuelto.
CREATE OR REPLACE FUNCTION cobros_confirmar(p_id uuid, p_modo text, p_referencia text, p_estado text, p_monto numeric)
RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    x cobros%ROWTYPE;
BEGIN
    IF p_id IS NOT NULL THEN
        SELECT * INTO x FROM cobros WHERE id = p_id AND modo = p_modo FOR UPDATE;
    ELSE
        SELECT * INTO x FROM cobros WHERE referencia = p_referencia AND modo = p_modo FOR UPDATE;
    END IF;
    IF NOT FOUND THEN RETURN 'no_es_nuestro'; END IF;

    IF p_estado = 'pagado' THEN
        IF x.estado = 'pagado' THEN RETURN 'ya_estaba'; END IF;
        IF NOT (x.estado = 'procesando' OR (x.estado = 'rechazado' AND x.error_codigo = 'sin_respuesta')) THEN
            RETURN 'estado_' || x.estado;
        END IF;
        IF p_monto IS DISTINCT FROM x.monto THEN RETURN 'monto_distinto'; END IF;
        UPDATE cobros SET estado = 'pagado', pagado_en = now(), referencia = p_referencia,
                          error_codigo = NULL, error_mensaje = NULL
         WHERE id = x.id;
        PERFORM cobros_aplicar_cuenta(x.cuenta_id);
        RETURN 'pagado';
    ELSIF p_estado = 'devuelto' THEN
        IF x.estado = 'devuelto' THEN RETURN 'ya_estaba'; END IF;
        IF x.estado <> 'pagado' THEN RETURN 'estado_' || x.estado; END IF;
        UPDATE cobros SET estado = 'devuelto', notas = left(COALESCE(notas || ' · ', '') || 'Devuelto en Culqi', 500)
         WHERE id = x.id;
        RETURN 'devuelto';
    END IF;
    RETURN 'sin_cambio';
END $$;

-- Guardar un aviso de Culqi, valga o no.
CREATE OR REPLACE FUNCTION cobros_evento(p_tipo text, p_referencia text, p_valido boolean, p_detalle text, p_ip text)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v_id bigint;
BEGIN
    INSERT INTO cobros_eventos (tipo, referencia, valido, detalle, ip)
    VALUES (left(p_tipo, 80), left(p_referencia, 100), p_valido, left(p_detalle, CASE WHEN p_valido THEN 4000 ELSE 1000 END), left(p_ip, 60))
    RETURNING id INTO v_id;
    IF p_valido THEN
        UPDATE cobros_ajustes SET ultimo_evento_en = now() WHERE id = 1;
    END IF;
    RETURN v_id;
END $$;

CREATE OR REPLACE FUNCTION cobros_evento_resultado(p_id bigint, p_aplicado boolean, p_resultado text) RETURNS void
LANGUAGE sql SECURITY DEFINER SET search_path TO 'public' AS $$
    UPDATE cobros_eventos SET aplicado = p_aplicado, resultado = left(p_resultado, 200) WHERE id = p_id;
$$;

-- -----------------------------------------------------------------------------
-- 10. LA PLATAFORMA
-- -----------------------------------------------------------------------------

-- El panel de pagos.
CREATE OR REPLACE FUNCTION cobros_config() RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE k cobros_ajustes%ROWTYPE;
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    SELECT * INTO k FROM cobros_ajustes WHERE id = 1;
    RETURN jsonb_build_object(
        'modo', k.modo, 'reintentoDias', to_jsonb(k.reintento_dias), 'graciaDias', k.gracia_dias,
        'avisoDias', k.aviso_dias, 'alVencer', k.al_vencer,
        'emisora', k.emisora_empresa, 'serieFactura', k.serie_factura, 'serieBoleta', k.serie_boleta,
        'enviarCorreo', k.enviar_correo, 'descripcion', k.descripcion,
        'aceptaTarjeta', k.acepta_tarjeta, 'aceptaTransferencia', k.acepta_transferencia, 'aceptaYape', k.acepta_yape,
        'transferenciaDatos', k.transferencia_datos, 'correoPagos', k.correo_pagos,
        'ultimoEventoEn', k.ultimo_evento_en,
        'prueba', jsonb_build_object('en', k.prueba_en, 'ok', k.prueba_ok, 'detalle', k.prueba_detalle),
        'modificadoEn', k.modificado_en,
        'modificadoPor', (SELECT u.nombre FROM usuarios u WHERE u.id = k.modificado_por),
        -- Las empresas que pueden emitir: con series de factura o de boleta.
        'emisoras', (SELECT COALESCE(jsonb_agg(jsonb_build_object(
                         'id', e.id, 'nombre', e.nombre, 'ruc', e.ruc, 'razonSocial', e.razon_social,
                         'seriesFactura', (SELECT COALESCE(jsonb_agg(DISTINCT s.serie), '[]'::jsonb) FROM sucursal_series s
                                            WHERE s.empresa_id = e.id AND s.activo AND s.tipo_comprobante = '01'),
                         'seriesBoleta',  (SELECT COALESCE(jsonb_agg(DISTINCT s.serie), '[]'::jsonb) FROM sucursal_series s
                                            WHERE s.empresa_id = e.id AND s.activo AND s.tipo_comprobante = '03'))
                       ORDER BY e.nombre), '[]'::jsonb)
                       FROM empresas e
                      WHERE e.activo AND e.suspendida_en IS NULL
                        AND EXISTS (SELECT 1 FROM sucursal_series s WHERE s.empresa_id = e.id AND s.activo
                                       AND s.tipo_comprobante IN ('01', '03'))),
        'eventos', (SELECT COALESCE(jsonb_agg(jsonb_build_object('id', v.id, 'en', v.recibido_en, 'tipo', v.tipo,
                         'valido', v.valido, 'aplicado', v.aplicado, 'resultado', v.resultado) ORDER BY v.id DESC), '[]'::jsonb)
                      FROM (SELECT * FROM cobros_eventos ORDER BY id DESC LIMIT 10) v),
        'mes', (SELECT jsonb_build_object('pagos', count(*) FILTER (WHERE estado = 'pagado'),
                                          'monto', COALESCE(sum(monto) FILTER (WHERE estado = 'pagado' AND modo IN ('real', 'manual')), 0),
                                          'rechazados', count(*) FILTER (WHERE estado = 'rechazado'))
                  FROM cobros WHERE creado_en >= date_trunc('month', now())));
END $$;

-- Guardar el panel. p_produccion: la API dice si es producción (SUNAT real);
-- ahí el modo simulado no se puede elegir.
CREATE OR REPLACE FUNCTION cobros_guardar_config(p jsonb, p_produccion boolean) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_modo  text := p->>'modo';
    v_reint integer[];
    v_emp   uuid := NULLIF(p->>'emisora', '')::uuid;
    v_sf    text := NULLIF(upper(btrim(COALESCE(p->>'serieFactura', ''))), '');
    v_sb    text := NULLIF(upper(btrim(COALESCE(p->>'serieBoleta', ''))), '');
    v_gr    integer := (p->>'graciaDias')::int;
    v_av    integer := (p->>'avisoDias')::int;
    v_desc  text := btrim(COALESCE(p->>'descripcion', ''));
    v_banco text := NULLIF(btrim(COALESCE(p->>'transferenciaDatos', '')), '');
    v_corr  text := NULLIF(lower(btrim(COALESCE(p->>'correoPagos', ''))), '');
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF v_modo NOT IN ('apagado', 'simulado', 'culqi') THEN
        RAISE EXCEPTION 'COBRO_CONFIG: Elige el modo: apagado, simulado o Culqi.';
    END IF;
    IF v_modo = 'simulado' AND p_produccion THEN
        RAISE EXCEPTION 'COBRO_CONFIG: En producción no se puede usar el modo simulado: nadie puede «pagar de mentira».';
    END IF;
    SELECT array_agg(x::int ORDER BY x::int) INTO v_reint
      FROM (SELECT DISTINCT jsonb_array_elements_text(COALESCE(p->'reintentoDias', '[]'::jsonb)) x) q;
    v_reint := COALESCE(v_reint, ARRAY[]::integer[]);
    IF cardinality(v_reint) > 5 OR EXISTS (SELECT 1 FROM unnest(v_reint) d WHERE d < 1 OR d > 30) THEN
        RAISE EXCEPTION 'COBRO_CONFIG: Hasta 5 reintentos, cada uno entre el día 1 y el 30.';
    END IF;
    IF v_gr IS NULL OR v_gr NOT BETWEEN 0 AND 30 THEN
        RAISE EXCEPTION 'COBRO_CONFIG: Los días de gracia van de 0 a 30.';
    END IF;
    IF v_av IS NULL OR v_av NOT BETWEEN 0 AND 15 THEN
        RAISE EXCEPTION 'COBRO_CONFIG: El aviso antes del cobro va de 0 a 15 días.';
    END IF;
    IF COALESCE(p->>'alVencer', '') NOT IN ('no_emite', 'solo_aviso') THEN
        RAISE EXCEPTION 'COBRO_CONFIG: Elige qué pasa al terminar la gracia.';
    END IF;
    IF length(v_desc) NOT BETWEEN 5 AND 120 THEN
        RAISE EXCEPTION 'COBRO_CONFIG: La descripción del comprobante va de 5 a 120 letras.';
    END IF;
    IF v_emp IS NOT NULL THEN
        IF NOT EXISTS (SELECT 1 FROM empresas WHERE id = v_emp AND activo) THEN
            RAISE EXCEPTION 'COBRO_CONFIG: Esa empresa no existe.';
        END IF;
        IF v_sf IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sucursal_series WHERE empresa_id = v_emp AND activo
                                               AND tipo_comprobante = '01' AND serie = v_sf) THEN
            RAISE EXCEPTION 'COBRO_CONFIG: La serie % no es una serie de factura de esa empresa.', v_sf;
        END IF;
        IF v_sb IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sucursal_series WHERE empresa_id = v_emp AND activo
                                               AND tipo_comprobante = '03' AND serie = v_sb) THEN
            RAISE EXCEPTION 'COBRO_CONFIG: La serie % no es una serie de boleta de esa empresa.', v_sb;
        END IF;
    ELSE
        v_sf := NULL; v_sb := NULL;
    END IF;
    IF COALESCE((p->>'aceptaTransferencia')::boolean, false) AND v_banco IS NULL THEN
        RAISE EXCEPTION 'COBRO_CONFIG: Para aceptar transferencias escribe los datos de tu cuenta bancaria.';
    END IF;
    IF v_corr IS NOT NULL AND v_corr !~ '^[^@\s]+@[^@\s]+\.[^@\s]+$' THEN
        RAISE EXCEPTION 'COBRO_CONFIG: El correo de pagos no es válido.';
    END IF;

    UPDATE cobros_ajustes SET
        modo = v_modo, reintento_dias = v_reint, gracia_dias = v_gr, aviso_dias = v_av, al_vencer = p->>'alVencer',
        emisora_empresa = v_emp, serie_factura = v_sf, serie_boleta = v_sb,
        enviar_correo = COALESCE((p->>'enviarCorreo')::boolean, true), descripcion = v_desc,
        acepta_tarjeta = COALESCE((p->>'aceptaTarjeta')::boolean, true),
        acepta_transferencia = COALESCE((p->>'aceptaTransferencia')::boolean, false),
        acepta_yape = COALESCE((p->>'aceptaYape')::boolean, false),
        transferencia_datos = left(v_banco, 600), correo_pagos = v_corr
     WHERE id = 1;
END $$;

CREATE OR REPLACE FUNCTION cobros_guardar_prueba(p_ok boolean, p_detalle text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    UPDATE cobros_ajustes SET prueba_en = now(), prueba_ok = p_ok, prueba_detalle = left(p_detalle, 300) WHERE id = 1;
END $$;

-- Los pagos de todas las cuentas, con filtros.
CREATE OR REPLACE FUNCTION cobros_lista(p_estado text, p_texto text, p_desde date, p_hasta date) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v_t text := '%' || lower(btrim(COALESCE(p_texto, ''))) || '%';
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    RETURN (SELECT COALESCE(jsonb_agg(jsonb_build_object(
                'id', x.id, 'numero', x.numero, 'fecha', COALESCE(x.pagado_en, x.creado_en), 'cuentaId', x.cuenta_id,
                'cuenta', COALESCE(c.titular_nombre, (SELECT u.nombre FROM usuarios u WHERE u.cuenta_id = c.id AND u.nivel_cuenta = 'dueno' ORDER BY u.creado_en LIMIT 1)),
                'tipoCuenta', c.tipo, 'monto', x.monto, 'concepto', x.concepto, 'medio', x.medio, 'modo', x.modo,
                'estado', x.estado, 'referencia', x.referencia, 'error', x.error_mensaje,
                'desde', x.periodo_desde, 'hasta', x.periodo_hasta,
                'comprador', x.comprador_nombre, 'documento', x.comprador_doc,
                'comprobante', x.comprobante_estado, 'comprobanteNumero', x.comprobante_numero,
                'registradoPor', (SELECT u.nombre FROM usuarios u WHERE u.id = x.registrado_por),
                'notas', x.notas, 'anuladoMotivo', x.anulado_motivo,
                'puedeAnular', x.estado = 'pagado' AND x.modo IN ('manual', 'simulado'))
              ORDER BY x.creado_en DESC), '[]'::jsonb)
              FROM (SELECT * FROM cobros x
                     WHERE (p_estado IS NULL OR p_estado = '' OR x.estado = p_estado)
                       AND (p_desde IS NULL OR x.creado_en >= p_desde)
                       AND (p_hasta IS NULL OR x.creado_en < p_hasta + 1)
                     ORDER BY x.creado_en DESC LIMIT 500) x
              JOIN cuentas c ON c.id = x.cuenta_id
             WHERE v_t = '%%'
                OR lower(COALESCE(c.titular_nombre, '')) LIKE v_t OR lower(COALESCE(x.comprador_nombre, '')) LIKE v_t
                OR COALESCE(x.comprador_doc, '') LIKE v_t OR lower(COALESCE(x.referencia, '')) LIKE v_t
                OR x.numero::text = btrim(p_texto)
                OR EXISTS (SELECT 1 FROM usuarios u WHERE u.cuenta_id = c.id AND lower(u.correo) LIKE v_t));
END $$;

-- REGISTRAR UN PAGO A MANO (transferencia, depósito, Yape, efectivo).
-- Cubre p_meses desde el próximo cobro (o desde que termina el mes gratis).
CREATE OR REPLACE FUNCTION cobros_manual(p_cuenta uuid, p_monto numeric, p_medio text, p_referencia text,
                                         p_meses integer, p_notas text) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    c       cuentas%ROWTYPE;
    d       cobros_datos%ROWTYPE;
    k       cobros_ajustes%ROWTYPE;
    v_desde date;
    v_hasta date;
    v_id    uuid;
    v_conc  text;
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    SELECT * INTO c FROM cuentas WHERE id = p_cuenta;
    IF NOT FOUND THEN RAISE EXCEPTION 'COBRO_DATOS: Esa cuenta no existe.'; END IF;
    IF p_medio NOT IN ('transferencia', 'deposito', 'yape', 'efectivo', 'otro') THEN
        RAISE EXCEPTION 'COBRO_DATOS: Elige cómo pagó: transferencia, depósito, Yape, efectivo u otro.';
    END IF;
    IF p_monto IS NULL OR p_monto <= 0 OR p_monto > 100000 THEN
        RAISE EXCEPTION 'COBRO_DATOS: Escribe el monto que pagó.';
    END IF;
    IF p_meses IS NULL OR p_meses NOT BETWEEN 1 AND 24 THEN
        RAISE EXCEPTION 'COBRO_DATOS: El pago cubre de 1 a 24 meses.';
    END IF;
    IF length(btrim(COALESCE(p_referencia, ''))) < 3 THEN
        RAISE EXCEPTION 'COBRO_DATOS: Escribe el número de operación (para encontrarlo luego en el banco).';
    END IF;
    IF EXISTS (SELECT 1 FROM cobros WHERE modo = 'manual' AND medio = p_medio AND referencia = btrim(p_referencia)
                                      AND estado = 'pagado') THEN
        RAISE EXCEPTION 'COBRO_DATOS: Ese número de operación ya está registrado.';
    END IF;
    SELECT * INTO d FROM cobros_datos WHERE cuenta_id = p_cuenta;
    SELECT * INTO k FROM cobros_ajustes WHERE id = 1;

    v_desde := COALESCE(c.pago_proximo, current_date);
    IF c.prueba_hasta > now() THEN
        v_desde := GREATEST(v_desde, (c.prueba_hasta AT TIME ZONE 'America/Lima')::date);
    END IF;
    v_hasta := (v_desde + make_interval(months => p_meses))::date - 1;
    v_conc := replace(replace(k.descripcion, '{plan}', CASE c.tipo WHEN 'profesional' THEN 'Cartera' ELSE 'de la cuenta' END),
                      '{periodo}', to_char(v_desde, 'DD/MM/YYYY') || ' al ' || to_char(v_hasta, 'DD/MM/YYYY'));

    INSERT INTO cobros (cuenta_id, modo, medio, monto, concepto, periodo, periodo_desde, periodo_hasta, estado,
                        referencia, pagado_en, registrado_por, notas,
                        comprador_tipo_doc, comprador_doc, comprador_nombre, comprador_direccion, comprador_correo,
                        comprobante_tipo)
    VALUES (p_cuenta, 'manual', p_medio, round(p_monto, 2), left(v_conc, 200),
            CASE WHEN p_meses = 1 THEN 'mensual' WHEN p_meses = 12 THEN 'anual' ELSE 'otro' END,
            v_desde, v_hasta, 'pagado', btrim(p_referencia), now(), ctx_usuario(), NULLIF(btrim(COALESCE(p_notas, '')), ''),
            d.tipo_doc, d.numero, d.nombre, d.direccion, d.correo,
            CASE d.tipo_doc WHEN 'ruc' THEN '01' WHEN 'dni' THEN '03' END)
    RETURNING id INTO v_id;
    PERFORM cobros_aplicar_cuenta(p_cuenta);
    RETURN v_id;
END $$;

-- ANULAR un pago registrado a mano o simulado (se equivocaron al registrarlo).
-- Un pago real con tarjeta no se anula aquí: se devuelve en Culqi, y su aviso
-- lo marca como devuelto. Nada se borra.
CREATE OR REPLACE FUNCTION cobros_anular(p_id uuid, p_motivo text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    x cobros%ROWTYPE;
    v_hasta date;
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    SELECT * INTO x FROM cobros WHERE id = p_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'COBRO_DATOS: Ese pago no existe.'; END IF;
    IF x.estado <> 'pagado' OR x.modo NOT IN ('manual', 'simulado') THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo se anulan pagos registrados a mano o simulados. Un pago con tarjeta se devuelve desde Culqi.';
    END IF;
    IF length(btrim(COALESCE(p_motivo, ''))) < 3 THEN
        RAISE EXCEPTION 'COBRO_DATOS: Escribe por qué se anula.';
    END IF;
    UPDATE cobros SET estado = 'anulado', anulado_en = now(), anulado_por = ctx_usuario(), anulado_motivo = btrim(p_motivo)
     WHERE id = p_id;
    -- El próximo cobro vuelve a lo que de verdad está pagado.
    SELECT max(periodo_hasta) INTO v_hasta FROM cobros WHERE cuenta_id = x.cuenta_id AND estado = 'pagado';
    UPDATE cuentas SET pago_proximo = CASE WHEN v_hasta IS NULL THEN x.periodo_desde ELSE v_hasta + 1 END
     WHERE id = x.cuenta_id;
END $$;

-- Las cuentas para elegir al registrar un pago a mano.
CREATE OR REPLACE FUNCTION cobros_buscar_cuentas(p_texto text) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v_t text := '%' || lower(btrim(COALESCE(p_texto, ''))) || '%';
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    IF length(btrim(COALESCE(p_texto, ''))) < 2 THEN RETURN '[]'::jsonb; END IF;
    RETURN (SELECT COALESCE(jsonb_agg(q ORDER BY q->>'nombre'), '[]'::jsonb) FROM (
        SELECT jsonb_build_object('id', c.id, 'tipo', c.tipo,
                   'nombre', COALESCE(c.titular_nombre, u.nombre), 'correo', u.correo,
                   'proximo', c.pago_proximo, 'pruebaHasta', c.prueba_hasta,
                   'datos', (SELECT d.tipo_doc || ' ' || d.numero || ' · ' || d.nombre FROM cobros_datos d WHERE d.cuenta_id = c.id)) q
          FROM cuentas c
          LEFT JOIN LATERAL (SELECT u.nombre, u.correo FROM usuarios u WHERE u.cuenta_id = c.id AND u.nivel_cuenta = 'dueno'
                              ORDER BY u.creado_en LIMIT 1) u ON true
         WHERE c.activo
           AND (lower(COALESCE(c.titular_nombre, '')) LIKE v_t OR lower(COALESCE(u.nombre, '')) LIKE v_t
                OR lower(COALESCE(u.correo, '')) LIKE v_t OR COALESCE(c.titular_documento, '') LIKE v_t
                OR EXISTS (SELECT 1 FROM empresas e WHERE e.cuenta_id = c.id AND (e.ruc LIKE v_t OR lower(e.nombre) LIKE v_t)))
         LIMIT 20) s(q));
END $$;

-- -----------------------------------------------------------------------------
-- 11. DUEÑO Y PERMISOS DE LAS FUNCIONES
-- -----------------------------------------------------------------------------
DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY[
        'cobros_modo_guardado()', 'cobros_mi_cuenta()', 'cobros_monto(uuid, text, boolean)', 'cobros_mio(text)',
        'cobros_guardar_datos(text, text, text, text, text, text)', 'cobros_para_pasarela(text)',
        'cobros_fijar_cliente(text, text)', 'cobros_guardar_tarjeta(text, text, text, text, text)',
        'cobros_quitar_tarjeta(uuid)', 'cobros_tarjeta_principal(uuid)', 'cobros_aplicar_cuenta(uuid)',
        'cobros_preparar(text, text, uuid)', 'cobros_resultado(uuid, boolean, text, text, text)',
        'cobros_confirmar(uuid, text, text, text, numeric)', 'cobros_evento(text, text, boolean, text, text)',
        'cobros_evento_resultado(bigint, boolean, text)', 'cobros_config()', 'cobros_guardar_config(jsonb, boolean)',
        'cobros_guardar_prueba(boolean, text)', 'cobros_lista(text, text, date, date)',
        'cobros_manual(uuid, numeric, text, text, integer, text)', 'cobros_anular(uuid, text)',
        'cobros_buscar_cuentas(text)']
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        -- cobros_aplicar_cuenta es de uso interno: la llaman las otras.
        IF f <> 'cobros_aplicar_cuenta(uuid)' THEN
            EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
        END IF;
    END LOOP;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('073_cobros')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '073: cobros (panel de pagos, datos del comprobante, tarjetas guardadas sin su número, pagos y avisos de Culqi). Modo de pagos: %. Nada se borró.',
        (SELECT modo FROM cobros_ajustes WHERE id = 1);
END $$;