-- =============================================================================
-- 026 — EL TIPO DE CAMBIO DE UN COMPROBANTE EN DÓLARES (punto 4)
--
-- El servicio ya emite en dólares, pero exige el tipo de cambio y la pantalla
-- no lo pedía: decía «emite en soles por ahora». Aquí se guarda con el
-- borrador. Soles por unidad de la moneda, con los 3 decimales que declara
-- SUNAT; nulo en soles.
--
-- No se borra nada.
-- =============================================================================

BEGIN;

ALTER TABLE documentos ADD COLUMN tipo_cambio numeric(9,3);

ALTER TABLE documentos ADD CONSTRAINT ck_documentos_tipo_cambio
    CHECK (tipo_cambio IS NULL OR (tipo_cambio > 0 AND tipo_cambio < 100));

COMMENT ON COLUMN documentos.tipo_cambio IS
    'Soles por unidad de la moneda (3 decimales), el de venta de la SBS para la fecha de emisión. Nulo en soles.';

INSERT INTO migraciones_aplicadas (nombre) VALUES ('026_tipo_de_cambio');

COMMIT;