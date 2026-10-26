-- =============================================================================
-- 063 — VARIANTES: TALLA, COLOR… (e186, 25 oct 2026)
--
-- Inventario ampliado D3. Un MODELO («POLO BÁSICO») agrupa sus variantes
-- («POLO BÁSICO · M / ROJO»). Cada variante ES UN PRODUCTO de verdad: su
-- código, su precio, su stock, su kardex, su línea en la factura y en la guía.
-- Por eso no cambia nada del stock: el modelo solo agrupa (no se vende ni se
-- cuenta).
--
--   · productos.es_modelo   el que agrupa (nunca controla stock).
--   · productos.padre_id    la variante apunta a su modelo.
--   · productos.atributos   {"TALLA": "M", "COLOR": "ROJO"} (jsonb).
--
-- Solo crea. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

ALTER TABLE productos ADD COLUMN IF NOT EXISTS es_modelo boolean NOT NULL DEFAULT false;
ALTER TABLE productos ADD COLUMN IF NOT EXISTS padre_id  uuid REFERENCES productos (id);
ALTER TABLE productos ADD COLUMN IF NOT EXISTS atributos jsonb;
CREATE INDEX IF NOT EXISTS ix_productos_padre ON productos (padre_id) WHERE padre_id IS NOT NULL;

COMMENT ON COLUMN productos.es_modelo IS '063: modelo que agrupa variantes (talla, color…). No se vende ni controla stock: lo hacen sus variantes.';
COMMENT ON COLUMN productos.padre_id IS '063: la variante apunta a su modelo.';
COMMENT ON COLUMN productos.atributos IS '063: los valores de la variante, p. ej. {"TALLA": "M", "COLOR": "ROJO"}.';

-- Un servicio, un kit o un modelo nunca controlan stock propio.
CREATE OR REPLACE FUNCTION fn_producto_servicio_sin_stock() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.unidad_medida = 'ZZ' OR NEW.es_kit OR NEW.es_modelo THEN NEW.controla_stock := false; END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS producto_servicio_sin_stock ON productos;
CREATE TRIGGER producto_servicio_sin_stock
    BEFORE INSERT OR UPDATE OF unidad_medida, controla_stock, es_kit, es_modelo ON productos
    FOR EACH ROW EXECUTE FUNCTION fn_producto_servicio_sin_stock();

-- La variante: su modelo es un modelo de la misma empresa; un modelo no es variante de otro.
CREATE OR REPLACE FUNCTION fn_producto_variante() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.padre_id IS NOT NULL THEN
        IF NEW.es_modelo OR NEW.padre_id = NEW.id THEN
            RAISE EXCEPTION 'Un modelo no puede ser variante de otro.' USING ERRCODE = 'check_violation';
        END IF;
        IF NOT EXISTS (SELECT 1 FROM productos p WHERE p.id = NEW.padre_id AND p.es_modelo AND p.empresa_id = NEW.empresa_id) THEN
            RAISE EXCEPTION 'La variante tiene que apuntar a un modelo de la misma empresa.' USING ERRCODE = 'check_violation';
        END IF;
    END IF;
    IF NEW.atributos IS NOT NULL AND jsonb_typeof(NEW.atributos) <> 'object' THEN
        RAISE EXCEPTION 'Los atributos de una variante van como {"TALLA": "M"}.' USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;

CREATE OR REPLACE TRIGGER b_producto_variante BEFORE INSERT OR UPDATE OF padre_id, es_modelo, atributos ON productos
    FOR EACH ROW EXECUTE FUNCTION fn_producto_variante();

-- La misma combinación no se repite en un modelo.
CREATE UNIQUE INDEX IF NOT EXISTS ux_productos_variante ON productos (padre_id, atributos)
 WHERE padre_id IS NOT NULL AND activo;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('063_variantes')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '063: variantes (talla, color…): cada una es un producto con su stock. Nada se borró.';
END $$;