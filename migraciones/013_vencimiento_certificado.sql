-- =============================================================================
-- 013 — CUÁNDO VENCE EL CERTIFICADO DIGITAL DE CADA EMPRESA
--
-- El certificado vive en el servicio de facturación, no aquí: es el que firma
-- cada XML. El día que vence, la empresa deja de poder emitir EN SECO, y hoy
-- nada lo avisa. El servicio ya lo dice en `GET /v1/emisor`
-- (`certificadoValidoHasta`); esto guarda la respuesta para poder avisar sin
-- llamarlo en cada pantalla.
--
-- Lo que añade:
--
--   1. Dos columnas en `credenciales_facturacion`: la fecha y cuándo se
--      preguntó por última vez.
--   2. Una puerta estrecha, `empresas_con_facturacion()`, para que la revisión
--      diaria sepa a qué empresas preguntar.
--
-- Todo en una transacción, como la 012.
-- =============================================================================

BEGIN;


-- =============================================================================
-- 1. LA FECHA, JUNTO A LA CLAVE
--
-- En la credencial y no en `empresas` porque es lo que contestó el servicio
-- CON ESA CLAVE: si mañana se rota la clave y apunta a otro servicio, la fecha
-- vieja ya no dice nada de él. Al rotar, el endpoint copia los dos valores a
-- la fila nueva y «Probar conexión» los refresca en el acto.
--
-- LAS DOS NULAS SIGNIFICAN COSAS DISTINTAS, y por eso son dos:
--
--   consultado_en NULL                    todavía no se ha preguntado
--   consultado_en con valor, fecha NULL   se preguntó y el servicio NO tiene
--                                         certificado activo: no puede firmar
--
-- Sin disparador nuevo: `vigilar_tabla` ya engancha la tabla, y estas dos no
-- son sensibles. La revisión diaria deja una fila por empresa y día en la
-- bitácora —como ya la deja cada llamada al anotar `ultima_llamada_en`—, y
-- con ella queda escrito desde cuándo se sabía que el certificado vencía.
-- =============================================================================

ALTER TABLE credenciales_facturacion
    ADD COLUMN certificado_valido_hasta  timestamptz,
    ADD COLUMN certificado_consultado_en timestamptz;

COMMENT ON COLUMN credenciales_facturacion.certificado_valido_hasta IS
    'Hasta cuándo vale el certificado activo del emisor en el servicio (GET /v1/emisor). NULL con consultado_en puesto = el servicio no tiene certificado activo.';

COMMENT ON COLUMN credenciales_facturacion.certificado_consultado_en IS
    'Cuándo se preguntó al servicio por última vez con éxito. NULL = nunca.';


-- =============================================================================
-- 2. LA PUERTA: A QUÉ EMPRESAS HAY QUE PREGUNTAR
--
-- La revisión diaria es un trabajo de fondo: no tiene sesión ni empresa, y con
-- el aislamiento bien puesto no puede listar las credenciales de nadie. Es el
-- mismo huevo y gallina que el ingreso, y se rompe igual: con una función
-- marcada que devuelve LO JUSTO.
--
-- Devuelve solo identificadores de empresa. Ni la clave, ni la URL, ni la
-- fecha: con cada id, la revisión fija el contexto de ESA empresa —sin
-- usuario, igual que el receptor del webhook— y lee su credencial con el
-- aislamiento normal. Lo que esta función enseña es que esas empresas existen
-- y tienen el servicio configurado; nada que no sepa ya quien puede llamarla.
--
-- Solo empresas activas: una dada de baja no emite ni tiene a quién avisar.
-- Las suspendidas SÍ entran: no pueden entrar, pero vuelven, y cuando vuelvan
-- conviene que la fecha esté al día.
-- =============================================================================

CREATE FUNCTION empresas_con_facturacion()
RETURNS TABLE (empresa_id uuid)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = public, pg_temp
AS $$
    SELECT c.empresa_id
      FROM credenciales_facturacion c
      JOIN empresas e ON e.id = c.empresa_id
     WHERE c.activo
       AND e.activo
     ORDER BY c.empresa_id;
$$;

COMMENT ON FUNCTION empresas_con_facturacion() IS
    'Puerta para la revisión diaria del certificado: solo los ids de las empresas activas con credencial activa. No devuelve ningún secreto.';

REVOKE ALL ON FUNCTION empresas_con_facturacion() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION empresas_con_facturacion() TO plataforma_app;


INSERT INTO migraciones_aplicadas (nombre) VALUES ('013_vencimiento_certificado');

COMMIT;