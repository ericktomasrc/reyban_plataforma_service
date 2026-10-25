-- =============================================================================
-- 054 — EL MEDIO DE PAGO HABITUAL DE LA DETRACCIÓN (e170, 25 oct 2026)
--
-- Desde la 040 la empresa recuerda su cuenta de detracciones del Banco de la
-- Nación, el código y el porcentaje que más usa. Faltaba el medio de pago
-- (catálogo 59: 001 depósito en cuenta, 003 transferencia, …): quien paga
-- siempre por transferencia tenía que cambiarlo en cada factura.
--
-- Se guarda al emitir una factura con detracción, como los otros tres, y la
-- pantalla lo propone en la siguiente. Vacío = 001 (depósito en cuenta), que
-- es lo que salía hasta hoy.
--
-- Solo añade una columna. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

ALTER TABLE empresas ADD COLUMN IF NOT EXISTS detraccion_medio_pago text;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_empresas_detraccion_medio_pago') THEN
        ALTER TABLE empresas ADD CONSTRAINT ck_empresas_detraccion_medio_pago
            CHECK (detraccion_medio_pago IS NULL OR detraccion_medio_pago ~ '^[0-9]{3}$');
    END IF;
END $$;

COMMENT ON COLUMN empresas.detraccion_medio_pago IS
    '054: medio de pago habitual de la detracción (catálogo 59, tres dígitos). Lo propone la pantalla de emisión.';

INSERT INTO migraciones_aplicadas (nombre) VALUES ('054_detraccion_medio_pago')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN RAISE NOTICE '054: la empresa recuerda también el medio de pago de la detracción. Nada se borró.'; END $$;