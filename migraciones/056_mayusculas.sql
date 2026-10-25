-- =============================================================================
-- 056 — LO QUE SE ESCRIBE, EN MAYÚSCULAS (e174, 25 oct 2026)
--
-- Nombres, razones sociales, direcciones, descripciones de productos, notas…
-- se guardan en MAYÚSCULAS, como los ve SUNAT y como se ven en pantalla.
--
-- Dos capas:
--   1. La API los pone en mayúsculas antes de guardar (Plataforma.Core/Datos/
--      Mayusculas.cs). Es la principal: así lo que EF tiene en memoria es igual
--      a lo guardado, y un comprobante viaja a SUNAT tal como quedó.
--   2. Esta migración pone un disparador en las tablas de datos maestros y en
--      las que se escriben con SQL directo (cajas, movimientos de caja, notas
--      del inventario), por si algo escribe sin pasar por la API.
--
-- NO se tocan: correos, teléfonos, WhatsApp, enlaces, códigos de lista
-- (contado, efectivo, merma…), números de operación ni cuentas. Tampoco las
-- líneas de comprobantes ni las guías YA emitidos: son lo que se mandó a SUNAT.
--
-- Pasa a mayúsculas, UNA VEZ, los datos maestros que ya existen (clientes,
-- productos, proveedores, categorías, sucursales, conductores, vehículos,
-- transportistas). Ningún código único choca: los índices ya comparan en
-- minúsculas, y la placa ya estaba en mayúsculas.
--
-- Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

-- Los datos de todas las empresas (la migración la corre el dueño de la base).
SET LOCAL app.super = '1';

CREATE OR REPLACE FUNCTION fn_mayusculas() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    j jsonb := to_jsonb(NEW);
    c text;
BEGIN
    FOREACH c IN ARRAY TG_ARGV LOOP
        IF jsonb_typeof(j -> c) = 'string' AND (j ->> c) <> upper(j ->> c) THEN
            j := jsonb_set(j, ARRAY[c], to_jsonb(upper(j ->> c)));
        END IF;
    END LOOP;
    NEW := jsonb_populate_record(NEW, j);
    RETURN NEW;
END $$;

COMMENT ON FUNCTION fn_mayusculas() IS
    '056: pone en mayúsculas las columnas que recibe como argumentos (BEFORE INSERT OR UPDATE).';

-- El nombre empieza por «a_» para que corra antes que los demás BEFORE.
DO $$
DECLARE
    t record;
BEGIN
    FOR t IN SELECT * FROM (VALUES
        ('clientes',           'razon_social, nombre_comercial, direccion'),
        ('productos',          'codigo, descripcion'),
        ('proveedores',        'razon_social, direccion'),
        ('producto_categorias','nombre'),
        ('sucursales',         'nombre, direccion'),
        ('conductores',        'nombres, apellidos, licencia'),
        ('vehiculos',          'placa, descripcion'),
        ('transportistas',     'razon_social'),
        ('pos_cajas',          'nombre'),
        ('pos_sesiones',       'nota_apertura, observacion_cierre'),
        ('pos_movimientos',    'motivo, detalle'),
        ('inv_operaciones',    'documento_proveedor, nota'),
        ('inv_traslados',      'nota')
    ) AS x (tabla, columnas)
    LOOP
        IF to_regclass(t.tabla) IS NOT NULL THEN
            EXECUTE format(
                'CREATE OR REPLACE TRIGGER a_mayusculas_%1$s BEFORE INSERT OR UPDATE ON %1$I '
                'FOR EACH ROW EXECUTE FUNCTION fn_mayusculas(%2$s)',
                t.tabla,
                (SELECT string_agg(quote_literal(btrim(c)), ', ') FROM unnest(string_to_array(t.columnas, ',')) c));
        END IF;
    END LOOP;
END $$;

-- --- Los datos que ya existen (solo los maestros) ------------------------------

UPDATE clientes SET razon_social = upper(razon_social), nombre_comercial = upper(nombre_comercial), direccion = upper(direccion)
 WHERE razon_social <> upper(razon_social) OR nombre_comercial <> upper(nombre_comercial) OR direccion <> upper(direccion);

UPDATE productos SET codigo = upper(codigo), descripcion = upper(descripcion)
 WHERE codigo <> upper(codigo) OR descripcion <> upper(descripcion);

UPDATE proveedores SET razon_social = upper(razon_social), direccion = upper(direccion)
 WHERE razon_social <> upper(razon_social) OR direccion <> upper(direccion);

UPDATE producto_categorias SET nombre = upper(nombre) WHERE nombre <> upper(nombre);

UPDATE sucursales SET nombre = upper(nombre), direccion = upper(direccion)
 WHERE nombre <> upper(nombre) OR direccion <> upper(direccion);

UPDATE conductores SET nombres = upper(nombres), apellidos = upper(apellidos), licencia = upper(licencia)
 WHERE nombres <> upper(nombres) OR apellidos <> upper(apellidos) OR licencia <> upper(licencia);

UPDATE transportistas SET razon_social = upper(razon_social) WHERE razon_social <> upper(razon_social);

-- La placa ya se guarda en mayúsculas (lo exige la base): solo la descripción.
UPDATE vehiculos SET descripcion = upper(descripcion) WHERE descripcion <> upper(descripcion);

INSERT INTO migraciones_aplicadas (nombre) VALUES ('056_mayusculas')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '056: lo que se escribe se guarda en MAYÚSCULAS (datos maestros pasados a mayúsculas; comprobantes y guías emitidos, intactos). Nada se borró.';
END $$;