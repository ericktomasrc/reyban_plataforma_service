-- =============================================================================
-- 006 — SEMILLAS
--
-- El catálogo de permisos, el módulo de facturación y los tres roles de
-- sistema.
--
-- POR QUÉ LOS PERMISOS SON UNA MIGRACIÓN Y NO UNA PANTALLA:
--
-- Un permiso existe porque hay una línea de código que lo comprueba. Crear
-- permisos desde la interfaz produciría permisos que no protegen nada —y, lo
-- que es peor, la sensación de que sí—. Los roles, en cambio, sí se crean
-- desde la interfaz: son combinaciones de permisos que ya existen.
-- =============================================================================


-- =============================================================================
-- PERMISOS
--
-- El área tiene que coincidir con el código del módulo para que la vista
-- `menu_usuario` sepa qué entrada mostrar. Las áreas `plataforma` y `usuarios`
-- no son módulos: son transversales y no aparecen en ese menú.
-- =============================================================================

INSERT INTO permisos (codigo, area, descripcion) VALUES

-- --- Plataforma: solo para el super administrador ---------------------------
('plataforma.empresas_gestionar', 'plataforma', 'Crear, suspender y reactivar empresas'),
('plataforma.modulos_gestionar',  'plataforma', 'Activar y desactivar módulos por empresa'),
('plataforma.bitacora_ver',       'plataforma', 'Ver la bitácora y los eventos de seguridad de todas las empresas'),
('plataforma.suplantar',          'plataforma', 'Entrar como otro usuario para dar soporte'),

-- --- Usuarios: dentro de la propia empresa ----------------------------------
('usuarios.ver',              'usuarios', 'Ver la lista de usuarios de la empresa'),
('usuarios.crear',            'usuarios', 'Invitar y crear usuarios'),
('usuarios.editar',           'usuarios', 'Editar datos de un usuario'),
('usuarios.desactivar',       'usuarios', 'Desactivar un usuario'),
('usuarios.roles_gestionar',  'usuarios', 'Crear roles y asignar permisos'),
('usuarios.sesiones_revocar', 'usuarios', 'Cerrar sesiones abiertas de otros usuarios'),
('usuarios.bitacora_ver',     'usuarios', 'Ver la bitácora de la propia empresa'),

-- --- Facturación ------------------------------------------------------------
('facturacion.ver',                    'facturacion', 'Ver comprobantes emitidos'),
('facturacion.emitir',                 'facturacion', 'Emitir comprobantes'),
('facturacion.anular',                 'facturacion', 'Dar de baja y anular comprobantes'),
('facturacion.descargar',              'facturacion', 'Descargar XML, CDR y PDF'),
('facturacion.clientes_gestionar',     'facturacion', 'Crear y editar clientes'),
('facturacion.productos_gestionar',    'facturacion', 'Crear y editar productos'),
('facturacion.credenciales_gestionar', 'facturacion', 'Configurar la conexión con el servicio de facturación');


-- =============================================================================
-- MÓDULOS
-- =============================================================================

INSERT INTO modulos (codigo, nombre, descripcion, ruta, icono, orden) VALUES
('facturacion', 'Facturación electrónica',
 'Emisión de comprobantes de pago electrónicos ante SUNAT',
 '/facturacion', 'recibo', 10);


-- =============================================================================
-- ROLES DE SISTEMA
--
-- `empresa_id` nulo = disponibles para todas las empresas. Una empresa puede
-- crear los suyos propios además de estos.
-- =============================================================================

-- El contexto va vacío: estas filas las crea el sistema, no una persona, y la
-- bitácora lo va a registrar así (usuario_id nulo). Es correcto y es lo que
-- se quiere ver cuando alguien pregunte de dónde salió este rol.

INSERT INTO roles (id, empresa_id, nombre, descripcion, editable) VALUES
('11111111-1111-1111-1111-111111111111', NULL, 'Administrador',
 'Control total dentro de su empresa. No se puede editar ni borrar.', false),

('22222222-2222-2222-2222-222222222222', NULL, 'Facturador',
 'Emite comprobantes y gestiona clientes y productos.', true),

('33333333-3333-3333-3333-333333333333', NULL, 'Consulta',
 'Solo lectura: ver comprobantes y descargar sus archivos.', true);


-- --- Administrador: todo lo de empresa, nada de plataforma -------------------
--
-- Fíjate en el WHERE: el rol de administrador de una empresa NO recibe los
-- permisos del área `plataforma`. Un administrador de cliente no puede
-- activarse módulos que no contrató ni ver datos de otra empresa.
--
-- Si se mezclaran ambos niveles en un solo rol "administrador", tarde o
-- temprano un cliente se activa un módulo que no paga.

INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT '11111111-1111-1111-1111-111111111111', codigo
  FROM permisos
 WHERE area <> 'plataforma';


-- --- Facturador --------------------------------------------------------------
--
-- Sin `facturacion.anular` y sin `credenciales_gestionar`: anular un
-- comprobante tiene consecuencias tributarias, y las credenciales son la
-- llave con la que se emite en nombre de la empresa. Quien factura todos los
-- días no necesita ninguna de las dos.

INSERT INTO rol_permiso (rol_id, permiso_codigo) VALUES
('22222222-2222-2222-2222-222222222222', 'facturacion.ver'),
('22222222-2222-2222-2222-222222222222', 'facturacion.emitir'),
('22222222-2222-2222-2222-222222222222', 'facturacion.descargar'),
('22222222-2222-2222-2222-222222222222', 'facturacion.clientes_gestionar'),
('22222222-2222-2222-2222-222222222222', 'facturacion.productos_gestionar');


-- --- Consulta ----------------------------------------------------------------

INSERT INTO rol_permiso (rol_id, permiso_codigo) VALUES
('33333333-3333-3333-3333-333333333333', 'facturacion.ver'),
('33333333-3333-3333-3333-333333333333', 'facturacion.descargar');


-- =============================================================================
-- EL PRIMER SUPER ADMINISTRADOR
--
-- NO se crea aquí, y es a propósito: una contraseña escrita en un archivo de
-- migración acaba en el repositorio, en el historial de git y en la máquina
-- de cualquiera que clone el proyecto.
--
-- Lo crea la aplicación al arrancar, si no existe ninguno, con una contraseña
-- que sale de una variable de entorno y con `clave_cambio_forzado = true`.
-- Es el mismo mecanismo que ya usa el servicio de facturación.
-- =============================================================================

INSERT INTO migraciones_aplicadas (nombre) VALUES ('006_semillas');
