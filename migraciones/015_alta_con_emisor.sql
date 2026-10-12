-- =============================================================================
-- 015 — EL ALTA CREA EL EMISOR EN EL SERVICIO; LA CLAVE ANTERIOR SE VACÍA
--
-- Diseño: alta-empresa-diseno.md §13 (parte C2).
--
-- Desde ahora el alta de la plataforma crea el emisor en el servicio en la
-- misma operación (POST /alta/emisores con la clave de alta) y guarda la fac_
-- y el whsec_ que devuelve. Esta migración prepara `credenciales_facturacion`
-- para tres cosas:
--
--   1. VACIAR LA CLAVE VIEJA. Al rotar, la fila anterior se queda (nada se
--      borra) pero su clave cifrada se vacía cuando se comprueba que el
--      servicio ya la rechaza («Ya la revoqué»), o en el acto si era la misma
--      clave. Una clave revocada guardada no sirve para nada y sí para filtrarse.
--   2. LO QUE DICE EL SERVICIO DEL EMISOR: la última respuesta de
--      GET /v1/emisor (alta, portal, dirección y qué falta para emitir), para
--      enseñarla sin preguntar en cada pantalla.
--   3. DE DÓNDE SALIÓ LA CREDENCIAL: del alta, y si el alta ya se confirmó.
--
-- Todo en una transacción, como la 012, la 013 y la 014.
-- =============================================================================

BEGIN;


-- =============================================================================
-- 1. LA CLAVE CIFRADA PUEDE QUEDAR VACÍA — SOLO EN UNA FILA INACTIVA
--
-- La activa es la que emite: sin clave no puede existir. El CHECK lo dice en
-- la base, para que siga siendo cierto aunque alguien escriba sin pasar por
-- la aplicación.
-- =============================================================================

ALTER TABLE credenciales_facturacion ALTER COLUMN clave_cifrada DROP NOT NULL;

ALTER TABLE credenciales_facturacion
    ADD COLUMN clave_vaciada_en        timestamptz,
    ADD COLUMN revocada_comprobada_en  timestamptz,
    ADD COLUMN revocada_comprobada_por uuid,
    ADD COLUMN revocada_comprobacion   text;

ALTER TABLE credenciales_facturacion
    ADD CONSTRAINT ck_credenciales_clave_activa
    CHECK (NOT activo OR clave_cifrada IS NOT NULL);

-- Vaciada es vaciada: con fecha de vaciado no puede volver a tener clave.
ALTER TABLE credenciales_facturacion
    ADD CONSTRAINT ck_credenciales_vaciada
    CHECK (clave_vaciada_en IS NULL OR clave_cifrada IS NULL);

-- Una comprobación de revocada solo tiene sentido en una fila retirada.
ALTER TABLE credenciales_facturacion
    ADD CONSTRAINT ck_credenciales_revocada
    CHECK (revocada_comprobada_en IS NULL OR NOT activo);


-- =============================================================================
-- 2. LO QUE DICE EL SERVICIO DEL EMISOR
--
-- La respuesta de GET /v1/emisor, solo los campos que la plataforma usa. No
-- lleva ningún secreto: estado del alta, identificador del portal, correo de
-- respuesta, dirección y la lista de lo que falta. La escriben «Conectar»,
-- «Volver a preguntar al servicio», la confirmación del alta y la revisión
-- diaria.
-- =============================================================================

ALTER TABLE credenciales_facturacion
    ADD COLUMN emisor_estado    jsonb,
    ADD COLUMN emisor_estado_en timestamptz;


-- =============================================================================
-- 3. DE DÓNDE SALIÓ
--
-- `creada_por_alta`: la crearon el alta de la plataforma y el servicio en la
-- misma operación (no se pegó a mano). `alta_confirmada_en`: cuándo confirmó
-- la plataforma el alta al servicio. Mientras sea nulo, el emisor está «sin
-- confirmar» allí, y la pantalla lo dice.
-- =============================================================================

ALTER TABLE credenciales_facturacion
    ADD COLUMN creada_por_alta    boolean NOT NULL DEFAULT false,
    ADD COLUMN alta_confirmada_en timestamptz;


COMMENT ON COLUMN credenciales_facturacion.clave_cifrada IS
    'La fac_ cifrada. Nula solo en una fila inactiva, despues de comprobar que el servicio ya la rechaza.';
COMMENT ON COLUMN credenciales_facturacion.revocada_comprobacion IS
    'Que se comprobo al vaciarla: el servicio la rechaza, o era la misma clave que la activa.';
COMMENT ON COLUMN credenciales_facturacion.emisor_estado IS
    'Ultima respuesta de GET /v1/emisor (sin secretos): alta, portal, direccion, listo.';
COMMENT ON COLUMN credenciales_facturacion.creada_por_alta IS
    'La creo el alta de la plataforma junto con el emisor del servicio.';


-- =============================================================================
-- COMPROBACIONES. Si algo no cuadra, RAISE y no queda nada a medias.
-- =============================================================================

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
         WHERE table_name = 'credenciales_facturacion'
           AND column_name = 'clave_cifrada'
           AND is_nullable = 'NO') THEN
        RAISE EXCEPTION 'clave_cifrada sigue siendo obligatoria.';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conrelid = 'credenciales_facturacion'::regclass
           AND conname  = 'ck_credenciales_clave_activa') THEN
        RAISE EXCEPTION 'Falta ck_credenciales_clave_activa.';
    END IF;

    IF (SELECT count(*) FROM information_schema.columns
         WHERE table_name = 'credenciales_facturacion'
           AND column_name IN ('clave_vaciada_en', 'revocada_comprobada_en',
                               'revocada_comprobada_por', 'revocada_comprobacion',
                               'emisor_estado', 'emisor_estado_en',
                               'creada_por_alta', 'alta_confirmada_en')) <> 8 THEN
        RAISE EXCEPTION 'Faltan columnas nuevas en credenciales_facturacion.';
    END IF;
END $$;


INSERT INTO migraciones_aplicadas (nombre) VALUES ('015_alta_con_emisor');

COMMIT;