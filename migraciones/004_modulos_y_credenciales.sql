-- =============================================================================
-- 004 — MÓDULOS CONTRATADOS Y CREDENCIALES DEL SERVICIO DE FACTURACIÓN
--
-- Dos cosas que parecen de configuración y son de seguridad:
--
--   · qué módulos puede usar cada empresa
--   · con qué llave habla la plataforma con el servicio de facturación
-- =============================================================================


-- =============================================================================
-- CATÁLOGO DE MÓDULOS
--
-- Hoy solo existe `facturacion`. La tabla se crea igual, porque el día que
-- entre el segundo módulo no se quiere estar migrando con clientes dentro.
-- =============================================================================

CREATE TABLE modulos (
    codigo      text    PRIMARY KEY,
    nombre      text    NOT NULL,
    descripcion text,
    ruta        text    NOT NULL,
    icono       text,
    orden       integer NOT NULL DEFAULT 0,
    disponible  boolean NOT NULL DEFAULT true,

    CONSTRAINT ck_modulos_codigo CHECK (codigo ~ '^[a-z_]+$')
);


-- =============================================================================
-- MÓDULOS CONTRATADOS POR CADA EMPRESA
--
-- Esta es la pieza que sostiene la promesa del documento maestro: «todo lo
-- que varía por empresa vive en configuración, nunca en condicionales de
-- código».
--
-- La columna `config` guarda los ajustes propios de esa empresa para ese
-- módulo. El día que un cliente pida algo distinto, la respuesta va aquí. El
-- día que la respuesta sea un `if (empresa == "X")` en el código, empieza la
-- cuenta regresiva hacia cincuenta sistemas disfrazados de uno.
-- =============================================================================

-- Con `id` propio por el mismo motivo que rol_permiso y usuario_rol: sin él,
-- la bitácora no sabría qué fila registrar al activar o quitar un módulo.

CREATE TABLE empresa_modulo (
    id             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id     uuid        NOT NULL REFERENCES empresas (id),
    modulo_codigo  text        NOT NULL REFERENCES modulos (codigo),

    config         jsonb       NOT NULL DEFAULT '{}'::jsonb,

    contratado_en  timestamptz,
    vence_en       timestamptz,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1
);

CREATE UNIQUE INDEX ux_empresa_modulo ON empresa_modulo (empresa_id, modulo_codigo);

COMMENT ON TABLE empresa_modulo IS
    'Activar y desactivar módulos es cosa del super administrador. Un admin de empresa NO puede darse módulos que no contrató.';


-- --- El menú que ve cada usuario ---------------------------------------------
--
-- Cruce de tres filtros: lo que la empresa contrató, los roles del usuario y
-- los permisos de esos roles.
--
-- Y el recordatorio que va en todas las pantallas: OCULTAR EL MENÚ NO ES
-- SEGURIDAD. Esta vista es comodidad. La validación real va en cada endpoint,
-- que comprueba el permiso otra vez. Si alguien escribe la URL a mano, recibe
-- un 403 aunque el menú nunca se lo haya mostrado.

-- Con security_invoker, por el mismo motivo que permisos_efectivos: sin él,
-- la vista se saltaría el aislamiento de las tablas de debajo.

CREATE OR REPLACE VIEW menu_usuario WITH (security_invoker = true) AS
SELECT DISTINCT
       pe.usuario_id,
       m.codigo,
       m.nombre,
       m.ruta,
       m.icono,
       m.orden
  FROM permisos_efectivos pe
  JOIN empresa_modulo em ON em.empresa_id = pe.empresa_id AND em.activo
  JOIN modulos        m  ON m.codigo = em.modulo_codigo   AND m.disponible
  JOIN permisos       p  ON p.codigo = pe.permiso_codigo
 WHERE p.area = m.codigo
   AND (em.vence_en IS NULL OR em.vence_en > now());


-- =============================================================================
-- CREDENCIALES DEL SERVICIO DE FACTURACIÓN
--
-- Una clave de API por empresa, porque en el servicio una clave equivale a un
-- RUC. Si la plataforma usara una sola clave para todos sus clientes, todas
-- las facturas saldrían a nombre de la misma empresa.
--
-- SE GUARDA CIFRADA, NO HASHEADA, y es la única excepción a la regla de la
-- migración 003. Motivo: hay que poder recuperarla para ponerla en la
-- cabecera de cada llamada. De un hash no se vuelve atrás.
--
-- El cifrado es AES-GCM con la llave maestra de la plataforma, que vive en
-- una variable de entorno y NUNCA en la base. Guardar la llave junto a lo que
-- protege es cerrar la puerta y dejar la llave en la cerradura.
--
-- CONSECUENCIA QUE HAY QUE TENER PRESENTE: la llave maestra es parte del
-- respaldo, y va guardada aparte. Sin ella, un respaldo restaurado trae las
-- credenciales convertidas en ruido.
-- =============================================================================

CREATE TABLE credenciales_facturacion (
    id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id        uuid        NOT NULL REFERENCES empresas (id),

    -- Dónde vive el servicio. Cambia entre desarrollo y producción, y podría
    -- cambiar por empresa el día que un cliente grande tenga su propia
    -- instancia.
    base_url          text        NOT NULL,

    -- UN SOLO CAMPO, no clave + nonce por separado.
    --
    -- Dentro van pegados el nonce (12 bytes), la etiqueta de autenticación
    -- (16) y el texto cifrado. Guardarlos juntos evita el fallo clásico de
    -- recuperar la clave con el nonce de otra fila, y deja una sola regla en
    -- todo el sistema: «lo cifrado es un blob, se descifra entero o no se
    -- descifra».
    clave_cifrada     bytea       NOT NULL,

    -- Los últimos cuatro caracteres, en claro, solo para que el administrador
    -- reconozca cuál es sin poder usarla. Es lo que hacen los bancos con las
    -- tarjetas y sirve para lo mismo.
    pista             text,

    rotada_en         timestamptz,
    ultima_llamada_en timestamptz,
    ultimo_fallo_en   timestamptz,
    ultimo_fallo      text,

    creado_en         timestamptz NOT NULL DEFAULT now(),
    creado_por        uuid,
    modificado_en     timestamptz,
    modificado_por    uuid,
    activo            boolean     NOT NULL DEFAULT true,
    version           integer     NOT NULL DEFAULT 1
);

-- Una credencial activa por empresa. Las anteriores quedan desactivadas, no
-- borradas: si mañana hay que explicar con qué llave se emitió un comprobante
-- de hace un año, la respuesta tiene que existir.
CREATE UNIQUE INDEX ux_credenciales_empresa
    ON credenciales_facturacion (empresa_id) WHERE activo;


-- =============================================================================
-- AISLAMIENTO Y VIGILANCIA
-- =============================================================================

ALTER TABLE empresa_modulo           ENABLE ROW LEVEL SECURITY;
ALTER TABLE credenciales_facturacion ENABLE ROW LEVEL SECURITY;

ALTER TABLE empresa_modulo           FORCE ROW LEVEL SECURITY;
ALTER TABLE credenciales_facturacion FORCE ROW LEVEL SECURITY;

-- La empresa PUEDE ver qué módulos tiene. NO puede activarse ninguno: el
-- WITH CHECK solo pasa para el super administrador. Es la diferencia entre
-- leer tu contrato y firmarte uno nuevo.
CREATE POLICY p_empresa_modulo ON empresa_modulo
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super());

CREATE POLICY p_credenciales ON credenciales_facturacion
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());

-- Mismo caso que `permisos`: catálogo sin campos de auditoría, pero con
-- bitácora.
CREATE TRIGGER auditar_modulos
    AFTER INSERT OR UPDATE OR DELETE ON modulos
    FOR EACH ROW EXECUTE FUNCTION fn_auditar();

SELECT vigilar_tabla('empresa_modulo');
SELECT vigilar_tabla('credenciales_facturacion', ARRAY['clave_cifrada']);

GRANT SELECT ON modulos, menu_usuario TO plataforma_app;
GRANT SELECT, INSERT, UPDATE ON empresa_modulo, credenciales_facturacion
    TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('004_modulos_y_credenciales');
