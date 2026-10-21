-- =============================================================================
-- 040 — FACTURAS: CONTADO O CRÉDITO (CON CUOTAS) Y DETRACCIÓN
--        (21 oct 2026, lo que pidió el contador)
--
-- Lo mismo que pregunta «Emitir Factura» en SUNAT SOL:
--
--   · ¿Al contado o al crédito? A crédito, las cuotas (monto y vencimiento).
--     Obligatorio desde 2022 con receptor RUC (errores 3249 / 3251).
--   · ¿Sujeta a detracción? Código del bien o servicio (catálogo 54),
--     porcentaje, cuenta del Banco de la Nación y medio de pago (catálogo 59).
--     Un servicio contable de más de S/ 700 la lleva (código 022, 12 %).
--
-- El servicio de facturación ya sabía hacerlo; faltaba que la plataforma lo
-- preguntara y lo guardara. Hasta hoy toda factura salía al contado y sin
-- detracción.
--
-- EN LA EMPRESA, LO HABITUAL: la cuenta de detracciones y el código y
-- porcentaje que más usa. Se escriben una vez (al emitir la primera factura
-- con detracción) y la pantalla los propone después.
--
-- Solo añade columnas, con valores por defecto: los documentos de antes
-- quedan «al contado, sin detracción», que es como salieron. Nada se borra.
-- Se puede correr dos veces.
-- =============================================================================

BEGIN;

-- --- documentos --------------------------------------------------------------

ALTER TABLE documentos ADD COLUMN IF NOT EXISTS forma_pago text  NOT NULL DEFAULT 'contado';
ALTER TABLE documentos ADD COLUMN IF NOT EXISTS cuotas     jsonb NOT NULL DEFAULT '[]'::jsonb;
ALTER TABLE documentos ADD COLUMN IF NOT EXISTS detraccion jsonb;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_documentos_forma_pago') THEN
        ALTER TABLE documentos ADD CONSTRAINT ck_documentos_forma_pago
            CHECK (forma_pago IN ('contado', 'credito'));
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_documentos_cuotas') THEN
        ALTER TABLE documentos ADD CONSTRAINT ck_documentos_cuotas CHECK (
            jsonb_typeof(cuotas) = 'array'
            AND jsonb_array_length(cuotas) <= 60
            -- Al contado no hay cuotas (error 3252 de SUNAT).
            AND (forma_pago = 'credito' OR jsonb_array_length(cuotas) = 0));
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_documentos_detraccion') THEN
        ALTER TABLE documentos ADD CONSTRAINT ck_documentos_detraccion CHECK (
            detraccion IS NULL
            OR (jsonb_typeof(detraccion) = 'object'
                -- Solo facturas: la boleta no da crédito fiscal.
                AND tipo = '01'
                AND (detraccion ->> 'codigo') ~ '^[0-9]{3}$'
                AND (detraccion ->> 'porcentaje')::numeric > 0
                AND (detraccion ->> 'porcentaje')::numeric <= 100));
    END IF;
END $$;

COMMENT ON COLUMN documentos.forma_pago IS '040: contado | credito. Al contado no hay cuotas.';
COMMENT ON COLUMN documentos.cuotas IS
    '040: [{numero, monto, vence}] a crédito. Suman el monto neto pendiente (total − detracción).';
COMMENT ON COLUMN documentos.detraccion IS
    '040: {codigo (cat. 54), porcentaje, cuenta (Banco de la Nación), medioPago (cat. 59), monto (S/)}. Solo facturas.';


-- --- empresas: lo habitual de la detracción ---------------------------------

ALTER TABLE empresas ADD COLUMN IF NOT EXISTS detraccion_cuenta     text;
ALTER TABLE empresas ADD COLUMN IF NOT EXISTS detraccion_codigo     text;
ALTER TABLE empresas ADD COLUMN IF NOT EXISTS detraccion_porcentaje numeric(5,2);

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_empresas_detraccion') THEN
        ALTER TABLE empresas ADD CONSTRAINT ck_empresas_detraccion CHECK (
            (detraccion_cuenta IS NULL OR detraccion_cuenta ~ '^[0-9][0-9-]{4,23}[0-9]$')
            AND (detraccion_codigo IS NULL OR detraccion_codigo ~ '^[0-9]{3}$')
            AND (detraccion_porcentaje IS NULL OR detraccion_porcentaje > 0 AND detraccion_porcentaje <= 100));
    END IF;
END $$;

COMMENT ON COLUMN empresas.detraccion_cuenta IS
    '040: cuenta de detracciones del Banco de la Nación (la abre el vendedor). La propone la pantalla de emisión.';


INSERT INTO migraciones_aplicadas (nombre) VALUES ('040_forma_pago_y_detraccion')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN RAISE NOTICE '040: facturas al contado o al crédito (cuotas) y con detracción. Nada se borró.'; END $$;