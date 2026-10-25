-- =============================================================================
-- 055 — PUNTO DE VENTA Y CAJA (e173, 25 oct 2026)
--
-- La venta de mostrador: se abre la caja con un fondo, se vende (cada venta es
-- una boleta o factura normal, que va a SUNAT y descuenta stock como siempre),
-- se registran ingresos y retiros de efectivo, y al cerrar se cuenta el dinero
-- y queda el arqueo: lo esperado, lo contado y la diferencia.
--
-- Es un MÓDULO QUE SE CONTRATA («Punto de venta», Empresas › Módulos), como el
-- inventario. Sin él, nada cambia: la empresa factura como siempre.
--
-- Tablas:
--   pos_cajas        las cajas de cada sucursal («Caja 01»). Se crean solas.
--   pos_sesiones     una apertura y su cierre (el arqueo). Una abierta por
--                    persona y una por caja.
--   pos_movimientos  ingresos y retiros de efectivo. Solo inserción.
--   pos_ventas       qué comprobante salió de qué caja, con el vuelto.
--                    Solo inserción.
--   pos_pagos        cómo se pagó cada venta (efectivo, Yape/Plin, tarjeta,
--                    transferencia; varios si es mixto). Solo inserción.
--
-- Lo que cuenta la caja sale de los comprobantes: una venta cuyo comprobante
-- no llegó a SUNAT (error) o fue rechazado no suma al efectivo esperado.
--
-- Solo crea. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

INSERT INTO modulos (codigo, nombre, descripcion, ruta, icono, orden) VALUES
('pos', 'Punto de venta',
 'Venta rápida de mostrador con apertura y cierre de caja (arqueo)',
 '/pos', 'caja', 15)
ON CONFLICT (codigo) DO NOTHING;

-- --- Las cajas ---------------------------------------------------------------

CREATE TABLE IF NOT EXISTS pos_cajas (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id      uuid        NOT NULL REFERENCES empresas (id),
    sucursal_id     uuid        NOT NULL REFERENCES sucursales (id),
    nombre          text        NOT NULL,

    creado_en       timestamptz NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean     NOT NULL DEFAULT true,
    version         integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_pos_cajas_nombre CHECK (length(btrim(nombre)) BETWEEN 1 AND 60)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pos_cajas_nombre ON pos_cajas (sucursal_id, lower(btrim(nombre)));

COMMENT ON TABLE pos_cajas IS '055: las cajas de cada sucursal. La primera («Caja 01») se crea sola al abrir.';

-- --- Aperturas y cierres -----------------------------------------------------

CREATE TABLE IF NOT EXISTS pos_sesiones (
    id                 uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id         uuid          NOT NULL REFERENCES empresas (id),
    sucursal_id        uuid          NOT NULL REFERENCES sucursales (id),
    caja_id            uuid          NOT NULL REFERENCES pos_cajas (id),
    usuario_id         uuid          NOT NULL REFERENCES usuarios (id),
    abierta_en         timestamptz   NOT NULL DEFAULT now(),
    fondo_inicial      numeric(12,2) NOT NULL DEFAULT 0,
    nota_apertura      text,

    cerrada_en         timestamptz,
    cerrada_por        uuid          REFERENCES usuarios (id),
    efectivo_esperado  numeric(12,2),
    efectivo_contado   numeric(12,2),
    diferencia         numeric(12,2),
    observacion_cierre text,
    deja_en_caja       numeric(12,2),
    entrega            numeric(12,2),
    -- Lo que dijo el sistema al cerrar (ventas por medio, ingresos, retiros):
    -- el arqueo no cambia aunque después cambie el estado de un comprobante.
    resumen            jsonb,

    creado_en          timestamptz   NOT NULL DEFAULT now(),
    creado_por         uuid,
    modificado_en      timestamptz,
    modificado_por     uuid,
    activo             boolean       NOT NULL DEFAULT true,
    version            integer       NOT NULL DEFAULT 1,

    CONSTRAINT ck_pos_sesiones_fondo CHECK (fondo_inicial >= 0),
    CONSTRAINT ck_pos_sesiones_cierre CHECK (
        cerrada_en IS NULL
        OR (efectivo_contado IS NOT NULL AND efectivo_contado >= 0
            AND efectivo_esperado IS NOT NULL AND diferencia IS NOT NULL
            AND (diferencia = 0 OR length(btrim(coalesce(observacion_cierre, ''))) >= 3)
            AND coalesce(deja_en_caja, 0) >= 0 AND coalesce(entrega, 0) >= 0))
);
-- Una caja abierta por persona, y una persona por caja.
CREATE UNIQUE INDEX IF NOT EXISTS ux_pos_sesiones_usuario ON pos_sesiones (usuario_id) WHERE cerrada_en IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_pos_sesiones_caja    ON pos_sesiones (caja_id)    WHERE cerrada_en IS NULL;
CREATE INDEX IF NOT EXISTS ix_pos_sesiones_empresa ON pos_sesiones (empresa_id, abierta_en DESC);

COMMENT ON TABLE pos_sesiones IS '055: una apertura de caja y su cierre (arqueo). Cerrada no se reabre: se abre otra.';

-- Cerrada, no se toca: el arqueo es un documento.
CREATE OR REPLACE FUNCTION fn_pos_sesion_cerrada() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.cerrada_en IS NOT NULL THEN
        RAISE EXCEPTION 'POS_CERRADA: esta caja ya se cerró; el arqueo no se cambia.'
            USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'pos_sesiones'::regclass AND tgname = 'pos_sesion_cerrada') THEN
        CREATE TRIGGER pos_sesion_cerrada BEFORE UPDATE ON pos_sesiones
            FOR EACH ROW EXECUTE FUNCTION fn_pos_sesion_cerrada();
    END IF;
END $$;

-- --- Ingresos y retiros de efectivo -------------------------------------------

CREATE TABLE IF NOT EXISTS pos_movimientos (
    id          uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id  uuid          NOT NULL REFERENCES empresas (id),
    sesion_id   uuid          NOT NULL REFERENCES pos_sesiones (id),
    tipo        text          NOT NULL,
    monto       numeric(12,2) NOT NULL,
    motivo      text          NOT NULL,
    detalle     text,
    creado_en   timestamptz   NOT NULL DEFAULT now(),
    creado_por  uuid          DEFAULT ctx_usuario(),

    CONSTRAINT ck_pos_movimientos_tipo  CHECK (tipo IN ('ingreso', 'retiro')),
    CONSTRAINT ck_pos_movimientos_monto CHECK (monto > 0),
    CONSTRAINT ck_pos_movimientos_motivo CHECK (length(btrim(motivo)) BETWEEN 3 AND 80)
);
CREATE INDEX IF NOT EXISTS ix_pos_movimientos_sesion ON pos_movimientos (sesion_id);

-- --- Las ventas y sus pagos ----------------------------------------------------

CREATE TABLE IF NOT EXISTS pos_ventas (
    id            uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id    uuid          NOT NULL REFERENCES empresas (id),
    sesion_id     uuid          NOT NULL REFERENCES pos_sesiones (id),
    documento_id  uuid          NOT NULL REFERENCES documentos (id),
    total         numeric(12,2) NOT NULL,
    recibido      numeric(12,2),
    vuelto        numeric(12,2) NOT NULL DEFAULT 0,
    creado_en     timestamptz   NOT NULL DEFAULT now(),
    creado_por    uuid          DEFAULT ctx_usuario(),

    CONSTRAINT ck_pos_ventas_montos CHECK (total >= 0 AND vuelto >= 0 AND (recibido IS NULL OR recibido >= 0))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_pos_ventas_documento ON pos_ventas (documento_id);
CREATE INDEX IF NOT EXISTS ix_pos_ventas_sesion ON pos_ventas (sesion_id);

CREATE TABLE IF NOT EXISTS pos_pagos (
    id          uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id  uuid          NOT NULL REFERENCES empresas (id),
    venta_id    uuid          NOT NULL REFERENCES pos_ventas (id),
    medio       text          NOT NULL,
    -- En efectivo: lo que queda en caja (lo recibido menos el vuelto).
    monto       numeric(12,2) NOT NULL,
    referencia  text,
    creado_en   timestamptz   NOT NULL DEFAULT now(),
    creado_por  uuid          DEFAULT ctx_usuario(),

    CONSTRAINT ck_pos_pagos_medio CHECK (medio IN ('efectivo', 'yape', 'tarjeta', 'transferencia')),
    CONSTRAINT ck_pos_pagos_monto CHECK (monto > 0)
);
CREATE INDEX IF NOT EXISTS ix_pos_pagos_venta ON pos_pagos (venta_id);

COMMENT ON COLUMN pos_pagos.medio IS '055: efectivo | yape (Yape o Plin) | tarjeta | transferencia.';

-- =============================================================================
-- AISLAMIENTO, BITÁCORA, DUEÑOS Y PERMISOS
-- =============================================================================

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['pos_cajas', 'pos_sesiones', 'pos_movimientos', 'pos_ventas', 'pos_pagos'] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'p_' || t) THEN
            EXECUTE format(
                'CREATE POLICY %I ON %I USING (ctx_es_super() OR empresa_id = ctx_empresa()) ' ||
                'WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa())', 'p_' || t, t);
        END IF;
    END LOOP;

    -- Cajas y aperturas: bitácora completa.
    FOREACH t IN ARRAY ARRAY['pos_cajas', 'pos_sesiones'] LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = t::regclass AND NOT tgisinternal AND tgname LIKE 'auditar_%') THEN
            PERFORM vigilar_tabla(t);
        END IF;
    END LOOP;

    -- Movimientos, ventas y pagos: solo inserción (el candado y su bitácora).
    FOREACH t IN ARRAY ARRAY['pos_movimientos', 'pos_ventas', 'pos_pagos'] LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = t::regclass AND NOT tgisinternal AND tgname LIKE 'inmutable_%') THEN
            PERFORM vigilar_tabla_inmutable(t);
        END IF;
    END LOOP;
END $$;

GRANT SELECT, INSERT, UPDATE ON pos_cajas, pos_sesiones TO plataforma_app;
GRANT SELECT, INSERT ON pos_movimientos, pos_ventas, pos_pagos TO plataforma_app;

INSERT INTO permisos (codigo, area, descripcion) VALUES
    ('pos.vender',      'pos', 'Vender en el punto de venta (abrir y cerrar su caja)'),
    ('pos.movimientos', 'pos', 'Registrar ingresos y retiros de efectivo en su caja'),
    ('pos.descuentos',  'pos', 'Dar descuentos en el punto de venta'),
    ('pos.cajas_ver',   'pos', 'Ver las cajas y los arqueos de todos')
ON CONFLICT (codigo) DO NOTHING;

-- Administrador y Jefe de sucursal: todo. Facturador: vender y sus movimientos.
INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT r.id, p.codigo
  FROM roles r
 CROSS JOIN (VALUES ('pos.vender'), ('pos.movimientos'), ('pos.descuentos'), ('pos.cajas_ver')) AS p (codigo)
 WHERE (r.id IN ('11111111-1111-1111-1111-111111111111', '55555555-5555-5555-5555-555555555555')
        OR r.basado_en = '55555555-5555-5555-5555-555555555555')
   AND NOT EXISTS (SELECT 1 FROM rol_permiso o WHERE o.rol_id = r.id AND o.permiso_codigo = p.codigo);

INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT r.id, p.codigo
  FROM roles r
 CROSS JOIN (VALUES ('pos.vender'), ('pos.movimientos')) AS p (codigo)
 WHERE (r.id = '22222222-2222-2222-2222-222222222222' OR r.basado_en = '22222222-2222-2222-2222-222222222222')
   AND NOT EXISTS (SELECT 1 FROM rol_permiso o WHERE o.rol_id = r.id AND o.permiso_codigo = p.codigo);

INSERT INTO migraciones_aplicadas (nombre) VALUES ('055_punto_de_venta')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '055: punto de venta y caja listos (módulo «Punto de venta»: ninguna empresa lo tiene hasta que el super administrador lo active). Nada se borró.';
END $$;