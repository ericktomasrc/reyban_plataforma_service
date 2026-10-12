-- =============================================================================
-- 014 — LA EMPRESA SE DA DE ALTA CON UN NOMBRE; EL RUC LO TRAE LA CLAVE
--
-- Diseño: alta-empresa-diseno.md, parte B (§3.2 y §3.4).
--
-- Hasta hoy el RUC y la razón social se tecleaban dos veces —en el servicio y
-- aquí— y nada los ataba. Desde ahora:
--
--   · El alta pide solo un NOMBRE para mostrar (`empresas.nombre`).
--   · El RUC, la razón social y el ambiente llegan del servicio, al pulsar
--     «Conectar» con la clave fac_ de la empresa (`GET /v1/emisor`).
--   · Un RUC, una empresa activa: `ux_empresas_ruc` pasa a ser parcial.
--   · El RUC no cambia una vez puesto: lo impide un disparador.
--
-- Todo en una transacción, como la 012 y la 013.
-- =============================================================================

BEGIN;


-- =============================================================================
-- 1. EL NOMBRE
--
-- Es lo que se teclea en el alta y lo que se ve en la cabecera, en la lista y
-- en los correos. Las empresas que ya existen lo heredan del nombre comercial
-- o, si no tienen, de la razón social.
-- =============================================================================

ALTER TABLE empresas ADD COLUMN nombre text;

UPDATE empresas
   SET nombre = COALESCE(NULLIF(btrim(nombre_comercial), ''), razon_social);

ALTER TABLE empresas ALTER COLUMN nombre SET NOT NULL;

ALTER TABLE empresas
    ADD CONSTRAINT ck_empresas_nombre
    CHECK (length(btrim(nombre)) BETWEEN 1 AND 200);


-- =============================================================================
-- 2. RUC Y RAZÓN SOCIAL: OPCIONALES HASTA CONECTAR, Y SIEMPRE JUNTOS
--
-- `ck_empresas_ruc` (once dígitos) sigue valiendo: un NULL lo pasa.
-- `ck_empresas_datos_oficiales`: si hay RUC hay razón social. Los dos llegan
-- juntos del servicio; uno sin el otro sería un alta a medias.
-- =============================================================================

ALTER TABLE empresas ALTER COLUMN ruc          DROP NOT NULL;
ALTER TABLE empresas ALTER COLUMN razon_social DROP NOT NULL;

ALTER TABLE empresas
    ADD CONSTRAINT ck_empresas_datos_oficiales
    CHECK (ruc IS NULL OR razon_social IS NOT NULL);


-- =============================================================================
-- 3. EL AMBIENTE Y CUÁNDO SE PREGUNTÓ
--
-- `ambiente` es el del servicio ('beta' o 'produccion'), nunca se teclea aquí.
-- `datos_oficiales_en` es cuándo se preguntó por última vez: la pantalla lo
-- enseña junto a «vienen del servicio».
-- =============================================================================

ALTER TABLE empresas
    ADD COLUMN ambiente           text,
    ADD COLUMN datos_oficiales_en timestamptz;

ALTER TABLE empresas
    ADD CONSTRAINT ck_empresas_ambiente
    CHECK (ambiente IS NULL OR ambiente IN ('beta', 'produccion'));


-- =============================================================================
-- 4. UN RUC, UNA EMPRESA ACTIVA
--
-- Antes: único SIEMPRE, también entre desactivadas. Ahora: único entre las
-- activas (decisión del 10 de octubre, §10.1). Es lo que permite corregir un
-- RUC equivocado: se desactiva la empresa mala y se da de alta otra.
--
-- CONSECUENCIA: puede haber dos filas con el mismo RUC si una está
-- desactivada. Toda consulta que busque «la empresa de este RUC» tiene que
-- filtrar por `activo`.
-- =============================================================================

DROP INDEX ux_empresas_ruc;

CREATE UNIQUE INDEX ux_empresas_ruc
    ON empresas (ruc)
    WHERE ruc IS NOT NULL AND activo;


-- =============================================================================
-- 5. EL RUC NO CAMBIA UNA VEZ PUESTO
--
-- De NULL a un valor, sí (es «Conectar»). De un valor a otro, o a NULL, no:
-- los comprobantes emitidos llevan ese RUC, y una empresa que cambia de RUC
-- es otra empresa. Igual que el identificador del portal en el servicio.
-- =============================================================================

CREATE FUNCTION fn_empresas_ruc_inmutable() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.ruc IS NOT NULL AND NEW.ruc IS DISTINCT FROM OLD.ruc THEN
        RAISE EXCEPTION 'El RUC de una empresa no cambia una vez puesto (empresa %, RUC %).',
                        OLD.id, OLD.ruc
              USING ERRCODE = 'check_violation',
                    HINT    = 'Si fue un error, desactiva esta empresa y da de alta otra.';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER tg_empresas_ruc_inmutable
    BEFORE UPDATE OF ruc ON empresas
    FOR EACH ROW EXECUTE FUNCTION fn_empresas_ruc_inmutable();


-- =============================================================================
-- 6. LO QUE DICE EL SERVICIO DEL WEBHOOK
--
-- La URL a la que el servicio manda los avisos de esta empresa, según
-- `GET /v1/webhook`, y cuándo se preguntó. Si no es la de la plataforma, la
-- pantalla lo dice en rojo: alguien con la clave la cambió (diseño §2.4).
--
-- Sin disparador nuevo: `vigilar_tabla` ya engancha la tabla y ninguna de las
-- dos es sensible.
-- =============================================================================

ALTER TABLE credenciales_facturacion
    ADD COLUMN webhook_url_servicio text,
    ADD COLUMN webhook_revisado_en  timestamptz;


COMMENT ON COLUMN empresas.nombre IS
    'Nombre para mostrar. Se teclea en el alta y se puede editar. El oficial es razon_social, que llega del servicio.';
COMMENT ON COLUMN empresas.ruc IS
    'Llega del servicio al Conectar (GET /v1/emisor). No cambia una vez puesto. Unico entre las empresas activas.';
COMMENT ON COLUMN empresas.ambiente IS
    'beta o produccion, segun el servicio. Nunca se teclea aqui.';
COMMENT ON COLUMN credenciales_facturacion.webhook_url_servicio IS
    'La URL a la que el servicio manda los avisos de esta empresa (GET /v1/webhook).';


-- =============================================================================
-- COMPROBACIONES. Si algo no cuadra, RAISE y no queda nada a medias.
-- =============================================================================

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM empresas WHERE nombre IS NULL OR btrim(nombre) = '') THEN
        RAISE EXCEPTION 'Hay empresas sin nombre después de migrar.';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_indexes
         WHERE indexname = 'ux_empresas_ruc'
           AND indexdef LIKE '%WHERE%activo%') THEN
        RAISE EXCEPTION 'ux_empresas_ruc no quedó parcial.';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
         WHERE tgrelid = 'empresas'::regclass
           AND tgname  = 'tg_empresas_ruc_inmutable') THEN
        RAISE EXCEPTION 'Falta el disparador del RUC inmutable.';
    END IF;
END $$;


INSERT INTO migraciones_aplicadas (nombre) VALUES ('014_alta_desde_servicio');

COMMIT;