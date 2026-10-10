-- =============================================================================
-- 012 — RECEPTOR DEL WEBHOOK DE FACTURACIÓN
--
-- Lo que necesita el paso 4 y no estaba en la base:
--
--   1. Dónde guardar el secreto con el que el servicio firma los avisos.
--   2. Que un aviso con firma falsa no pueda ocupar el evento_id del
--      verdadero.
--   3. Con qué ordenar dos avisos del mismo comprobante que lleguen al revés.
--
-- Todo en una transacción: si algo falla a medias, la base queda como estaba.
-- psql sin --single-transaction confirma sentencia a sentencia, y una 012
-- aplicada a medias es peor que una que no se aplicó.
-- =============================================================================

BEGIN;


-- =============================================================================
-- 1. EL SECRETO DEL WEBHOOK
--
-- El servicio firma cada aviso con un secreto `whsec_...` propio de cada
-- webhook, que muestra UNA sola vez al registrarlo en su panel. No es la clave
-- `fac_...`: son dos secretos distintos con dos usos distintos, y uno no se
-- deduce del otro.
--
-- Va en la misma fila que la clave de API porque se configuran juntos, en la
-- misma pantalla y por la misma persona, y porque «guardar siempre inserta»:
-- al rotar la clave, la fila nueva tiene que llevarse también el secreto.
--
-- Cifrado igual que `clave_cifrada`: nonce + etiqueta + texto cifrado en un
-- solo blob, con PLATAFORMA_LLAVE_MAESTRA.
--
-- NULL = todavía no configurado. El receptor lo trata como firma inválida.
-- =============================================================================

ALTER TABLE credenciales_facturacion
    ADD COLUMN webhook_secreto_cifrado bytea;


-- -----------------------------------------------------------------------------
-- 1b. Y TAPARLO EN LA BITÁCORA
--
-- `vigilar_tabla` enganchó esta tabla en la 004 con `clave_cifrada` como única
-- columna sensible. Si no se añade la nueva, el secreto cifrado aparece
-- entero en `bitacora.antes` y `bitacora.despues` cada vez que alguien guarda
-- las credenciales. Y eso no da ningún error.
--
-- CÓMO SE REENGANCHA SIN DEPENDER DE LOS DETALLES DE `vigilar_tabla`:
--
--   · se guardan las definiciones de los disparadores de la tabla,
--   · se quitan los dos que pone `vigilar_tabla` (fn_auditar y
--     fn_marcar_auditoria), porque los crea con CREATE TRIGGER y chocarían,
--   · se vuelve a llamar a `vigilar_tabla` con la lista nueva,
--   · si `vigilar_tabla` no recreó algún disparador que había antes
--     —el de los campos de auditoría, por ejemplo—, se restaura el original,
--   · y se comprueba que el de auditoría existe y conoce la columna nueva.
--
-- Si cualquiera de esas comprobaciones falla, RAISE y la transacción entera
-- se deshace.
-- -----------------------------------------------------------------------------

CREATE TEMP TABLE _disparadores_previos ON COMMIT DROP AS
SELECT tg.tgname::text               AS nombre,
       p.proname::text               AS funcion,
       pg_get_triggerdef(tg.oid)     AS definicion
  FROM pg_trigger tg
  JOIN pg_proc    p ON p.oid = tg.tgfoid
 WHERE tg.tgrelid = 'credenciales_facturacion'::regclass
   AND NOT tg.tgisinternal;

DO $$
DECLARE
    d record;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM _disparadores_previos WHERE funcion = 'fn_auditar') THEN
        RAISE EXCEPTION 'credenciales_facturacion no tenía disparador de auditoría. '
                        'Algo no cuadra con la 004: no se sigue.';
    END IF;

    -- LOS DOS, NO SOLO EL DE LA BITÁCORA.
    --
    -- `vigilar_tabla` (001) engancha DOS disparadores con CREATE TRIGGER, no
    -- con CREATE OR REPLACE:
    --
    --     marcar_<tabla>   BEFORE INSERT OR UPDATE  → fn_marcar_auditoria()
    --     auditar_<tabla>  AFTER  ...               → fn_auditar(sensibles)
    --
    -- Quitando solo el segundo, la llamada de abajo intenta recrear el primero
    -- y revienta con «trigger ... already exists». Comprobado: la 012 anterior
    -- moría justo ahí.
    --
    -- Se quitan los dos y `vigilar_tabla` los vuelve a poner. El de marcado no
    -- lleva parámetros, así que recrearlo lo deja idéntico.
    FOR d IN
        SELECT nombre FROM _disparadores_previos
         WHERE funcion IN ('fn_auditar', 'fn_marcar_auditoria')
    LOOP
        EXECUTE format('DROP TRIGGER %I ON credenciales_facturacion', d.nombre);
    END LOOP;
END $$;

-- Ahora sí: los dos que crea están quitados, así que los recrea sin chocar.
SELECT vigilar_tabla('credenciales_facturacion',
                     ARRAY['clave_cifrada', 'webhook_secreto_cifrado']);

DO $$
DECLARE
    d record;
    def_auditoria text;
BEGIN
    -- Lo que había y ya no está, se restaura tal cual.
    FOR d IN
        SELECT prev.nombre, prev.funcion, prev.definicion
          FROM _disparadores_previos prev
         WHERE prev.funcion <> 'fn_auditar'
           AND NOT EXISTS (
                 SELECT 1
                   FROM pg_trigger tg
                   JOIN pg_proc p ON p.oid = tg.tgfoid
                  WHERE tg.tgrelid = 'credenciales_facturacion'::regclass
                    AND NOT tg.tgisinternal
                    AND p.proname = prev.funcion)
    LOOP
        RAISE NOTICE 'Se restaura el disparador % (%)', d.nombre, d.funcion;
        EXECUTE d.definicion;
    END LOOP;

    -- Y el de auditoría tiene que existir y conocer la columna nueva.
    SELECT pg_get_triggerdef(tg.oid) INTO def_auditoria
      FROM pg_trigger tg
      JOIN pg_proc p ON p.oid = tg.tgfoid
     WHERE tg.tgrelid = 'credenciales_facturacion'::regclass
       AND NOT tg.tgisinternal
       AND p.proname = 'fn_auditar';

    IF def_auditoria IS NULL THEN
        RAISE EXCEPTION 'vigilar_tabla no dejó disparador de auditoría en credenciales_facturacion.';
    END IF;

    IF position('webhook_secreto_cifrado' IN def_auditoria) = 0
       OR position('clave_cifrada' IN def_auditoria) = 0 THEN
        RAISE EXCEPTION 'El disparador de auditoría no tapa las dos columnas sensibles: %',
                        def_auditoria;
    END IF;

    -- Y el de marcado tiene que haber vuelto: sin él, `creado_por` y
    -- `modificado_por` dejarían de escribirse solos en esta tabla, en silencio.
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger tg JOIN pg_proc p ON p.oid = tg.tgfoid
         WHERE tg.tgrelid = 'credenciales_facturacion'::regclass
           AND NOT tg.tgisinternal
           AND p.proname = 'fn_marcar_auditoria')
    THEN
        RAISE EXCEPTION 'Se perdió el disparador de los campos de auditoría de credenciales_facturacion.';
    END IF;
END $$;


-- =============================================================================
-- 2. EL ÍNDICE DE IDEMPOTENCIA, SOLO PARA AVISOS AUTÉNTICOS
--
-- Con el índice de la 005, un aviso con firma falsa que copie el evento_id de
-- uno real lo ocupa primero, y el verdadero choca contra el índice y se trata
-- como duplicado: se pierde. Guardar los avisos inválidos sigue siendo
-- correcto —es lo que dice quién llama a la puerta—, pero no pueden reservar
-- nada.
-- =============================================================================

DROP INDEX ux_webhooks_evento;

CREATE UNIQUE INDEX ux_webhooks_evento
    ON webhooks_recibidos (evento_id)
    WHERE evento_id IS NOT NULL AND firma_valida;


-- =============================================================================
-- 3. CON QUÉ ORDENAR LOS AVISOS DE UN MISMO COMPROBANTE
--
-- El servicio reintenta con espera creciente, así que dos avisos del mismo
-- comprobante pueden llegar al revés: el «anulado» antes que el «aceptado»
-- que se quedó reintentando. Sin esto, el segundo en llegar pisaría al
-- primero y la factura anulada volvería a decir «aceptada».
--
-- Cada aviso trae `ocurridoEn`, el now() de la transacción del servicio en
-- que cambió el estado. Se guarda el del último aviso aplicado, y uno que no
-- sea posterior no se aplica.
--
-- Sin disparador nuevo: `vigilar_tabla` enganchó `documentos` sin lista de
-- columnas, así que la columna entra sola en la bitácora.
-- =============================================================================

ALTER TABLE documentos
    ADD COLUMN estado_servicio_en timestamptz;

COMMENT ON COLUMN documentos.estado_servicio_en IS
    'ocurridoEn del último aviso del servicio aplicado. Un aviso que no sea posterior no se aplica.';

COMMENT ON COLUMN credenciales_facturacion.webhook_secreto_cifrado IS
    'Secreto whsec_ con el que el servicio firma los avisos. Cifrado como clave_cifrada. Tapado en la bitácora.';


INSERT INTO migraciones_aplicadas (nombre) VALUES ('012_webhook_facturacion');

COMMIT;