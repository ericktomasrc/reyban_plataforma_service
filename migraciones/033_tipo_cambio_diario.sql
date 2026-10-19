-- =============================================================================
-- 033 — EL TIPO DE CAMBIO, GUARDADO POR DÍA (18 oct 2026)
--
-- El de SUNAT (que sale del cierre de la SBS) es el mismo para todas las
-- empresas y para todo el día. Antes se consultaba a Decolecta en cada factura
-- en dólares; ahora se consulta UNA vez por fecha y se guarda aquí.
--
--   publicado = true   SUNAT tiene tipo de cambio para esa fecha.
--   publicado = false  no publicó (domingo, feriado) o todavía no publica hoy:
--                      se propone el último publicado, avisando de qué día es.
--
-- Datos públicos, sin empresa: los lee y los guarda la plataforma. Nada se
-- borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS tipos_cambio (
    id             uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    moneda         char(3)       NOT NULL DEFAULT 'USD',
    fecha          date          NOT NULL,
    compra         numeric(10,3),
    venta          numeric(10,3),
    publicado      boolean       NOT NULL,
    fuente         text          NOT NULL DEFAULT 'decolecta',
    consultado_en  timestamptz   NOT NULL DEFAULT now(),

    creado_en      timestamptz   NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean       NOT NULL DEFAULT true,
    version        integer       NOT NULL DEFAULT 1,

    CONSTRAINT ux_tipos_cambio UNIQUE (moneda, fecha),
    CONSTRAINT ck_tipos_cambio_venta CHECK (NOT publicado OR (venta IS NOT NULL AND venta > 0 AND venta < 100))
);

COMMENT ON TABLE tipos_cambio IS
    'Tipo de cambio SUNAT por día (una consulta por fecha). publicado=false: ese día no hubo publicación.';

ALTER TABLE tipos_cambio ENABLE ROW LEVEL SECURITY;
ALTER TABLE tipos_cambio FORCE  ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE tablename = 'tipos_cambio' AND policyname = 'p_tipos_cambio') THEN
        -- Son datos públicos de SUNAT: iguales para todas las empresas.
        CREATE POLICY p_tipos_cambio ON tipos_cambio USING (true) WITH CHECK (true);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_tipos_cambio') THEN
        PERFORM vigilar_tabla('tipos_cambio');
    END IF;
END $$;

GRANT SELECT, INSERT, UPDATE ON tipos_cambio TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('033_tipo_cambio_diario')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '033: tipo de cambio por día listo (se llena solo, con la primera factura en dólares de cada fecha). Nada se borró.';
END $$;