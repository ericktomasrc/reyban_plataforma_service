-- =============================================================================
-- 002 — EMPRESAS, USUARIOS, ROLES Y PERMISOS
--
-- Aquí aparece el aislamiento entre empresas, que es el primero de los cuatro
-- problemas irreversibles del documento maestro: se resuelve ahora o no se
-- resuelve.
--
-- Dos barreras, no una:
--
--   1. `empresa_id` en toda tabla de negocio.
--   2. Row Level Security de PostgreSQL, que hace que las filas de otra
--      empresa NO EXISTAN para la consulta, aunque alguien escriba un
--      SELECT * sin WHERE.
--
-- La segunda es la que importa. La primera depende de que el código no se
-- olvide nunca; la segunda es el motor de la base de datos.
-- =============================================================================


-- =============================================================================
-- EMPRESAS  (los tenants)
-- =============================================================================

CREATE TABLE empresas (
    id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),

    ruc               text        NOT NULL,
    razon_social      text        NOT NULL,
    nombre_comercial  text,
    direccion         text,
    correo_contacto   text,
    telefono          text,

    plan              text        NOT NULL DEFAULT 'basico',

    -- Suspender no es desactivar. `activo = false` significa "esta empresa ya
    -- no existe para nosotros"; suspendida significa "no paga, pero vuelve".
    suspendida_en     timestamptz,
    motivo_suspension text,

    creado_en         timestamptz NOT NULL DEFAULT now(),
    creado_por        uuid,
    modificado_en     timestamptz,
    modificado_por    uuid,
    activo            boolean     NOT NULL DEFAULT true,
    version           integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_empresas_ruc CHECK (ruc ~ '^[0-9]{11}$')
);

-- El RUC es único SIEMPRE, incluso entre empresas desactivadas. Si se
-- permitiera repetirlo al dar de baja una, dos filas distintas tendrían los
-- comprobantes del mismo contribuyente y nadie sabría cuál es cuál.
CREATE UNIQUE INDEX ux_empresas_ruc ON empresas (ruc);

COMMENT ON COLUMN empresas.plan IS
    'Comercial, no técnico. No condiciona el código: los módulos se activan en empresa_modulo.';


-- =============================================================================
-- USUARIOS
--
-- SOBRE `empresa_id` NULO: un usuario sin empresa es un super administrador,
-- alguien tuyo. Vive fuera de los tenants porque su trabajo es justamente
-- cruzarlos.
--
-- SOBRE EL CORREO: es único en toda la plataforma, no por empresa. La
-- consecuencia es que una persona que trabaje para dos empresas cliente
-- necesita dos cuentas. Se elige así porque la alternativa —el mismo correo
-- en dos empresas— obliga a preguntar "¿a cuál entras?" en cada ingreso, y
-- convierte la pantalla más usada del sistema en la más confusa.
-- =============================================================================

CREATE TABLE usuarios (
    id                   uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id           uuid        REFERENCES empresas (id),

    correo               text        NOT NULL,
    nombre               text        NOT NULL,

    -- PBKDF2-SHA256. El formato guarda el algoritmo y las iteraciones junto
    -- al hash, para poder subir las iteraciones con los años sin dejar fuera
    -- a los usuarios existentes: al ingresar se les recifra la contraseña.
    clave_hash           text        NOT NULL,
    clave_cambiada_en    timestamptz,
    clave_cambio_forzado boolean     NOT NULL DEFAULT false,

    -- Cifrado con la llave maestra de la plataforma, NUNCA en claro.
    -- Con este secreto legible, cualquiera con acceso a la base genera
    -- códigos válidos y el segundo factor deja de ser un segundo factor.
    secreto_2fa          bytea,
    dosfa_activo         boolean     NOT NULL DEFAULT false,
    dosfa_activado_en    timestamptz,

    intentos_fallidos    integer     NOT NULL DEFAULT 0,
    bloqueado_hasta      timestamptz,
    ultimo_ingreso_en    timestamptz,
    ultimo_ingreso_ip    inet,

    es_super_admin       boolean     NOT NULL DEFAULT false,

    creado_en            timestamptz NOT NULL DEFAULT now(),
    creado_por           uuid,
    modificado_en        timestamptz,
    modificado_por       uuid,
    activo               boolean     NOT NULL DEFAULT true,
    version              integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_usuarios_correo CHECK (correo ~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$'),

    -- Un super admin no pertenece a ninguna empresa, y un usuario de empresa
    -- no puede ser super admin. Mezclarlos es como se acaba dando acceso
    -- global a alguien sin querer.
    CONSTRAINT ck_usuarios_ambito CHECK (
        (es_super_admin AND empresa_id IS NULL) OR
        (NOT es_super_admin AND empresa_id IS NOT NULL)
    )
);

CREATE UNIQUE INDEX ux_usuarios_correo ON usuarios (lower(correo));
CREATE INDEX ix_usuarios_empresa ON usuarios (empresa_id) WHERE activo;


-- =============================================================================
-- PERMISOS Y ROLES
--
-- LA REGLA: el código pregunta por PERMISOS, nunca por roles.
--
-- Un endpoint dice «hace falta facturacion.emitir» y le da igual qué rol lo
-- tenga. Así, cuando mañana se cree un rol nuevo que también deba emitir,
-- ningún código se entera.
--
-- Si los endpoints preguntaran por roles, cada rol nuevo sería un despliegue.
-- =============================================================================

-- Catálogo global. No lleva empresa_id: los permisos que existen son los
-- mismos para todos, lo que cambia es quién los tiene.
CREATE TABLE permisos (
    codigo      text PRIMARY KEY,
    area        text NOT NULL,
    descripcion text NOT NULL,

    CONSTRAINT ck_permisos_codigo CHECK (codigo ~ '^[a-z_]+\.[a-z_]+$')
);

COMMENT ON TABLE permisos IS
    'Catálogo fijo, se llena en la migración de semillas. Añadir un permiso es una migración, no una pantalla.';


CREATE TABLE roles (
    id             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),

    -- NULL = rol de sistema, disponible para todas las empresas.
    empresa_id     uuid        REFERENCES empresas (id),

    nombre         text        NOT NULL,
    descripcion    text,

    -- El rol de administrador no se puede editar ni borrar. Si se le pudieran
    -- quitar permisos, alguien podría dejar a su empresa sin nadie capaz de
    -- administrarla, sin forma de recuperarlo desde dentro.
    editable       boolean     NOT NULL DEFAULT true,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1
);

CREATE UNIQUE INDEX ux_roles_nombre_empresa
    ON roles (COALESCE(empresa_id, '00000000-0000-0000-0000-000000000000'::uuid), lower(nombre))
    WHERE activo;


-- LAS TABLAS DE UNIÓN LLEVAN `id` PROPIO, y no es decoración.
--
-- Lo natural sería que su clave primaria fuera la pareja (rol, permiso). Pero
-- entonces la bitácora no sabría qué anotar en `fila_id` —guarda el `id` de
-- la fila— y cada cambio de permisos quedaría registrado como «(sin id)».
-- Justo en la tabla que decide quién puede hacer qué.
--
-- Con `id` propio, la bitácora identifica la fila y la pareja sigue siendo
-- única gracias al índice de más abajo. Cuesta 16 bytes por fila.

CREATE TABLE rol_permiso (
    id             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    rol_id         uuid        NOT NULL REFERENCES roles (id) ON DELETE CASCADE,
    permiso_codigo text        NOT NULL REFERENCES permisos (codigo),

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1
);

CREATE UNIQUE INDEX ux_rol_permiso ON rol_permiso (rol_id, permiso_codigo);


CREATE TABLE usuario_rol (
    id             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    usuario_id     uuid        NOT NULL REFERENCES usuarios (id) ON DELETE CASCADE,
    rol_id         uuid        NOT NULL REFERENCES roles (id),

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1
);

CREATE UNIQUE INDEX ux_usuario_rol ON usuario_rol (usuario_id, rol_id);


-- --- Los permisos efectivos de cada usuario ----------------------------------
--
-- Una vista, para que el cálculo viva en un solo sitio. Si cada endpoint
-- reconstruyera esta consulta, tarde o temprano una versión olvidaría mirar
-- si el rol está activo.

-- `security_invoker` NO ES OPCIONAL AQUÍ.
--
-- Por defecto, una vista de PostgreSQL se ejecuta con los permisos de QUIEN
-- LA CREÓ —aquí, el propietario— y no con los de quien la consulta. Eso
-- significa que las políticas de aislamiento de las tablas de debajo NO se
-- aplican: cualquiera que consultara esta vista vería los permisos de todas
-- las empresas.
--
-- Con security_invoker, la vista corre con los permisos del que pregunta, y
-- el aislamiento vuelve a valer. Una vista es la forma más silenciosa de
-- agujerear un RLS bien puesto.

CREATE OR REPLACE VIEW permisos_efectivos WITH (security_invoker = true) AS
SELECT DISTINCT
       u.id         AS usuario_id,
       u.empresa_id,
       rp.permiso_codigo
  FROM usuarios u
  JOIN usuario_rol ur ON ur.usuario_id = u.id AND ur.activo
  JOIN roles       r  ON r.id  = ur.rol_id    AND r.activo
  JOIN rol_permiso rp ON rp.rol_id = r.id     AND rp.activo
 WHERE u.activo;


-- =============================================================================
-- AISLAMIENTO ENTRE EMPRESAS  (Row Level Security)
--
-- A partir de aquí, una consulta hecha por el rol de aplicación solo ve las
-- filas de la empresa que el middleware fijó en el contexto. El super
-- administrador es la única excepción, y está escrita a la vista en cada
-- política.
--
-- NOTA SOBRE `FORCE`: sin FORCE, el propietario de la tabla se salta las
-- políticas. Se activa FORCE para que ni siquiera el dueño pueda leer de otra
-- empresa sin querer desde una sesión de mantenimiento.
-- =============================================================================

ALTER TABLE empresas    ENABLE ROW LEVEL SECURITY;
ALTER TABLE usuarios    ENABLE ROW LEVEL SECURITY;
ALTER TABLE roles       ENABLE ROW LEVEL SECURITY;
ALTER TABLE rol_permiso ENABLE ROW LEVEL SECURITY;
ALTER TABLE usuario_rol ENABLE ROW LEVEL SECURITY;

ALTER TABLE empresas    FORCE ROW LEVEL SECURITY;
ALTER TABLE usuarios    FORCE ROW LEVEL SECURITY;
ALTER TABLE roles       FORCE ROW LEVEL SECURITY;
ALTER TABLE rol_permiso FORCE ROW LEVEL SECURITY;
ALTER TABLE usuario_rol FORCE ROW LEVEL SECURITY;


CREATE POLICY p_empresas ON empresas
    USING (ctx_es_super() OR id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR id = ctx_empresa());

CREATE POLICY p_usuarios ON usuarios
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());

-- Los roles de sistema (empresa_id NULL) los ve todo el mundo; los propios,
-- solo su empresa. Pero crear o modificar un rol de sistema es cosa del
-- super administrador: por eso el WITH CHECK es más estricto que el USING.
CREATE POLICY p_roles ON roles
    USING (ctx_es_super() OR empresa_id IS NULL OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());

CREATE POLICY p_rol_permiso ON rol_permiso
    USING (ctx_es_super() OR EXISTS (
        SELECT 1 FROM roles r WHERE r.id = rol_permiso.rol_id
           AND (r.empresa_id IS NULL OR r.empresa_id = ctx_empresa())))
    WITH CHECK (ctx_es_super() OR EXISTS (
        SELECT 1 FROM roles r WHERE r.id = rol_permiso.rol_id
           AND r.empresa_id = ctx_empresa()));

CREATE POLICY p_usuario_rol ON usuario_rol
    USING (ctx_es_super() OR EXISTS (
        SELECT 1 FROM usuarios u WHERE u.id = usuario_rol.usuario_id
           AND u.empresa_id = ctx_empresa()))
    WITH CHECK (ctx_es_super() OR EXISTS (
        SELECT 1 FROM usuarios u WHERE u.id = usuario_rol.usuario_id
           AND u.empresa_id = ctx_empresa()));


-- =============================================================================
-- VIGILANCIA
--
-- Fíjate en lo que se oculta: el hash de la contraseña y el secreto del
-- segundo factor. Sin esa lista, cada cambio de contraseña dejaría una copia
-- del hash anterior en la bitácora, para siempre.
-- =============================================================================

-- El catálogo de permisos no lleva campos de auditoría —lo cambian las
-- migraciones, no las personas— pero sí lleva bitácora. Añadir o quitar un
-- permiso cambia quién puede hacer qué en todo el sistema, y eso tiene que
-- quedar escrito aunque lo haya hecho un despliegue a las tres de la mañana.
CREATE TRIGGER auditar_permisos
    AFTER INSERT OR UPDATE OR DELETE ON permisos
    FOR EACH ROW EXECUTE FUNCTION fn_auditar();

SELECT vigilar_tabla('empresas');
SELECT vigilar_tabla('usuarios', ARRAY['clave_hash', 'secreto_2fa']);
SELECT vigilar_tabla('roles');
SELECT vigilar_tabla('rol_permiso');
SELECT vigilar_tabla('usuario_rol');


-- =============================================================================
-- PERMISOS DEL ROL DE APLICACIÓN
--
-- Ningún DELETE en ninguna tabla. Se desactiva con `activo = false`, nunca se
-- borra: un cliente con documentos emitidos que desaparece de la tabla deja
-- esos documentos apuntando al vacío, y eso es exactamente lo que una
-- fiscalización va a pedir.
-- =============================================================================

GRANT SELECT, INSERT, UPDATE ON empresas, usuarios, roles, rol_permiso, usuario_rol
    TO plataforma_app;
GRANT SELECT ON permisos, permisos_efectivos TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('002_empresas_y_usuarios');
