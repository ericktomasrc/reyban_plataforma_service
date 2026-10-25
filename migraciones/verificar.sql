-- =============================================================================
-- VERIFICAR
--
-- Estas comprobaciones NO son opcionales. Cada una corresponde a una promesa
-- que hacen las migraciones, y una promesa que nadie comprueba es una
-- suposición.
--
-- Se corre CONECTADO COMO `plataforma_app`, no como el propietario ni como
-- superusuario: un superusuario se salta el RLS siempre, así que una prueba
-- de aislamiento hecha como postgres pasa aunque el aislamiento no exista.
--
--     psql -U plataforma_app -d plataforma -f verificar.sql
--
-- Si alguna comprobación falla, el script se detiene con un error.
-- =============================================================================

\set ON_ERROR_STOP on

DO $$
DECLARE
    v_a        uuid;
    v_b        uuid;
    v_c        uuid;
    v_usr_a    uuid;
    v_usr_b    uuid;
    v_cli_a    uuid;
    v_doc      uuid;
    v_n        integer;
    v_txt      text;
    v_ok       boolean;
    v_reg      uuid;
    v_res      text;
    v_cta      uuid;
BEGIN

-- =========================================================================
RAISE NOTICE '1. Ninguna tabla se quedó sin vigilar';
-- =========================================================================
SELECT count(*) INTO v_n FROM tablas_sin_vigilar;
IF v_n > 0 THEN
    RAISE EXCEPTION 'Hay % tabla(s) sin disparador de bitácora. Mira la vista tablas_sin_vigilar.', v_n;
END IF;


-- =========================================================================
RAISE NOTICE '2. Se crean dos empresas y un usuario en cada una';
-- =========================================================================
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

-- Con `nombre` desde la 014: es obligatorio, y el RUC y la razón social
-- dejaron de serlo.
INSERT INTO empresas (nombre, ruc, razon_social) VALUES ('Empresa A', '20100000001', 'EMPRESA A')
    RETURNING id INTO v_a;
INSERT INTO empresas (nombre, ruc, razon_social) VALUES ('Empresa B', '20100000002', 'EMPRESA B')
    RETURNING id INTO v_b;

INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash)
VALUES (v_a, 'ana@empresa-a.pe', 'Ana', 'pbkdf2$210000$xxx')
    RETURNING id INTO v_usr_a;

INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash)
VALUES (v_b, 'beto@empresa-b.pe', 'Beto', 'pbkdf2$210000$yyy');


-- =========================================================================
RAISE NOTICE '3. AISLAMIENTO: la empresa A no ve nada de la B';
-- =========================================================================
PERFORM fijar_contexto(v_usr_a, v_a, '127.0.0.1', 'verificar.sql', false);

SELECT count(*) INTO v_n FROM empresas;
IF v_n <> 1 THEN
    RAISE EXCEPTION 'Con el contexto en la empresa A se ven % empresas. Debería ver 1.', v_n;
END IF;

SELECT count(*) INTO v_n FROM usuarios;
IF v_n <> 1 THEN
    RAISE EXCEPTION 'Con el contexto en la empresa A se ven % usuarios. Debería ver 1.', v_n;
END IF;

-- Y tampoco puede escribir en la otra: el WITH CHECK lo impide.
BEGIN
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash)
    VALUES (v_b, 'colado@empresa-b.pe', 'Colado', 'x');
    RAISE EXCEPTION 'FALLO GRAVE: la empresa A pudo crear un usuario en la empresa B.';
EXCEPTION WHEN insufficient_privilege THEN
    NULL;  -- correcto
END;


-- =========================================================================
RAISE NOTICE '4. Los campos de auditoría los pone la base, no la aplicación';
-- =========================================================================
INSERT INTO clientes (empresa_id, tipo_documento, numero_documento, razon_social,
                      creado_por, creado_en, version)
VALUES (v_a, '6', '20999999999', 'CLIENTE DE PRUEBA',
        '00000000-0000-0000-0000-000000000000'::uuid,  -- mentira deliberada
        '1999-01-01'::timestamptz,                      -- mentira deliberada
        999)                                            -- mentira deliberada
    RETURNING id INTO v_cli_a;

SELECT creado_por = v_usr_a AND version = 1 AND creado_en > now() - interval '1 minute'
  INTO v_ok FROM clientes WHERE id = v_cli_a;

IF NOT v_ok THEN
    RAISE EXCEPTION 'La aplicación consiguió mentir sobre quién creó la fila.';
END IF;


-- =========================================================================
RAISE NOTICE '5. La bitácora registró el alta, con usuario e IP';
-- =========================================================================
SELECT count(*) INTO v_n
  FROM bitacora
 WHERE tabla = 'clientes' AND fila_id = v_cli_a::text
   AND operacion = 'I' AND usuario_id = v_usr_a AND ip = '127.0.0.1'::inet;

IF v_n <> 1 THEN
    RAISE EXCEPTION 'El alta del cliente no quedó registrada en la bitácora.';
END IF;


-- =========================================================================
RAISE NOTICE '6. Un UPDATE sin cambios reales no ensucia la bitácora';
-- =========================================================================
SELECT count(*) INTO v_n FROM bitacora WHERE tabla = 'clientes';
UPDATE clientes SET razon_social = 'CLIENTE DE PRUEBA' WHERE id = v_cli_a;

SELECT count(*) - v_n INTO v_n FROM bitacora WHERE tabla = 'clientes';
IF v_n <> 0 THEN
    RAISE EXCEPTION 'Un UPDATE que no cambió nada dejó % fila(s) en la bitácora.', v_n;
END IF;


-- =========================================================================
RAISE NOTICE '7. Un UPDATE de verdad sí queda, con la lista de campos';
-- =========================================================================
UPDATE clientes SET razon_social = 'CLIENTE CORREGIDO' WHERE id = v_cli_a;

SELECT campos_cambiados INTO v_txt
  FROM bitacora
 WHERE tabla = 'clientes' AND fila_id = v_cli_a::text AND operacion = 'U'
 ORDER BY id DESC LIMIT 1;

IF v_txt IS NULL OR v_txt NOT LIKE '%razon_social%' THEN
    RAISE EXCEPTION 'El cambio de razón social no quedó en campos_cambiados (salió: %).', v_txt;
END IF;

-- Y la versión subió sola.
SELECT version INTO v_n FROM clientes WHERE id = v_cli_a;
IF v_n <> 2 THEN
    RAISE EXCEPTION 'La columna version no se incrementó (vale %).', v_n;
END IF;


-- =========================================================================
RAISE NOTICE '8. LO SENSIBLE NO LLEGA A LA BITÁCORA';
-- =========================================================================
PERFORM fijar_contexto(v_usr_a, v_a, '127.0.0.1', 'verificar.sql', false);
UPDATE usuarios SET clave_hash = 'pbkdf2$210000$SECRETO_NUEVO' WHERE id = v_usr_a;

SELECT despues ->> 'clave_hash' INTO v_txt
  FROM bitacora
 WHERE tabla = 'usuarios' AND fila_id = v_usr_a::text AND operacion = 'U'
 ORDER BY id DESC LIMIT 1;

IF v_txt IS NULL THEN
    RAISE EXCEPTION 'El cambio de contraseña NO quedó registrado. Un cambio invisible es peor que uno mal guardado.';
END IF;

IF v_txt IS DISTINCT FROM '[oculto]' THEN
    RAISE EXCEPTION 'FALLO GRAVE: el hash de la contraseña quedó copiado en la bitácora (%).', v_txt;
END IF;

-- Y tiene que decir QUÉ cambió, aunque no diga a qué valor.
SELECT array_to_string(campos_cambiados, ',') INTO v_txt
  FROM bitacora
 WHERE tabla = 'usuarios' AND fila_id = v_usr_a::text AND operacion = 'U'
 ORDER BY id DESC LIMIT 1;

IF v_txt NOT LIKE '%clave_hash%' THEN
    RAISE EXCEPTION 'La bitácora no dice que cambió clave_hash (dice: %).', v_txt;
END IF;

SELECT count(*) INTO v_n
  FROM bitacora
 WHERE (antes::text LIKE '%SECRETO_NUEVO%' OR despues::text LIKE '%SECRETO_NUEVO%');
IF v_n > 0 THEN
    RAISE EXCEPTION 'FALLO GRAVE: el secreto aparece en % fila(s) de la bitácora.', v_n;
END IF;


-- =========================================================================
RAISE NOTICE '9. La bitácora no se puede reescribir';
-- =========================================================================
BEGIN
    UPDATE bitacora SET usuario_id = NULL WHERE tabla = 'clientes';
    RAISE EXCEPTION 'FALLO GRAVE: se pudo modificar la bitácora.';
EXCEPTION
    WHEN insufficient_privilege THEN NULL;  -- el rol no tiene el permiso
    WHEN raise_exception THEN
        IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;  -- correcto: saltó el disparador
END;

BEGIN
    DELETE FROM bitacora;
    RAISE EXCEPTION 'FALLO GRAVE: se pudo borrar la bitácora.';
EXCEPTION
    WHEN insufficient_privilege THEN NULL;
    WHEN raise_exception THEN
        IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
END;


-- =========================================================================
RAISE NOTICE '10. Nada se puede borrar: el rol no tiene DELETE';
-- =========================================================================
BEGIN
    DELETE FROM clientes WHERE id = v_cli_a;
    RAISE EXCEPTION 'FALLO GRAVE: se pudo borrar un cliente.';
EXCEPTION
    WHEN insufficient_privilege THEN NULL;
    WHEN raise_exception THEN
        IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
END;


-- =========================================================================
RAISE NOTICE '11. El historial del documento se escribe solo';
-- =========================================================================
INSERT INTO documentos (empresa_id, tipo, cliente_id, fecha_emision, moneda)
VALUES (v_a, '01', v_cli_a, current_date, 'PEN')
    RETURNING id INTO v_doc;

SELECT count(*) INTO v_n FROM documento_historial
 WHERE documento_id = v_doc AND estado_nuevo = 'borrador';
IF v_n <> 1 THEN
    RAISE EXCEPTION 'El alta del documento no generó su fila de historial.';
END IF;

UPDATE documentos SET estado = 'encolado', serie = 'F001', correlativo = 1 WHERE id = v_doc;
UPDATE documentos SET estado = 'aceptado', codigo_sunat = '0' WHERE id = v_doc;

SELECT count(*) INTO v_n FROM documento_historial WHERE documento_id = v_doc;
IF v_n <> 3 THEN
    RAISE EXCEPTION 'El historial del documento tiene % filas, deberían ser 3.', v_n;
END IF;

-- Un cambio que no toca el estado no ensucia el historial.
UPDATE documentos SET importe_total = 118.00 WHERE id = v_doc;
SELECT count(*) INTO v_n FROM documento_historial WHERE documento_id = v_doc;
IF v_n <> 3 THEN
    RAISE EXCEPTION 'Un cambio ajeno al estado añadió una fila al historial.';
END IF;


-- =========================================================================
RAISE NOTICE '12. El historial es de solo inserción';
-- =========================================================================
BEGIN
    UPDATE documento_historial SET estado_nuevo = 'aceptado' WHERE documento_id = v_doc;
    RAISE EXCEPTION 'FALLO GRAVE: se pudo reescribir el historial del documento.';
EXCEPTION
    WHEN insufficient_privilege THEN NULL;
    WHEN raise_exception THEN
        IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
END;


-- =========================================================================
RAISE NOTICE '13. No se puede repetir la numeración';
-- =========================================================================
BEGIN
    INSERT INTO documentos (empresa_id, tipo, serie, correlativo, cliente_id,
                            fecha_emision, moneda)
    VALUES (v_a, '01', 'F001', 1, v_cli_a, current_date, 'PEN');
    RAISE EXCEPTION 'FALLO GRAVE: se aceptó un correlativo duplicado.';
EXCEPTION
    WHEN unique_violation THEN NULL;
    WHEN raise_exception THEN
        IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
END;


-- =========================================================================
RAISE NOTICE '14. Un usuario de empresa no puede activarse módulos';
-- =========================================================================
BEGIN
    INSERT INTO empresa_modulo (empresa_id, modulo_codigo) VALUES (v_a, 'facturacion');
    RAISE EXCEPTION 'FALLO GRAVE: la empresa se activó un módulo por su cuenta.';
EXCEPTION
    WHEN insufficient_privilege THEN NULL;
    WHEN raise_exception THEN
        IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
END;

-- El super administrador sí puede.
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
INSERT INTO empresa_modulo (empresa_id, modulo_codigo) VALUES (v_a, 'facturacion');


-- =========================================================================
RAISE NOTICE '15. El menú sale del cruce de módulo, rol y permiso';
-- =========================================================================
INSERT INTO usuario_rol (usuario_id, rol_id)
VALUES (v_usr_a, '22222222-2222-2222-2222-222222222222');   -- Facturador

PERFORM fijar_contexto(v_usr_a, v_a, '127.0.0.1', 'verificar.sql', false);

SELECT count(*) INTO v_n FROM menu_usuario WHERE usuario_id = v_usr_a;
IF v_n <> 1 THEN
    RAISE EXCEPTION 'El menú del facturador trae % entradas, debería traer 1.', v_n;
END IF;

-- Y el facturador NO tiene permiso de anular.
SELECT count(*) INTO v_n FROM permisos_efectivos
 WHERE usuario_id = v_usr_a AND permiso_codigo = 'facturacion.anular';
IF v_n <> 0 THEN
    RAISE EXCEPTION 'El rol Facturador trae el permiso de anular, y no debería.';
END IF;


-- =========================================================================
RAISE NOTICE '16. LAS VISTAS TAMPOCO SE SALTAN EL AISLAMIENTO';
-- =========================================================================
--
-- Una vista de PostgreSQL corre por defecto con los permisos de quien la
-- creó, no de quien la consulta. Sin `security_invoker`, estas dos vistas
-- enseñarían los permisos y el menú de TODAS las empresas a cualquiera.
--
-- Es la forma más silenciosa de agujerear un RLS bien puesto: las tablas
-- siguen protegidas y la vista las publica.

-- Se le da un rol a Beto, que es de la empresa B.
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

SELECT id INTO v_usr_b FROM usuarios WHERE correo = 'beto@empresa-b.pe';
INSERT INTO usuario_rol (usuario_id, rol_id)
VALUES (v_usr_b, '33333333-3333-3333-3333-333333333333');

-- Y desde la empresa A se mira si se le ve.
PERFORM fijar_contexto(v_usr_a, v_a, '127.0.0.1', 'verificar.sql', false);

SELECT count(*) INTO v_n FROM permisos_efectivos WHERE usuario_id = v_usr_b;
IF v_n <> 0 THEN
    RAISE EXCEPTION
        'FALLO GRAVE: la vista permisos_efectivos enseña los permisos de otra empresa. Falta security_invoker.';
END IF;

SELECT count(*) INTO v_n FROM menu_usuario WHERE usuario_id = v_usr_b;
IF v_n <> 0 THEN
    RAISE EXCEPTION
        'FALLO GRAVE: la vista menu_usuario enseña el menu de otra empresa. Falta security_invoker.';
END IF;


-- =========================================================================
RAISE NOTICE '17. Los eventos de seguridad se registran y no se tocan';
-- =========================================================================
PERFORM registrar_evento('ingreso_clave_incorrecta', false, NULL, NULL,
                         'ana@empresa-a.pe', '{"intento": 1}'::jsonb);

SELECT count(*) INTO v_n FROM eventos_seguridad WHERE NOT exito;
IF v_n <> 1 THEN
    RAISE EXCEPTION 'El intento fallido no quedó registrado.';
END IF;

BEGIN
    UPDATE eventos_seguridad SET exito = true;
    RAISE EXCEPTION 'FALLO GRAVE: se pudieron reescribir los eventos de seguridad.';
EXCEPTION
    WHEN insufficient_privilege THEN NULL;
    WHEN raise_exception THEN
        IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
END;


-- =========================================================================
RAISE NOTICE '18. El correo es unico en toda la plataforma, y se puede saber';
-- =========================================================================
--
-- La trampa: el correo es único GLOBALMENTE, pero el aislamiento solo deja
-- ver la propia empresa. Sin una puerta para esto, al crear un usuario la
-- comprobación diría que el correo está libre aunque lo tenga alguien de otra
-- empresa, y el INSERT se estrellaría contra el índice único.
--
-- Y no se arregla escribiendo la consulta en SQL crudo: el RLS actúa en el
-- motor, por debajo de cualquier cosa que haga la aplicación.

PERFORM fijar_contexto(v_usr_a, v_a, '127.0.0.1', 'verificar.sql', false);

-- Desde la empresa A, el usuario de la B no existe...
SELECT count(*) INTO v_n FROM usuarios WHERE lower(correo) = 'beto@empresa-b.pe';
IF v_n <> 0 THEN
    RAISE EXCEPTION 'La empresa A ve al usuario de la B en una consulta directa.';
END IF;

-- ...pero la función sí sabe que el correo está ocupado.
IF correo_disponible('beto@empresa-b.pe') THEN
    RAISE EXCEPTION
        'correo_disponible() dice que el correo de otra empresa esta libre. Le falta SECURITY DEFINER.';
END IF;

IF NOT correo_disponible('nadie@ninguna-parte.pe') THEN
    RAISE EXCEPTION 'correo_disponible() dice que un correo inexistente esta ocupado.';
END IF;


-- =========================================================================
RAISE NOTICE '19. Alta sin RUC; un RUC por empresa activa; el RUC no cambia (014)';
-- =========================================================================
--
-- El alta lleva solo un nombre. El RUC llega del servicio al conectar, una
-- sola vez, y no puede repetirse entre empresas activas: es lo que impide
-- pegar la clave de un cliente en la empresa de otro.

PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

INSERT INTO empresas (nombre) VALUES ('Empresa sin conectar')
    RETURNING id INTO v_c;

-- Conectar: el RUC pasa de NULL a un valor, con su razón social.
UPDATE empresas SET ruc = '20100000003', razon_social = 'EMPRESA C' WHERE id = v_c;

SELECT ruc INTO v_txt FROM empresas WHERE id = v_c;
IF v_txt IS DISTINCT FROM '20100000003' THEN
    RAISE EXCEPTION 'No se pudo poner el RUC a una empresa que no lo tenía (quedó %).', v_txt;
END IF;

-- Una vez puesto, no cambia.
BEGIN
    UPDATE empresas SET ruc = '20100000009' WHERE id = v_c;
    RAISE EXCEPTION 'FALLO GRAVE: se pudo cambiar el RUC de una empresa.';
EXCEPTION
    WHEN check_violation THEN NULL;  -- correcto: saltó tg_empresas_ruc_inmutable
    WHEN raise_exception THEN
        IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
END;

-- Ni se quita.
BEGIN
    UPDATE empresas SET ruc = NULL WHERE id = v_c;
    RAISE EXCEPTION 'FALLO GRAVE: se pudo quitar el RUC de una empresa.';
EXCEPTION
    WHEN check_violation THEN NULL;
    WHEN raise_exception THEN
        IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
END;

-- Dos empresas activas con el mismo RUC: no.
BEGIN
    INSERT INTO empresas (nombre, ruc, razon_social) VALUES ('Copia de A', '20100000001', 'EMPRESA A');
    RAISE EXCEPTION 'FALLO GRAVE: dos empresas activas con el mismo RUC.';
EXCEPTION
    WHEN unique_violation THEN NULL;
    WHEN raise_exception THEN
        IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
END;

-- Un RUC sin razón social: tampoco. Llegan juntos del servicio.
BEGIN
    INSERT INTO empresas (nombre, ruc) VALUES ('Sin razon social', '20100000004');
    RAISE EXCEPTION 'FALLO GRAVE: se aceptó un RUC sin razón social.';
EXCEPTION
    WHEN check_violation THEN NULL;
    WHEN raise_exception THEN
        IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
END;

-- Desactivar una empresa LIBERA su RUC (decisión del 10 de octubre): es como
-- se corrige un RUC equivocado.
UPDATE empresas SET activo = false WHERE id = v_b;
INSERT INTO empresas (nombre, ruc, razon_social) VALUES ('Empresa B otra vez', '20100000002', 'EMPRESA B');



-- =========================================================================
RAISE NOTICE '20. La clave cifrada solo puede faltar en una fila retirada (015)';
-- =========================================================================
--
-- Al rotar, la fila anterior se queda pero su clave se vacía en cuanto se
-- comprueba que el servicio ya la rechaza. La activa, la que emite, nunca
-- puede quedarse sin clave.

PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

-- Una activa sin clave: no.
BEGIN
    INSERT INTO credenciales_facturacion (empresa_id, base_url, clave_cifrada)
    VALUES (v_c, 'https://servicio.invalid', NULL);
    RAISE EXCEPTION 'FALLO GRAVE: se aceptó una credencial activa sin clave.';
EXCEPTION
    WHEN check_violation THEN NULL;  -- correcto: ck_credenciales_clave_activa
    WHEN raise_exception THEN
        IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
END;

-- Una retirada, vaciada y comprobada: sí.
INSERT INTO credenciales_facturacion
    (empresa_id, base_url, clave_cifrada, activo, rotada_en,
     clave_vaciada_en, revocada_comprobada_en, revocada_comprobacion)
VALUES
    (v_c, 'https://servicio.invalid', NULL, false, now(),
     now(), now(), 'comprobado: el servicio la rechaza');

-- Vaciada, pero con clave: no. Vaciada es vaciada.
BEGIN
    INSERT INTO credenciales_facturacion
        (empresa_id, base_url, clave_cifrada, activo, clave_vaciada_en)
    VALUES (v_c, 'https://servicio.invalid', '\x00'::bytea, false, now());
    RAISE EXCEPTION 'FALLO GRAVE: una clave vaciada conservó su copia cifrada.';
EXCEPTION
    WHEN check_violation THEN NULL;  -- correcto: ck_credenciales_vaciada
    WHEN raise_exception THEN
        IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
END;

-- Comprobada como revocada, pero activa: no.
BEGIN
    INSERT INTO credenciales_facturacion
        (empresa_id, base_url, clave_cifrada, revocada_comprobada_en)
    VALUES (v_c, 'https://servicio.invalid', '\x00'::bytea, now());
    RAISE EXCEPTION 'FALLO GRAVE: una credencial activa figura como revocada.';
EXCEPTION
    WHEN check_violation THEN NULL;  -- correcto: ck_credenciales_revocada
    WHEN raise_exception THEN
        IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
END;


-- =========================================================================
RAISE NOTICE '21. Registro libre: cuenta sin empresa, aislada y con su tope (016)';
-- =========================================================================
--
-- Quien se registra no tiene sesion: todo pasa por registrar_cuenta(). Su
-- usuario existe ANTES de tener empresa, y aun asi no ve nada de nadie.

PERFORM fijar_contexto(NULL, NULL, '10.0.0.1', 'verificar.sql', false);

SELECT r.resultado, r.usuario_id INTO v_res, v_reg
  FROM registrar_cuenta('profesional', 'natural', '12345678', 'Ana Contadora',
                        'ana.contadora@correo.pe', 'Ana', 'ana.contadora',
                        digest('token-de-prueba-016', 'sha256'), now() + interval '1 day',
                        '10.0.0.1') r;
IF v_res <> 'ok' OR v_reg IS NULL THEN
    RAISE EXCEPTION 'El registro no salio bien (%).', v_res;
END IF;

-- El mismo correo, en mayusculas: ocupado.
SELECT r.resultado INTO v_res
  FROM registrar_cuenta('negocio', 'natural', '87654321', 'Otra', 'ANA.CONTADORA@correo.pe',
                        'Otra', 'otra.persona', digest('t2', 'sha256'), now() + interval '1 day',
                        '10.0.0.1') r;
IF v_res <> 'correo_ocupado' THEN
    RAISE EXCEPTION 'Un correo repetido no se rechazo (%).', v_res;
END IF;

-- El mismo alias: ocupado. Y el siguiente libre a partir de «ana» es ana2.
SELECT r.resultado INTO v_res
  FROM registrar_cuenta('negocio', 'natural', '87654321', 'Otra', 'otra@correo.pe',
                        'Otra', 'Ana.Contadora', digest('t3', 'sha256'), now() + interval '1 day',
                        '10.0.0.1') r;
IF v_res <> 'alias_ocupado' THEN
    RAISE EXCEPTION 'Un alias repetido no se rechazo (%).', v_res;
END IF;

IF alias_libre('ana') <> 'ana2' THEN
    RAISE EXCEPTION 'alias_libre(ana) deberia ser ana2 y dio %.', alias_libre('ana');
END IF;

-- Con su propio contexto: se ve a si mismo, a su cuenta, y NADA mas.
PERFORM fijar_contexto(v_reg, NULL, '10.0.0.1', 'verificar.sql', false);

SELECT count(*) INTO v_n FROM usuarios;
IF v_n <> 1 THEN
    RAISE EXCEPTION 'El usuario recien registrado ve % usuarios. Deberia verse solo a si mismo.', v_n;
END IF;

SELECT count(*) INTO v_n FROM empresas;
IF v_n <> 0 THEN
    RAISE EXCEPTION 'FALLO GRAVE: un usuario sin empresa ve % empresas.', v_n;
END IF;

SELECT count(*) INTO v_n FROM clientes;
IF v_n <> 0 THEN
    RAISE EXCEPTION 'FALLO GRAVE: un usuario sin empresa ve % clientes.', v_n;
END IF;

SELECT count(*) INTO v_n FROM cuentas;
IF v_n <> 1 THEN
    RAISE EXCEPTION 'El usuario recien registrado ve % cuentas. Deberia ver solo la suya.', v_n;
END IF;

SELECT count(*) INTO v_n FROM permisos_efectivos WHERE usuario_id = v_reg;
IF v_n <> 0 THEN
    RAISE EXCEPTION 'Un usuario sin empresa no deberia tener permisos (tiene %).', v_n;
END IF;

-- No puede meterse en la empresa A.
BEGIN
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash)
    VALUES (v_a, 'colado2@empresa-a.pe', 'Colado', 'x');
    RAISE EXCEPTION 'FALLO GRAVE: un usuario sin empresa creo un usuario en la empresa A.';
EXCEPTION
    WHEN insufficient_privilege THEN NULL;
    WHEN raise_exception THEN
        IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
END;

-- Ni cambiarse su propio tope: las cuentas solo las escribe el super admin.
SELECT cuenta_id INTO v_cta FROM usuarios WHERE id = v_reg;
BEGIN
    UPDATE cuentas SET tope_empresas_dia = 9999 WHERE id = v_cta;
EXCEPTION WHEN insufficient_privilege THEN NULL;  -- correcto: WITH CHECK solo super
END;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
SELECT tope_empresas_dia INTO v_n FROM cuentas WHERE id = v_cta;
IF v_n <> 50 THEN
    RAISE EXCEPTION 'FALLO GRAVE: una cuenta se cambio su propio tope (quedo en %).', v_n;
END IF;

-- Usar el enlace confirma el correo: la cuenta pasa a activa.
PERFORM fijar_contexto(NULL, NULL, '10.0.0.1', 'verificar.sql', false);
IF consumir_invitacion(digest('token-de-prueba-016', 'sha256'),
                       'pbkdf2-sha256$210000$x$y', '\x01'::bytea, '10.0.0.1'::inet) IS NULL THEN
    RAISE EXCEPTION 'El enlace del registro no se pudo usar.';
END IF;

PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
SELECT estado INTO v_txt FROM cuentas WHERE id = v_cta;
IF v_txt <> 'activa' THEN
    RAISE EXCEPTION 'Usar el enlace no activo la cuenta (quedo %).', v_txt;
END IF;

-- Suspendida, no entra.
UPDATE cuentas SET estado = 'suspendida', suspendida_en = now(), motivo_suspension = 'prueba'
 WHERE id = v_cta;
SELECT empresa_operativa INTO v_ok FROM buscar_usuario_para_ingreso('ana.contadora@correo.pe');
IF v_ok THEN
    RAISE EXCEPTION 'FALLO GRAVE: una cuenta suspendida puede entrar.';
END IF;

-- Un usuario normal sin empresa NI cuenta: no puede existir.
BEGIN
    INSERT INTO usuarios (correo, nombre, clave_hash) VALUES ('suelto@x.pe', 'Suelto', 'x');
    RAISE EXCEPTION 'FALLO GRAVE: se creo un usuario sin empresa ni cuenta.';
EXCEPTION
    WHEN check_violation THEN NULL;
    WHEN raise_exception THEN
        IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
END;

-- Toda empresa nace con su cuenta, aunque el alta no la mande.
SELECT count(*) INTO v_n FROM empresas WHERE cuenta_id IS NULL;
IF v_n <> 0 THEN
    RAISE EXCEPTION 'Hay % empresas sin cuenta.', v_n;
END IF;

-- Tope por red: a los 10 intentos en una hora, no deja mas.
PERFORM fijar_contexto(NULL, NULL, '10.0.0.9', 'verificar.sql', false);
FOR i IN 1..10 LOOP
    PERFORM registrar_cuenta('negocio', 'natural', '11111111', 'Robot', 'robot@x.pe',
                             'Robot', 'robot', digest('r' || i, 'sha256'),
                             now() + interval '1 day', '10.0.0.9');
END LOOP;
SELECT r.resultado INTO v_res
  FROM registrar_cuenta('negocio', 'natural', '11111111', 'Robot', 'robot99@x.pe',
                        'Robot', 'robot99', digest('r99', 'sha256'),
                        now() + interval '1 day', '10.0.0.9') r;
IF v_res <> 'demasiados' THEN
    RAISE EXCEPTION 'El tope de 10 registros por hora y red no paro el 11 (%).', v_res;
END IF;


-- =========================================================================
RAISE NOTICE '22. La empresa propia: el asistente crea, hace Administrador y activa (017)';
-- =========================================================================
DECLARE
    v_cta1   uuid;
    v_cta2   uuid;
    v_u1     uuid;
    v_u2     uuid;
    v_u3     uuid;
    v_emp    uuid := gen_random_uuid();
    v_emp2   uuid := gen_random_uuid();
    v_r      text;
BEGIN
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

    INSERT INTO cuentas (tipo, titular_tipo, titular_documento, titular_nombre, estado, confirmada_en, origen)
    VALUES ('profesional', 'natural', '12345678', 'Erika Prueba', 'activa', now(), 'registro')
    RETURNING id INTO v_cta1;

    INSERT INTO cuentas (tipo, titular_tipo, titular_documento, titular_nombre, estado, origen)
    VALUES ('negocio', 'natural', '87654321', 'Pedro Pendiente', 'pendiente', 'registro')
    RETURNING id INTO v_cta2;

    -- (018) Cada uno es el dueño de su cuenta.
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta)
    VALUES (v_cta1, 'erika.017@correo.pe', 'Erika', 'x', 'dueno') RETURNING id INTO v_u1;
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta)
    VALUES (v_cta2, 'pedro.017@correo.pe', 'Pedro', 'x', 'dueno') RETURNING id INTO v_u2;

    -- Sin empresa todavía: no ve ni puede tocar nada de negocio.
    PERFORM fijar_contexto(v_u1, NULL, '10.0.0.1', 'verificar.sql', false);

    v_r := revisar_empresa_propia('20100000001');
    IF v_r <> 'ruc_ocupado' THEN
        RAISE EXCEPTION 'Un RUC que ya es de otra empresa no salio ocupado (%).', v_r;
    END IF;

    BEGIN
        INSERT INTO empresas (nombre, ruc, razon_social, cuenta_id)
        VALUES ('A mano', '20999999991', 'A MANO', v_cta1);
        RAISE EXCEPTION 'FALLO GRAVE: un usuario sin empresa inserto una empresa a mano.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
        WHEN raise_exception THEN
            IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    v_r := crear_empresa_propia(v_emp, 'Mi Empresa 017', '20999999992', 'MI EMPRESA 017 SAC', NULL,
                                'beta', 'dueno@correo.pe', 'https://localhost:7295',
                                '\x01'::bytea, 'abcd', '\x02'::bytea, 'http://localhost:5255/x');
    IF v_r <> 'ok' THEN
        RAISE EXCEPTION 'crear_empresa_propia no creo la empresa (%).', v_r;
    END IF;

    v_r := crear_empresa_propia(gen_random_uuid(), 'Otra', '20999999993', 'OTRA SAC', NULL,
                                'beta', NULL, 'https://localhost:7295',
                                '\x01'::bytea, 'abcd', '\x02'::bytea, 'http://localhost:5255/y');
    -- (018) Cuenta Profesional: la segunda empresa sí se puede.
    IF v_r <> 'ok' THEN
        RAISE EXCEPTION 'La Cuenta Profesional no pudo agregar su segunda empresa (%).', v_r;
    END IF;

    -- Ya es de su empresa y la ve.
    PERFORM fijar_contexto(v_u1, v_emp, '10.0.0.1', 'verificar.sql', false);

    IF NOT EXISTS (SELECT 1 FROM empresas WHERE id = v_emp AND cuenta_id = v_cta1) THEN
        RAISE EXCEPTION 'La empresa propia no quedo en la cuenta de quien la creo.';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM permisos_efectivos
                    WHERE usuario_id = v_u1 AND permiso_codigo = 'facturacion.credenciales_gestionar') THEN
        RAISE EXCEPTION 'Quien creo la empresa no quedo como Administrador.';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM credenciales_facturacion
                    WHERE empresa_id = v_emp AND activo AND creada_por_alta) THEN
        RAISE EXCEPTION 'La empresa propia no tiene su credencial del servicio.';
    END IF;

    -- No se da el modulo a mano: solo por la funcion.
    BEGIN
        INSERT INTO empresa_modulo (empresa_id, modulo_codigo, contratado_en)
        VALUES (v_emp, 'facturacion', now());
        RAISE EXCEPTION 'FALLO GRAVE: un administrador se activo un modulo a mano.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
        WHEN raise_exception THEN
            IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    v_r := activar_facturacion_propia();
    IF v_r <> 'ok' THEN
        RAISE EXCEPTION 'activar_facturacion_propia no activo (%).', v_r;
    END IF;

    v_r := activar_facturacion_propia();
    IF v_r <> 'ya_activo' THEN
        RAISE EXCEPTION 'La segunda activacion no dijo ya_activo (%).', v_r;
    END IF;

    -- Un modulo que el super administrador desactivo no se reactiva solo.
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    UPDATE empresa_modulo SET activo = false WHERE empresa_id = v_emp AND modulo_codigo = 'facturacion';
    PERFORM fijar_contexto(v_u1, v_emp, '10.0.0.1', 'verificar.sql', false);

    v_r := activar_facturacion_propia();
    IF v_r <> 'desactivado_por_admin' THEN
        RAISE EXCEPTION 'FALLO GRAVE: se reactivo un modulo que quito el super administrador (%).', v_r;
    END IF;

    -- El administrador de una empresa del super administrador no se activa modulos.
    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.1', 'verificar.sql', false);
    v_r := activar_facturacion_propia();
    IF v_r NOT IN ('no_es_autoservicio', 'sin_permiso') THEN
        RAISE EXCEPTION 'FALLO GRAVE: una empresa del alta se activo un modulo sola (%).', v_r;
    END IF;

    -- Cuenta sin confirmar: no agrega empresas.
    PERFORM fijar_contexto(v_u2, NULL, '10.0.0.2', 'verificar.sql', false);
    v_r := revisar_empresa_propia('20999999994');
    IF v_r <> 'cuenta_no_activa' THEN
        RAISE EXCEPTION 'Una cuenta pendiente pudo agregar empresa (%).', v_r;
    END IF;

    -- El tope de la cuenta.
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    UPDATE cuentas SET tope_empresas_dia = 1 WHERE id = v_cta1;
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta)
    VALUES (v_cta1, 'colega.017@correo.pe', 'Colega', 'x', 'colaborador_admin') RETURNING id INTO v_u3;

    PERFORM fijar_contexto(v_u3, NULL, '10.0.0.3', 'verificar.sql', false);
    v_r := revisar_empresa_propia('20999999995');
    IF v_r <> 'tope' THEN
        RAISE EXCEPTION 'El tope de empresas por dia de la cuenta no paro (%).', v_r;
    END IF;

    -- El super administrador no agrega empresas por aqui.
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
END;


-- =========================================================================
RAISE NOTICE '23. La cartera: cada uno abre solo lo suyo, y nadie de fuera lo ve (018)';
-- =========================================================================
DECLARE
    v_cta    uuid;
    v_neg    uuid;
    v_ana    uuid;
    v_luis   uuid;
    v_caj    uuid;
    v_rosa   uuid;
    v_e1     uuid;
    v_e2     uuid;
    v_r      text;
    v_n      integer;
    v_nuevo  uuid;
BEGIN
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

    INSERT INTO cuentas (tipo, titular_tipo, titular_documento, titular_nombre, estado, confirmada_en, origen)
    VALUES ('profesional', 'natural', '44445555', 'Ana Contadora', 'activa', now(), 'registro')
    RETURNING id INTO v_cta;

    INSERT INTO empresas (nombre, ruc, razon_social, cuenta_id) VALUES ('Bodega 018', '20777777771', 'BODEGA 018', v_cta)
        RETURNING id INTO v_e1;
    INSERT INTO empresas (nombre, ruc, razon_social, cuenta_id) VALUES ('Taller 018', '20777777772', 'TALLER 018', v_cta)
        RETURNING id INTO v_e2;

    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta)
    VALUES (v_cta, 'ana.018@correo.pe', 'Ana', 'x', 'dueno') RETURNING id INTO v_ana;
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta)
    VALUES (v_cta, 'luis.018@correo.pe', 'Luis', 'x', 'colaborador') RETURNING id INTO v_luis;
    INSERT INTO usuario_empresa (usuario_id, empresa_id) VALUES (v_luis, v_e1);
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash)
    VALUES (v_e1, 'cajera.018@correo.pe', 'Cajera', 'x') RETURNING id INTO v_caj;

    -- Luis ve una; Ana, las dos.
    PERFORM fijar_contexto(v_luis, NULL, '10.0.0.5', 'verificar.sql', false);
    SELECT count(*) INTO v_n FROM mis_empresas();
    IF v_n <> 1 THEN RAISE EXCEPTION 'El colaborador ve % empresas y le dieron 1.', v_n; END IF;

    v_r := abrir_empresa(v_e2);
    IF v_r <> 'sin_acceso' THEN
        RAISE EXCEPTION 'FALLO GRAVE: el colaborador abrio una empresa que no le dieron (%).', v_r;
    END IF;
    IF abrir_empresa(v_e1) <> 'ok' THEN RAISE EXCEPTION 'El colaborador no pudo abrir la suya.'; END IF;

    -- Una empresa de OTRA cuenta, nunca.
    IF abrir_empresa(v_a) <> 'sin_acceso' THEN
        RAISE EXCEPTION 'FALLO GRAVE: se abrio una empresa de otra cuenta.';
    END IF;

    IF (SELECT r.resultado FROM invitar_colaborador('x.018@correo.pe', 'X', 'colaborador', true, NULL,
                                                    digest('t018a', 'sha256'), now() + interval '1 day') r) <> 'sin_permiso' THEN
        RAISE EXCEPTION 'Un colaborador pudo invitar.';
    END IF;

    IF revisar_empresa_propia('20777777773') <> 'sin_permiso' THEN
        RAISE EXCEPTION 'Un colaborador pudo agregar empresas.';
    END IF;

    PERFORM fijar_contexto(v_ana, NULL, '10.0.0.4', 'verificar.sql', false);
    SELECT count(*) INTO v_n FROM mis_empresas();
    IF v_n <> 2 THEN RAISE EXCEPTION 'La duena ve % empresas y tiene 2.', v_n; END IF;

    SELECT r.resultado INTO v_r FROM invitar_colaborador('y.018@correo.pe', 'Y', 'colaborador', false, ARRAY[v_a],
                                                         digest('t018b', 'sha256'), now() + interval '1 day') r;
    IF v_r <> 'empresa_ajena' THEN
        RAISE EXCEPTION 'FALLO GRAVE: se dio acceso a una empresa de otra cuenta (%).', v_r;
    END IF;

    SELECT r.resultado, r.usuario_id INTO v_r, v_nuevo
      FROM invitar_colaborador('z.018@correo.pe', 'Zoe', 'colaborador', false, ARRAY[v_e2],
                               digest('t018c', 'sha256'), now() + interval '1 day') r;
    IF v_r <> 'ok' OR NOT puede_abrir(v_nuevo, v_e2) OR puede_abrir(v_nuevo, v_e1) THEN
        RAISE EXCEPTION 'La invitacion no dejo a Zoe con exactamente su empresa (%).', v_r;
    END IF;

    -- La cajera, dentro de la Bodega, no ve a la gente de la cuenta.
    PERFORM fijar_contexto(v_caj, v_e1, '10.0.0.6', 'verificar.sql', false);
    SELECT count(*) INTO v_n FROM usuarios WHERE nivel_cuenta IS NOT NULL;
    IF v_n <> 0 THEN
        RAISE EXCEPTION 'FALLO GRAVE: un usuario de la empresa ve % personas de la cuenta.', v_n;
    END IF;
    UPDATE usuarios SET activo = false WHERE id = v_luis;
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF NOT (SELECT activo FROM usuarios WHERE id = v_luis) THEN
        RAISE EXCEPTION 'FALLO GRAVE: un usuario de la empresa desactivo a un colaborador.';
    END IF;

    -- En pausa: nadie la abre, la cajera no entra, Luis sí (a su bandeja).
    PERFORM fijar_contexto(v_ana, NULL, '10.0.0.4', 'verificar.sql', false);
    IF (SELECT r.resultado FROM pausar_empresa(v_e1, 'El cliente se fue con otro estudio.', '20777777779') r) <> 'ruc' THEN
        RAISE EXCEPTION 'Se puso en pausa sin el RUC correcto.';
    END IF;
    IF (SELECT r.resultado FROM pausar_empresa(v_e1, 'El cliente se fue con otro estudio.', '20777777771') r) <> 'ok' THEN
        RAISE EXCEPTION 'No se pudo poner en pausa.';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF (SELECT empresa_id FROM usuarios WHERE id = v_luis) IS NOT NULL THEN
        RAISE EXCEPTION 'El colaborador se quedo con la empresa en pausa abierta.';
    END IF;
    IF (SELECT b.empresa_operativa FROM buscar_usuario_para_ingreso('cajera.018@correo.pe') b) THEN
        RAISE EXCEPTION 'FALLO GRAVE: el usuario de una empresa en pausa puede entrar.';
    END IF;
    IF NOT (SELECT b.empresa_operativa FROM buscar_usuario_para_ingreso('luis.018@correo.pe') b) THEN
        RAISE EXCEPTION 'El colaborador no puede entrar por una empresa en pausa.';
    END IF;

    PERFORM fijar_contexto(v_luis, NULL, '10.0.0.5', 'verificar.sql', false);
    IF abrir_empresa(v_e1) <> 'pausada' THEN RAISE EXCEPTION 'Se abrio una empresa en pausa.'; END IF;
    IF reanudar_empresa(v_e1) <> 'sin_permiso' THEN RAISE EXCEPTION 'Un colaborador reanudo una empresa.'; END IF;
    IF quitar_colaborador(v_nuevo) <> 'sin_permiso' THEN RAISE EXCEPTION 'Un colaborador quito a otro.'; END IF;

    PERFORM fijar_contexto(v_ana, NULL, '10.0.0.4', 'verificar.sql', false);
    -- En dos sentencias: una función STABLE ve la foto del principio de la suya.
    v_r := reanudar_empresa(v_e1);
    IF v_r <> 'ok' OR NOT puede_abrir(v_luis, v_e1) THEN
        RAISE EXCEPTION 'Reanudar no devolvio el acceso.';
    END IF;

    -- Quitarle la empresa: ya no la abre. Quitarlo: no entra.
    v_r := editar_colaborador(v_luis, 'colaborador', false, ARRAY[v_e2]);
    IF v_r <> 'ok' OR puede_abrir(v_luis, v_e1) OR NOT puede_abrir(v_luis, v_e2) THEN
        RAISE EXCEPTION 'Editar las empresas del colaborador no se aplico.';
    END IF;
    v_r := quitar_colaborador(v_luis);
    IF v_r <> 'ok' OR puede_abrir(v_luis, v_e2) THEN
        RAISE EXCEPTION 'Quitar al colaborador no le quito el acceso.';
    END IF;

    -- Cuenta Negocio: una empresa; al convertirla, puede más.
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    INSERT INTO cuentas (tipo, titular_tipo, titular_documento, titular_nombre, estado, confirmada_en, origen)
    VALUES ('negocio', 'natural', '55556666', 'Rosa Bodega', 'activa', now(), 'registro')
    RETURNING id INTO v_neg;
    INSERT INTO empresas (nombre, ruc, razon_social, cuenta_id) VALUES ('Bodega Rosita', '20777777774', 'BODEGA ROSITA', v_neg);
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta)
    VALUES (v_neg, 'rosa.018@correo.pe', 'Rosa', 'x', 'dueno') RETURNING id INTO v_rosa;

    PERFORM fijar_contexto(v_rosa, NULL, '10.0.0.7', 'verificar.sql', false);
    IF revisar_empresa_propia('20777777775') <> 'ya_tiene_empresa' THEN
        RAISE EXCEPTION 'Una Cuenta Negocio pudo agregar una segunda empresa.';
    END IF;
    v_r := convertir_a_profesional();
    IF v_r <> 'ok' OR revisar_empresa_propia('20777777775') <> 'ok' THEN
        RAISE EXCEPTION 'Convertir en Cuenta Profesional no abrio la cartera.';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
END;


-- =========================================================================
RAISE NOTICE '24. Quien se registra queda como dueño de su cuenta (020)';
-- =========================================================================

PERFORM fijar_contexto(NULL, NULL, '10.0.0.24', 'verificar.sql', false);
SELECT r.resultado, r.usuario_id INTO v_res, v_reg
  FROM registrar_cuenta('negocio', 'natural', '24242424', 'Dueña 020',
                        'duena.020@correo.pe', 'Dueña', 'duena.020',
                        digest('token-de-prueba-020', 'sha256'), now() + interval '1 day',
                        '10.0.0.24') r;
IF v_res <> 'ok' THEN
    RAISE EXCEPTION 'El registro de la 020 no salio bien (%).', v_res;
END IF;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
IF (SELECT nivel_cuenta FROM usuarios WHERE id = v_reg) IS DISTINCT FROM 'dueno' THEN
    RAISE EXCEPTION 'Quien se registra no quedo como dueño: el asistente le diria «no tiene cuenta propia».';
END IF;
IF EXISTS (SELECT 1 FROM cuentas c
            WHERE c.origen = 'registro'
              AND EXISTS (SELECT 1 FROM usuarios u WHERE u.cuenta_id = c.id AND u.activo AND NOT u.es_super_admin)
              AND NOT EXISTS (SELECT 1 FROM usuarios u WHERE u.cuenta_id = c.id AND u.nivel_cuenta = 'dueno' AND u.activo)) THEN
    RAISE EXCEPTION 'Hay cuentas de registro sin dueño.';
END IF;


-- =========================================================================
RAISE NOTICE '25. Recuperar la empresa: vuelve entera a su titular, una sola vez (021)';
-- =========================================================================
DECLARE
    v_cta   uuid;
    v_dueno uuid;
    v_caj   uuid;
    v_e     uuid;
    v_e2    uuid;
    v_nuevo uuid;
    v_r     text;
    v_n     integer;
    v_cor   text;
BEGIN
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

    INSERT INTO cuentas (tipo, titular_tipo, titular_documento, titular_nombre, estado, confirmada_en, origen)
    VALUES ('profesional', 'natural', '42424242', 'Estudio 021', 'activa', now(), 'registro')
    RETURNING id INTO v_cta;
    INSERT INTO empresas (nombre, ruc, razon_social, cuenta_id, correo_titular)
    VALUES ('Taller 021', '20888888881', 'TALLER 021 SAC', v_cta, 'titular.021@correo.pe') RETURNING id INTO v_e;
    INSERT INTO empresas (nombre, ruc, razon_social, cuenta_id, correo_titular)
    VALUES ('Bodega 021', '20888888882', 'BODEGA 021 SAC', v_cta, 'otra.021@correo.pe') RETURNING id INTO v_e2;
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta)
    VALUES (v_cta, 'estudio.021@correo.pe', 'Estudio', 'x', 'dueno') RETURNING id INTO v_dueno;
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash)
    VALUES (v_e, 'cajera.021@correo.pe', 'Cajera', 'x') RETURNING id INTO v_caj;

    -- Sin pausa no hay enlace, ni pidiéndolo por RUC.
    PERFORM fijar_contexto(NULL, NULL, '10.0.2.1', 'verificar.sql', false);
    IF EXISTS (SELECT 1 FROM pedir_recuperacion('20888888881', digest('r021x', 'sha256'),
                                                now() + interval '7 days', '10.0.2.1')) THEN
        RAISE EXCEPTION 'Se dio un enlace de recuperacion de una empresa que no esta en pausa.';
    END IF;

    -- El dueño la pausa; el enlace va al correo del titular.
    PERFORM fijar_contexto(v_dueno, NULL, '10.0.2.2', 'verificar.sql', false);
    IF (SELECT p.resultado FROM pausar_empresa(v_e, 'El cliente se va con otro estudio.', '20888888881') p) <> 'ok' THEN
        RAISE EXCEPTION 'No se pudo pausar para la prueba 25.';
    END IF;
    SELECT x.correo INTO v_cor FROM enlace_tras_pausa(v_e, digest('r021a', 'sha256'), now() + interval '7 days') x;
    IF v_cor IS DISTINCT FROM 'titular.021@correo.pe' THEN
        RAISE EXCEPTION 'El enlace de la pausa no fue al correo del titular (%).', v_cor;
    END IF;

    -- El pedido por RUC anula el anterior (y espera 10 minutos entre enlaces).
    PERFORM fijar_contexto(NULL, NULL, '10.0.2.3', 'verificar.sql', false);
    IF EXISTS (SELECT 1 FROM pedir_recuperacion('20888888881', digest('r021b', 'sha256'),
                                                now() + interval '7 days', '10.0.2.3')) THEN
        RAISE EXCEPTION 'Se dieron dos enlaces seguidos para la misma empresa.';
    END IF;

    IF (SELECT x.estado FROM resolver_recuperacion(digest('r021a', 'sha256')) x) <> 'ok' THEN
        RAISE EXCEPTION 'El enlace de la pausa no se reconoce.';
    END IF;
    IF empresa_por_enlace(digest('r021a', 'sha256')) IS DISTINCT FROM v_e THEN
        RAISE EXCEPTION 'empresa_por_enlace no devuelve la empresa del enlace (023).';
    END IF;

    -- Sin sesión: la cuenta nueva y la empresa entera a ella.
    SELECT x.resultado, x.usuario_id INTO v_r, v_nuevo
      FROM recuperar_con_cuenta_nueva(digest('r021a', 'sha256'), 'Jorge Titular',
                                      digest('inv021', 'sha256'), now() + interval '1 day', '10.0.2.3') x;
    IF v_r <> 'ok' THEN RAISE EXCEPTION 'La recuperacion no salio bien (%).', v_r; END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF NOT EXISTS (SELECT 1 FROM empresas e JOIN usuarios u ON u.cuenta_id = e.cuenta_id
                    WHERE e.id = v_e AND u.id = v_nuevo AND u.nivel_cuenta = 'dueno'
                      AND e.pausada_en IS NULL AND e.revision_pendiente AND u.empresa_id = v_e) THEN
        RAISE EXCEPTION 'La empresa no quedo en la cuenta del titular, abierta y por revisar.';
    END IF;
    IF (SELECT cuenta_id FROM usuarios WHERE id = v_caj) IS DISTINCT FROM (SELECT cuenta_id FROM empresas WHERE id = v_e) THEN
        RAISE EXCEPTION 'La cajera no se fue con su empresa.';
    END IF;
    IF puede_abrir(v_dueno, v_e) THEN
        RAISE EXCEPTION 'FALLO GRAVE: el estudio sigue abriendo una empresa que volvio a su titular.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM tokens_un_uso t WHERE t.usuario_id = v_nuevo AND t.proposito = 'invitacion') THEN
        RAISE EXCEPTION 'El titular no recibio su enlace para elegir contrasena.';
    END IF;

    -- El enlace sirve UNA vez.
    PERFORM fijar_contexto(NULL, NULL, '10.0.2.4', 'verificar.sql', false);
    IF (SELECT x.resultado FROM recuperar_con_cuenta_nueva(digest('r021a', 'sha256'), 'Otro',
                                digest('inv021b', 'sha256'), now() + interval '1 day', '10.0.2.4') x) <> 'usado' THEN
        RAISE EXCEPTION 'FALLO GRAVE: el enlace de recuperacion sirvio dos veces.';
    END IF;

    -- «Ya lo revisé».
    PERFORM fijar_contexto(v_nuevo, v_e, '10.0.2.5', 'verificar.sql', false);
    IF revision_hecha() <> 'ok' THEN RAISE EXCEPTION 'No se pudo marcar la revision.'; END IF;

    -- Recordatorios: la otra empresa, pausada hace 31 días, toca uno; y no dos.
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    UPDATE empresas SET pausada_en = now() - interval '31 days', motivo_pausa = 'Prueba de recordatorios.',
                        pausada_por = v_dueno
     WHERE id = v_e2;
    SELECT x.numero INTO v_n FROM enlace_recordatorio(v_e2, digest('r021c', 'sha256'), now() + interval '7 days') x;
    IF v_n IS DISTINCT FROM 1 THEN RAISE EXCEPTION 'No salio el primer recordatorio (%).', v_n; END IF;
    IF EXISTS (SELECT 1 FROM enlace_recordatorio(v_e2, digest('r021d', 'sha256'), now() + interval '7 days')) THEN
        RAISE EXCEPTION 'Salieron dos recordatorios el mismo mes.';
    END IF;

    -- Nadie lee los enlaces directamente.
    PERFORM fijar_contexto(v_nuevo, v_e, '10.0.2.6', 'verificar.sql', false);
    BEGIN
        SELECT count(*) INTO v_n FROM enlaces_recuperacion;
        RAISE EXCEPTION 'FALLO GRAVE: la tabla de enlaces de recuperacion se puede leer.';
    EXCEPTION WHEN insufficient_privilege THEN
        NULL;  -- lo esperado: ni siquiera hay permiso de lectura
    END;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
END;


-- =========================================================================
RAISE NOTICE '26. Reclamo con documentos: solo el super administrador lo ve y lo decide (022)';
-- =========================================================================
DECLARE
    v_cta   uuid;
    v_dueno uuid;
    v_e     uuid;
    v_sol   uuid;
    v_r     text;
    v_n     integer;
BEGIN
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    INSERT INTO cuentas (tipo, titular_tipo, titular_documento, titular_nombre, estado, confirmada_en, origen)
    VALUES ('profesional', 'natural', '43434343', 'Estudio 022', 'activa', now(), 'registro') RETURNING id INTO v_cta;
    INSERT INTO empresas (nombre, ruc, razon_social, cuenta_id, correo_titular)
    VALUES ('Taller 022', '20888888883', 'TALLER 022 SAC', v_cta, 'viejo.022@correo.pe') RETURNING id INTO v_e;
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta)
    VALUES (v_cta, 'estudio.022@correo.pe', 'Estudio', 'x', 'dueno') RETURNING id INTO v_dueno;
    UPDATE usuarios SET empresa_id = v_e WHERE id = v_dueno;

    -- Sin sesión: se pide, con un documento.
    PERFORM fijar_contexto(NULL, NULL, '10.0.3.1', 'verificar.sql', false);
    SELECT x.resultado, x.solicitud_id INTO v_r, v_sol
      FROM crear_solicitud_traspaso('20888888883', 'Jorge Reclamante', '41234567', 'representante',
                                    'nuevo.022@correo.pe', NULL, 'El correo era de mi ex contador.', '10.0.3.1') x;
    IF v_r <> 'ok' THEN RAISE EXCEPTION 'No se pudo crear la solicitud (%).', v_r; END IF;
    IF agregar_documento_solicitud(v_sol, 'ficha_ruc', 'ficha.pdf', 'application/pdf', 10, '\x00') <> 'ok' THEN
        RAISE EXCEPTION 'No se pudo adjuntar el documento.';
    END IF;
    IF (SELECT x.resultado FROM crear_solicitud_traspaso('20888888883', 'Otro', '41234568', 'titular',
                                'otro.022@correo.pe', NULL, 'Otra vez la misma empresa.', '10.0.3.1') x) <> 'ya_hay' THEN
        RAISE EXCEPTION 'Se abrieron dos solicitudes para la misma empresa.';
    END IF;

    -- Nadie de una empresa ve las solicitudes ni los documentos.
    PERFORM fijar_contexto(v_dueno, v_e, '10.0.3.2', 'verificar.sql', false);
    SELECT count(*) INTO v_n FROM solicitudes_traspaso_lista();
    IF v_n <> 0 THEN RAISE EXCEPTION 'FALLO GRAVE: una empresa ve las solicitudes de traspaso.'; END IF;
END;

DECLARE
    v_sol   uuid;
    v_doc   uuid;
    v_e     uuid;
    v_r     text;
    v_cor   text;
BEGIN
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    SELECT e.id INTO v_e FROM empresas e WHERE e.ruc = '20888888883';

    -- Sin ser super administrador no se aprueba.
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    SELECT x.id, (x.documentos -> 0 ->> 'id')::uuid INTO v_sol, v_doc FROM solicitudes_traspaso_lista() x WHERE x.ruc = '20888888883';
    IF NOT EXISTS (SELECT 1 FROM documento_solicitud(v_doc)) THEN
        RAISE EXCEPTION 'El super administrador no puede leer el documento del reclamo.';
    END IF;
    PERFORM fijar_contexto(NULL, v_e, '10.0.3.4', 'verificar.sql', false);
    IF EXISTS (SELECT 1 FROM documento_solicitud(v_doc)) THEN
        RAISE EXCEPTION 'FALLO GRAVE: alguien que no es super administrador lee un documento de un reclamo.';
    END IF;
    IF (SELECT x.resultado FROM aprobar_traspaso(v_sol, digest('t022', 'sha256'), now() + interval '7 days') x) <> 'sin_permiso' THEN
        RAISE EXCEPTION 'FALLO GRAVE: alguien que no es super administrador aprobo un traspaso.';
    END IF;

    -- El super administrador aprueba: pausa, correo nuevo y enlace a ese correo.
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    SELECT x.resultado, x.correo INTO v_r, v_cor
      FROM aprobar_traspaso(v_sol, digest('t022', 'sha256'), now() + interval '7 days') x;
    IF v_r <> 'ok' OR v_cor <> 'nuevo.022@correo.pe' THEN RAISE EXCEPTION 'No se aprobo bien (%).', v_r; END IF;
    IF NOT EXISTS (SELECT 1 FROM empresas WHERE id = v_e AND pausada_en IS NOT NULL
                                           AND correo_titular = 'nuevo.022@correo.pe') THEN
        RAISE EXCEPTION 'Al aprobar, la empresa no quedo en pausa con el correo nuevo.';
    END IF;
    IF (SELECT x.estado FROM resolver_recuperacion(digest('t022', 'sha256')) x) <> 'ok' THEN
        RAISE EXCEPTION 'Al aprobar no se creo el enlace de recuperacion.';
    END IF;
    IF (SELECT x.resultado FROM rechazar_traspaso(v_sol, 'Ya no se puede rechazar.') x) <> 'ya_revisada' THEN
        RAISE EXCEPTION 'Se reviso dos veces la misma solicitud.';
    END IF;
END;

-- =========================================================================
RAISE NOTICE '27. Camino B y respuesta al reclamo: solo el titular mueve la empresa (024)';
-- =========================================================================
DECLARE
    v_ctaA  uuid;  v_juan uuid;  v_e1 uuid;  v_caj uuid;
    v_ctaP  uuid;  v_ana  uuid;
    v_ctaQ  uuid;  v_luis uuid;  v_col uuid;
    v_e2    uuid;
    v_r     text;  v_x  record;  v_n integer;  v_ped uuid;  v_sol uuid;
BEGIN
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

    -- A: la Cuenta Negocio de Juan, con su empresa y su cajera.
    INSERT INTO cuentas (tipo, titular_tipo, titular_documento, titular_nombre, estado, confirmada_en, origen)
    VALUES ('negocio', 'natural', '42424242', 'Juan 024', 'activa', now(), 'registro') RETURNING id INTO v_ctaA;
    INSERT INTO empresas (nombre, ruc, razon_social, cuenta_id, correo_titular)
    VALUES ('Bodega 024', '10424242421', 'BODEGA 024', v_ctaA, 'juan.024@correo.pe') RETURNING id INTO v_e1;
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta, empresa_id)
    VALUES (v_ctaA, 'juan.024@correo.pe', 'Juan', 'x', 'dueno', v_e1) RETURNING id INTO v_juan;
    INSERT INTO usuario_rol (usuario_id, rol_id) VALUES (v_juan, '11111111-1111-1111-1111-111111111111');
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash)
    VALUES (v_e1, 'cajera.024@correo.pe', 'Cajera', 'x') RETURNING id INTO v_caj;

    -- P: el estudio de Ana. Q: el estudio de Luis, con un colaborador.
    INSERT INTO cuentas (tipo, titular_tipo, titular_documento, titular_nombre, estado, confirmada_en, origen)
    VALUES ('profesional', 'natural', '43434344', 'Estudio Ana 024', 'activa', now(), 'registro') RETURNING id INTO v_ctaP;
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta)
    VALUES (v_ctaP, 'ana.024@correo.pe', 'Ana', 'x', 'dueno') RETURNING id INTO v_ana;
    INSERT INTO cuentas (tipo, titular_tipo, titular_documento, titular_nombre, estado, confirmada_en, origen)
    VALUES ('profesional', 'natural', '43434345', 'Estudio Luis 024', 'activa', now(), 'registro') RETURNING id INTO v_ctaQ;
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta)
    VALUES (v_ctaQ, 'luis.024@correo.pe', 'Luis', 'x', 'dueno') RETURNING id INTO v_luis;
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta, ve_todas)
    VALUES (v_ctaQ, 'col.024@correo.pe', 'Colaborador', 'x', 'colaborador', true) RETURNING id INTO v_col;
    INSERT INTO empresas (nombre, ruc, razon_social, cuenta_id, correo_titular)
    VALUES ('Taller 024', '20424242423', 'TALLER 024 SAC', v_ctaQ, 'nuevo.024@correo.pe') RETURNING id INTO v_e2;

    -- --- Revisar y pedir -----------------------------------------------------
    PERFORM fijar_contexto(v_ana, NULL, '10.0.4.1', 'verificar.sql', false);
    SELECT * INTO v_x FROM revisar_pedido_empresa('10424242421');
    IF v_x.resultado <> 'ok' OR v_x.correo <> 'j•••@correo.pe' THEN
        RAISE EXCEPTION 'Revisar el pedido no dio ok con el correo tapado (%, %).', v_x.resultado, v_x.correo;
    END IF;
    IF (SELECT resultado FROM revisar_pedido_empresa('20999999990')) <> 'libre' THEN
        RAISE EXCEPTION 'Un RUC que no esta en Reyban no sale como libre.';
    END IF;

    PERFORM fijar_contexto(v_col, NULL, '10.0.4.2', 'verificar.sql', false);
    IF (SELECT resultado FROM revisar_pedido_empresa('10424242421')) <> 'sin_permiso' THEN
        RAISE EXCEPTION 'Un colaborador puede pedir empresas.';
    END IF;

    PERFORM fijar_contexto(v_ana, NULL, '10.0.4.1', 'verificar.sql', false);
    SELECT x.resultado, x.pedido_id INTO v_r, v_ped
      FROM pedir_empresa('10424242421', 'Juan, soy Ana.', digest('b024a', 'sha256'), now() + interval '7 days') x;
    IF v_r <> 'ok' THEN RAISE EXCEPTION 'No se pudo pedir la empresa (%).', v_r; END IF;
    IF (SELECT resultado FROM revisar_pedido_empresa('10424242421')) <> 'ya_pediste' THEN
        RAISE EXCEPTION 'No dice que ya la pediste.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM mis_pedidos() p WHERE p.id = v_ped AND p.estado = 'abierto') THEN
        RAISE EXCEPTION 'El pedido no sale en Mis empresas de quien lo pidio.';
    END IF;
    PERFORM fijar_contexto(v_luis, NULL, '10.0.4.3', 'verificar.sql', false);
    IF (SELECT resultado FROM revisar_pedido_empresa('10424242421')) <> 'ya_pedida' THEN
        RAISE EXCEPTION 'Se puede pedir una empresa que ya tiene un pedido abierto.';
    END IF;
    IF EXISTS (SELECT 1 FROM mis_pedidos()) THEN
        RAISE EXCEPTION 'FALLO GRAVE: una cuenta ve los pedidos de otra.';
    END IF;

    -- Nadie lee la tabla de pedidos directamente.
    BEGIN
        SELECT count(*) INTO v_n FROM pedidos_cartera;
        RAISE EXCEPTION 'FALLO GRAVE: la tabla de pedidos se puede leer.';
    EXCEPTION WHEN insufficient_privilege THEN
        NULL;
    END;

    -- --- El titular abre el enlace y aprueba (sin sesión) --------------------
    PERFORM fijar_contexto(NULL, NULL, '10.0.4.4', 'verificar.sql', false);
    SELECT * INTO v_x FROM resolver_pedido(digest('b024a', 'sha256'));
    IF v_x.estado <> 'ok' OR v_x.titular <> 'mover' OR v_x.quien <> 'Ana' THEN
        RAISE EXCEPTION 'La pagina del titular no se ve bien (%, %).', v_x.estado, v_x.titular;
    END IF;
    SELECT * INTO v_x FROM responder_pedido(digest('b024a', 'sha256'), true, true, NULL, NULL, NULL);
    IF v_x.resultado <> 'aprobado' OR v_x.titular <> 'mover' OR v_x.pide_correo <> 'ana.024@correo.pe' THEN
        RAISE EXCEPTION 'El titular no pudo aprobar (%).', v_x.resultado;
    END IF;
    IF v_x.aviso_correo IS NOT NULL THEN
        RAISE EXCEPTION 'Se avisa al propio titular como si fuera otra cuenta.';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF (SELECT cuenta_id FROM empresas WHERE id = v_e1) IS DISTINCT FROM v_ctaP THEN
        RAISE EXCEPTION 'La empresa no paso a la cuenta que la pidio.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM usuarios WHERE id = v_juan AND nivel_cuenta IS NULL
                      AND empresa_id = v_e1 AND cuenta_id = v_ctaP) THEN
        RAISE EXCEPTION 'El titular no quedo como usuario de su empresa.';
    END IF;
    IF (SELECT cuenta_id FROM usuarios WHERE id = v_caj) IS DISTINCT FROM v_ctaP THEN
        RAISE EXCEPTION 'La cajera no se fue con su empresa.';
    END IF;
    IF NOT puede_abrir(v_ana, v_e1) THEN RAISE EXCEPTION 'Ana no puede abrir la empresa que le confiaron.'; END IF;
    IF (SELECT revision_pendiente FROM empresas WHERE id = v_e1) THEN
        RAISE EXCEPTION 'El camino B dejo la empresa «por revisar» (eso es solo al recuperarla).';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '10.0.4.4', 'verificar.sql', false);
    IF (SELECT resultado FROM responder_pedido(digest('b024a', 'sha256'), true, true, NULL, NULL, NULL)) <> 'usado' THEN
        RAISE EXCEPTION 'FALLO GRAVE: el enlace del titular sirvio dos veces.';
    END IF;

    -- --- Otro pedido, y el titular dice que no -------------------------------
    PERFORM fijar_contexto(v_luis, NULL, '10.0.4.3', 'verificar.sql', false);
    IF (SELECT resultado FROM pedir_empresa('10424242421', NULL, digest('b024b', 'sha256'), now() + interval '7 days')) <> 'ok' THEN
        RAISE EXCEPTION 'No se pudo pedir de nuevo.';
    END IF;
    PERFORM fijar_contexto(NULL, NULL, '10.0.4.4', 'verificar.sql', false);
    IF (SELECT titular FROM resolver_pedido(digest('b024b', 'sha256'))) <> 'ya_entra' THEN
        RAISE EXCEPTION 'El titular que ya es usuario de su empresa no sale como «ya entra».';
    END IF;
    IF (SELECT resultado FROM responder_pedido(digest('b024b', 'sha256'), false, false, NULL, NULL, NULL)) <> 'rechazado' THEN
        RAISE EXCEPTION 'El titular no pudo decir que no.';
    END IF;
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF (SELECT cuenta_id FROM empresas WHERE id = v_e1) IS DISTINCT FROM v_ctaP THEN
        RAISE EXCEPTION 'FALLO GRAVE: un «no» del titular movio la empresa.';
    END IF;
    PERFORM fijar_contexto(v_luis, NULL, '10.0.4.3', 'verificar.sql', false);
    IF NOT EXISTS (SELECT 1 FROM mis_pedidos() p WHERE p.estado = 'rechazado') THEN
        RAISE EXCEPTION 'Quien pidio no ve que el titular no acepto.';
    END IF;

    -- --- En pausa: aprobar la reanuda ----------------------------------------
    PERFORM fijar_contexto(v_ana, NULL, '10.0.4.1', 'verificar.sql', false);
    IF (SELECT resultado FROM pausar_empresa(v_e1, 'Prueba del camino B en pausa.', '10424242421')) <> 'ok' THEN
        RAISE EXCEPTION 'No se pudo pausar para la prueba.';
    END IF;
    PERFORM fijar_contexto(v_luis, NULL, '10.0.4.3', 'verificar.sql', false);
    IF (SELECT resultado FROM pedir_empresa('10424242421', NULL, digest('b024c', 'sha256'), now() + interval '7 days')) <> 'ok' THEN
        RAISE EXCEPTION 'No se pudo pedir una empresa en pausa.';
    END IF;
    PERFORM fijar_contexto(NULL, NULL, '10.0.4.4', 'verificar.sql', false);
    SELECT * INTO v_x FROM responder_pedido(digest('b024c', 'sha256'), true, false, NULL, NULL, NULL);
    IF v_x.resultado <> 'aprobado' OR NOT v_x.estaba_pausada OR v_x.aviso_correo <> 'ana.024@correo.pe' THEN
        RAISE EXCEPTION 'Aprobar una empresa en pausa no salio bien (%).', v_x.resultado;
    END IF;
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF NOT EXISTS (SELECT 1 FROM empresas WHERE id = v_e1 AND cuenta_id = v_ctaQ AND pausada_en IS NULL) THEN
        RAISE EXCEPTION 'La empresa en pausa no paso reanudada a la cuenta que la pidio.';
    END IF;
    IF puede_abrir(v_ana, v_e1) THEN
        RAISE EXCEPTION 'FALLO GRAVE: la cuenta anterior sigue abriendo la empresa.';
    END IF;

    -- --- El titular sin usuario: se le crea uno, con su nombre ---------------
    PERFORM fijar_contexto(v_ana, NULL, '10.0.4.1', 'verificar.sql', false);
    IF (SELECT resultado FROM pedir_empresa('20424242423', NULL, digest('b024d', 'sha256'), now() + interval '7 days')) <> 'ok' THEN
        RAISE EXCEPTION 'No se pudo pedir la empresa del titular sin usuario.';
    END IF;
    PERFORM fijar_contexto(NULL, NULL, '10.0.4.4', 'verificar.sql', false);
    IF (SELECT resultado FROM responder_pedido(digest('b024d', 'sha256'), true, true, '', digest('inv024', 'sha256'),
                                               now() + interval '1 day')) <> 'nombre' THEN
        RAISE EXCEPTION 'Se creo un usuario sin nombre.';
    END IF;
    SELECT * INTO v_x FROM responder_pedido(digest('b024d', 'sha256'), true, true, 'Pedro Titular',
                                            digest('inv024', 'sha256'), now() + interval '1 day');
    IF v_x.resultado <> 'aprobado' OR v_x.titular <> 'crear' OR NOT v_x.invitacion THEN
        RAISE EXCEPTION 'No se creo el usuario del titular (%).', v_x.resultado;
    END IF;
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF NOT EXISTS (SELECT 1 FROM usuarios u JOIN usuario_rol ur ON ur.usuario_id = u.id AND ur.activo
                    JOIN tokens_un_uso t ON t.usuario_id = u.id AND t.proposito = 'invitacion'
                   WHERE u.correo = 'nuevo.024@correo.pe' AND u.empresa_id = v_e2 AND u.cuenta_id = v_ctaP
                     AND u.nivel_cuenta IS NULL AND ur.rol_id = '11111111-1111-1111-1111-111111111111') THEN
        RAISE EXCEPTION 'El usuario del titular no quedo como Administrador de su empresa con su enlace.';
    END IF;

    -- --- Cancelar ------------------------------------------------------------
    PERFORM fijar_contexto(v_luis, NULL, '10.0.4.3', 'verificar.sql', false);
    SELECT x.pedido_id INTO v_ped
      FROM pedir_empresa('20424242423', NULL, digest('b024e', 'sha256'), now() + interval '7 days') x;
    IF cancelar_pedido(v_ped) <> 'ok' THEN RAISE EXCEPTION 'No se pudo cancelar el pedido.'; END IF;
    PERFORM fijar_contexto(NULL, NULL, '10.0.4.4', 'verificar.sql', false);
    IF (SELECT resultado FROM responder_pedido(digest('b024e', 'sha256'), true, false, NULL, NULL, NULL)) <> 'cancelado' THEN
        RAISE EXCEPTION 'FALLO GRAVE: un pedido cancelado se pudo aprobar.';
    END IF;

    -- --- Camino C: la cuenta responde ----------------------------------------
    SELECT x.solicitud_id INTO v_sol
      FROM crear_solicitud_traspaso('20424242423', 'Carlos Reclamante', '41234569', 'representante',
                                    'carlos.024@correo.pe', NULL, 'El correo era del contador.', '10.0.4.9') x;

    PERFORM fijar_contexto(v_luis, NULL, '10.0.4.3', 'verificar.sql', false);
    IF EXISTS (SELECT 1 FROM reclamos_sobre_mis_empresas()) THEN
        RAISE EXCEPTION 'FALLO GRAVE: una cuenta ve los reclamos de empresas que no son suyas.';
    END IF;
    IF (SELECT resultado FROM responder_reclamo(v_sol, 'de_acuerdo', NULL)) <> 'no_existe' THEN
        RAISE EXCEPTION 'FALLO GRAVE: una cuenta responde un reclamo de una empresa ajena.';
    END IF;
    IF (SELECT resultado FROM revisar_pedido_empresa('20424242423')) <> 'reclamo_abierto' THEN
        RAISE EXCEPTION 'Se puede pedir una empresa con un reclamo abierto.';
    END IF;

    PERFORM fijar_contexto(v_ana, NULL, '10.0.4.1', 'verificar.sql', false);
    IF NOT EXISTS (SELECT 1 FROM reclamos_sobre_mis_empresas() r WHERE r.id = v_sol AND r.puedo_responder) THEN
        RAISE EXCEPTION 'La cuenta no ve el reclamo sobre su empresa.';
    END IF;
    IF (SELECT resultado FROM responder_reclamo(v_sol, 'se_opone', 'corto')) <> 'texto' THEN
        RAISE EXCEPTION 'Se acepto oponerse sin motivo.';
    END IF;
    IF (SELECT resultado FROM responder_reclamo(v_sol, 'se_opone', 'El gerente actual es otra persona.')) <> 'ok' THEN
        RAISE EXCEPTION 'La cuenta no pudo responder.';
    END IF;
    IF (SELECT resultado FROM responder_reclamo(v_sol, 'de_acuerdo', NULL)) <> 'ya_respondida' THEN
        RAISE EXCEPTION 'Se respondio dos veces el mismo reclamo.';
    END IF;
    IF agregar_documento_respuesta(v_sol, 'vigencia.pdf', 'application/pdf', 10, '\x00') <> 'ok' THEN
        RAISE EXCEPTION 'No se pudo adjuntar el documento de la respuesta.';
    END IF;
    IF agregar_documento_respuesta(v_sol, 'otro.pdf', 'application/pdf', 10, '\x00') <> 'no' THEN
        RAISE EXCEPTION 'Se adjuntaron dos documentos a la respuesta.';
    END IF;

    -- En pausa con un reclamo abierto, no se reanuda.
    IF (SELECT resultado FROM pausar_empresa(v_e2, 'Prueba de reanudar en revision.', '20424242423')) <> 'ok' THEN
        RAISE EXCEPTION 'No se pudo pausar para la prueba.';
    END IF;
    IF reanudar_empresa(v_e2) <> 'en_revision' THEN
        RAISE EXCEPTION 'Se reanudo una empresa con un reclamo abierto.';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF NOT EXISTS (SELECT 1 FROM solicitudes_traspaso_lista() x
                    WHERE x.id = v_sol AND x.respuesta = 'se_opone' AND x.respuesta_quien = 'Ana (dueño)'
                      AND jsonb_array_length(x.documentos) = 1) THEN
        RAISE EXCEPTION 'El super administrador no ve la respuesta de la cuenta.';
    END IF;
END;

-- =========================================================================
RAISE NOTICE '28. Certificado por vencer: un correo por umbral, a quien administra (025)';
-- =========================================================================
DECLARE
    v_cta  uuid;  v_e7 uuid;  v_e40 uuid;  v_esu uuid;  v_adm uuid;  v_caj uuid;
    v_due  uuid;  v_cola uuid;  v_colb uuid;
    v_x    record;  v_v timestamptz;  v_n integer;
BEGIN
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

    INSERT INTO cuentas (tipo, titular_tipo, titular_documento, titular_nombre, estado, confirmada_en, origen)
    VALUES ('profesional', 'natural', '45454525', 'Estudio 025', 'activa', now(), 'registro') RETURNING id INTO v_cta;

    -- Vence en 6 días (por fechas de Lima), en la cartera del estudio.
    INSERT INTO empresas (nombre, ruc, razon_social, cuenta_id, correo_titular)
    VALUES ('Bodega 025', '10252525251', 'BODEGA 025', v_cta, 'titular.025@correo.pe') RETURNING id INTO v_e7;
    -- Lejos (40 días) y suspendida (3 días): ninguna avisa.
    INSERT INTO empresas (nombre, ruc, razon_social, correo_titular)
    VALUES ('Lejos 025', '10252525254', 'LEJOS 025', 'lejos.025@correo.pe') RETURNING id INTO v_e40;
    INSERT INTO empresas (nombre, ruc, razon_social, correo_titular, suspendida_en, motivo_suspension)
    VALUES ('Suspendida 025', '10252525253', 'SUSP 025', 'susp.025@correo.pe', now(), 'Prueba 025.') RETURNING id INTO v_esu;

    v_v := ((now() AT TIME ZONE 'America/Lima')::date + 6 + time '12:00') AT TIME ZONE 'America/Lima';
    INSERT INTO credenciales_facturacion (empresa_id, base_url, clave_cifrada, certificado_valido_hasta) VALUES
        (v_e7,  'https://servicio.invalid', '\x00', v_v),
        (v_e40, 'https://servicio.invalid', '\x00', now() + interval '40 days'),
        (v_esu, 'https://servicio.invalid', '\x00', now() + interval '3 days');

    -- Un administrador de la empresa con el correo del titular en mayúsculas,
    -- y una cajera: el primero se cuenta una vez, la segunda no recibe.
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash)
    VALUES (v_e7, 'TITULAR.025@correo.pe', 'Admin 025', 'x') RETURNING id INTO v_adm;
    INSERT INTO usuario_rol (usuario_id, rol_id) VALUES (v_adm, '11111111-1111-1111-1111-111111111111');
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash)
    VALUES (v_e7, 'cajera.025@correo.pe', 'Cajera 025', 'x') RETURNING id INTO v_caj;

    -- La cuenta: el dueño y un colaborador administrador que la ve reciben;
    -- un colaborador administrador sin acceso a ella, no.
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta)
    VALUES (v_cta, 'dueno.025@correo.pe', 'Dueño 025', 'x', 'dueno') RETURNING id INTO v_due;
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta)
    VALUES (v_cta, 'cola.025@correo.pe', 'Colab A 025', 'x', 'colaborador_admin') RETURNING id INTO v_cola;
    INSERT INTO usuario_empresa (usuario_id, empresa_id) VALUES (v_cola, v_e7);
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta)
    VALUES (v_cta, 'colb.025@correo.pe', 'Colab B 025', 'x', 'colaborador_admin') RETURNING id INTO v_colb;

    SELECT * INTO v_x FROM certificados_por_avisar() p WHERE p.empresa_id = v_e7;
    IF NOT FOUND OR v_x.umbral <> 7 OR v_x.dias <> 6 THEN
        RAISE EXCEPTION 'El certificado a 6 dias no toca el aviso de 7 (%, %).', v_x.umbral, v_x.dias;
    END IF;
    IF EXISTS (SELECT 1 FROM certificados_por_avisar() p WHERE p.empresa_id IN (v_e40, v_esu)) THEN
        RAISE EXCEPTION 'Avisa una empresa lejos de vencer o suspendida.';
    END IF;

    SELECT * INTO v_x FROM tomar_aviso_certificado(v_e7, v_v, 7::smallint);
    IF v_x.aviso_id IS NULL OR v_x.dias <> 6 OR v_x.nombre <> 'Bodega 025' THEN
        RAISE EXCEPTION 'No se pudo tomar el aviso del certificado.';
    END IF;
    IF NOT (v_x.correos @> ARRAY['cola.025@correo.pe', 'dueno.025@correo.pe']) THEN
        RAISE EXCEPTION 'El aviso no va al dueño y al colaborador administrador (%).', v_x.correos;
    END IF;
    IF EXISTS (SELECT 1 FROM unnest(v_x.correos) c
                WHERE lower(c) IN ('cajera.025@correo.pe', 'colb.025@correo.pe')) THEN
        RAISE EXCEPTION 'El aviso va a quien no administra la empresa (%).', v_x.correos;
    END IF;
    SELECT count(*) INTO v_n FROM unnest(v_x.correos) c WHERE lower(c) = 'titular.025@correo.pe';
    IF v_n <> 1 THEN
        RAISE EXCEPTION 'El correo del titular sale % veces.', v_n;
    END IF;

    -- Una sola vez.
    IF EXISTS (SELECT 1 FROM tomar_aviso_certificado(v_e7, v_v, 7::smallint)) THEN
        RAISE EXCEPTION 'Se tomó dos veces el mismo aviso.';
    END IF;
    IF EXISTS (SELECT 1 FROM certificados_por_avisar() p WHERE p.empresa_id = v_e7) THEN
        RAISE EXCEPTION 'El aviso ya enviado sigue pendiente.';
    END IF;

    -- La aplicación no lee la tabla directamente.
    BEGIN
        PERFORM 1 FROM avisos_certificado;
        RAISE EXCEPTION 'FALLO GRAVE: la aplicación lee avisos_certificado directamente.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
        WHEN raise_exception THEN
            IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- Renovado: la cuenta empieza otra vez.
    UPDATE credenciales_facturacion SET certificado_valido_hasta = now() + interval '20 days'
     WHERE empresa_id = v_e7 AND activo;
    SELECT * INTO v_x FROM certificados_por_avisar() p WHERE p.empresa_id = v_e7;
    IF NOT FOUND OR v_x.umbral <> 30 THEN
        RAISE EXCEPTION 'Con el certificado renovado no vuelve a empezar la cuenta.';
    END IF;

    -- Vencido hace más de 30 días: ya no se avisa.
    UPDATE credenciales_facturacion SET certificado_valido_hasta = now() - interval '40 days'
     WHERE empresa_id = v_e7 AND activo;
    IF EXISTS (SELECT 1 FROM certificados_por_avisar() p WHERE p.empresa_id = v_e7) THEN
        RAISE EXCEPTION 'Avisa un certificado vencido hace 40 dias.';
    END IF;
END;

-- =========================================================================
RAISE NOTICE '29. Las puertas son de plataforma_puertas: BYPASSRLS, sin login, sin superusuario (027)';
-- =========================================================================
SELECT count(*) INTO v_n
  FROM pg_proc p
 WHERE p.pronamespace = 'public'::regnamespace
   AND p.prosecdef
   AND pg_get_userbyid(p.proowner) <> 'plataforma_puertas';
IF v_n > 0 THEN
    RAISE EXCEPTION 'Hay % funcion(es) SECURITY DEFINER con otro dueno. En su migracion: ALTER FUNCTION ... OWNER TO plataforma_puertas.', v_n;
END IF;
IF NOT EXISTS (SELECT 1 FROM pg_roles
                WHERE rolname = 'plataforma_puertas'
                  AND rolbypassrls AND NOT rolcanlogin AND NOT rolsuper
                  AND NOT rolcreaterole AND NOT rolcreatedb) THEN
    RAISE EXCEPTION 'plataforma_puertas no es como debe: BYPASSRLS, sin login, sin superusuario.';
END IF;
IF pg_has_role(current_user, 'plataforma_puertas', 'MEMBER') THEN
    RAISE EXCEPTION 'FALLO GRAVE: la aplicacion puede ponerse el rol de las puertas.';
END IF;

-- =========================================================================
RAISE NOTICE '30. Facturacion contratada en el alta: su administrador la activa con el asistente (028)';
-- =========================================================================
DECLARE
    v_e   uuid;
    v_adm uuid;
    v_caj uuid;
    v_r   text;
BEGIN
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

    INSERT INTO empresas (nombre, ruc, razon_social) VALUES ('Alta 028', '20282828281', 'ALTA 028')
        RETURNING id INTO v_e;

    IF NOT EXISTS (SELECT 1 FROM empresas WHERE id = v_e AND facturacion_contratada_en IS NOT NULL) THEN
        RAISE EXCEPTION 'El alta del super administrador no dejo Facturacion contratada.';
    END IF;
    IF EXISTS (SELECT 1 FROM empresa_modulo WHERE empresa_id = v_e AND modulo_codigo = 'facturacion') THEN
        RAISE EXCEPTION 'FALLO GRAVE: contratada no es activa, y el alta la activo.';
    END IF;

    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash)
    VALUES (v_e, 'admin.028@correo.pe', 'Admin 028', 'x') RETURNING id INTO v_adm;
    INSERT INTO usuario_rol (usuario_id, rol_id) VALUES (v_adm, '11111111-1111-1111-1111-111111111111');
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash)
    VALUES (v_e, 'cajera.028@correo.pe', 'Cajera 028', 'x') RETURNING id INTO v_caj;

    -- Sin el permiso, no.
    PERFORM fijar_contexto(v_caj, v_e, '10.0.0.28', 'verificar.sql', false);
    v_r := activar_facturacion_propia();
    IF v_r <> 'sin_permiso' THEN
        RAISE EXCEPTION 'FALLO GRAVE: alguien sin permiso activo Facturacion (%).', v_r;
    END IF;

    -- El administrador no se cambia la marca a mano.
    PERFORM fijar_contexto(v_adm, v_e, '10.0.0.28', 'verificar.sql', false);
    BEGIN
        UPDATE empresas SET facturacion_contratada_en = NULL WHERE id = v_e;
        RAISE EXCEPTION 'FALLO GRAVE: el administrador de la empresa cambio la marca de contratada.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
        WHEN raise_exception THEN
            IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- Con el asistente, si.
    v_r := activar_facturacion_propia();
    IF v_r <> 'ok' THEN
        RAISE EXCEPTION 'El administrador de una empresa contratada no pudo activar Facturacion (%).', v_r;
    END IF;
    v_r := activar_facturacion_propia();
    IF v_r <> 'ya_activo' THEN
        RAISE EXCEPTION 'La segunda activacion no dijo ya_activo (%).', v_r;
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
END;

-- =========================================================================
RAISE NOTICE '31. Consultas externas: se cuentan y el aviso sale una vez al 80 %% y al 100 %% (029)';
-- =========================================================================
DECLARE
    v_su uuid;
    v_r  json;
    i    integer;
BEGIN
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    INSERT INTO usuarios (correo, nombre, clave_hash, es_super_admin)
    VALUES ('super.029@correo.pe', 'Super 029', 'x', true) RETURNING id INTO v_su;

    PERFORM fijar_contexto(v_su, NULL, '10.0.0.29', 'verificar.sql', false);
    FOR i IN 1..3 LOOP
        v_r := anotar_consulta_externa('verificar', 5, false)::json;
        IF v_r->>'aviso' <> 'ok' THEN RAISE EXCEPTION 'Aviso antes de tiempo (%).', v_r; END IF;
    END LOOP;

    v_r := anotar_consulta_externa('verificar', 5, false)::json;
    IF v_r->>'aviso' <> 'aviso_80' OR json_array_length(v_r->'correos') = 0 THEN
        RAISE EXCEPTION 'No salio el aviso del 80 %% con sus correos (%).', v_r;
    END IF;

    v_r := anotar_consulta_externa('verificar', 5, false)::json;
    IF v_r->>'aviso' <> 'aviso_100' THEN RAISE EXCEPTION 'No salio el aviso del 100 %% (%).', v_r; END IF;

    v_r := anotar_consulta_externa('verificar', 5, true)::json;
    IF v_r->>'aviso' <> 'ok' OR json_array_length(v_r->'correos') <> 0 THEN
        RAISE EXCEPTION 'El aviso del 100 %% salio dos veces (%).', v_r;
    END IF;

    BEGIN
        PERFORM 1 FROM consultas_externas;
        RAISE EXCEPTION 'FALLO GRAVE: la aplicacion lee consultas_externas directamente.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
        WHEN raise_exception THEN
            IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
END;

-- =========================================================================
RAISE NOTICE '32. Conexión con SUNAT: solo el super la cambia; la numeración se separa por ambiente (030)';
-- =========================================================================
DECLARE
    v_su uuid;
    v_r  json;
BEGIN
    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.30', 'verificar.sql', false);
    v_r := conexion_sunat()::json;
    IF v_r->>'modo' IS NULL THEN RAISE EXCEPTION 'conexion_sunat() no dice el modo (%).', v_r; END IF;

    BEGIN
        PERFORM cambiar_conexion_sunat('real');
        RAISE EXCEPTION 'FALLO GRAVE: un usuario de empresa cambió la conexión con SUNAT.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
        WHEN raise_exception THEN
            IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- El ambiente de otra empresa no se toca; el de la propia, sí.
    BEGIN
        PERFORM fijar_ambiente_empresa(v_b, 'produccion');
        RAISE EXCEPTION 'FALLO GRAVE: la empresa A cambió el ambiente de la B.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
        WHEN raise_exception THEN
            IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;
    IF fijar_ambiente_empresa(v_a, 'produccion') <> 1 THEN
        RAISE EXCEPTION 'La empresa A no pudo guardar su propio ambiente.';
    END IF;
    PERFORM fijar_ambiente_empresa(v_a, 'beta');

    -- F001-1 real puede convivir con F001-1 de prueba; dos F001-1 reales, no.
    INSERT INTO documentos (empresa_id, tipo, serie, correlativo, cliente_id, fecha_emision, moneda, ambiente)
    VALUES (v_a, '01', 'F001', 1, v_cli_a, current_date, 'PEN', 'produccion');
    BEGIN
        INSERT INTO documentos (empresa_id, tipo, serie, correlativo, cliente_id, fecha_emision, moneda, ambiente)
        VALUES (v_a, '01', 'F001', 1, v_cli_a, current_date, 'PEN', 'produccion');
        RAISE EXCEPTION 'FALLO GRAVE: se aceptó un correlativo real duplicado.';
    EXCEPTION
        WHEN unique_violation THEN NULL;
        WHEN raise_exception THEN
            IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    INSERT INTO usuarios (correo, nombre, clave_hash, es_super_admin)
    VALUES ('super.030@correo.pe', 'Super 030', 'x', true) RETURNING id INTO v_su;
    PERFORM fijar_contexto(v_su, NULL, '10.0.0.30', 'verificar.sql', true);

    v_r := cambiar_conexion_sunat('real')::json;
    IF v_r->>'modo' <> 'real' THEN RAISE EXCEPTION 'El super no pudo pasar a real (%).', v_r; END IF;
    v_r := cambiar_conexion_sunat('beta')::json;
    IF v_r->>'modo' <> 'beta' THEN RAISE EXCEPTION 'El super no pudo volver a beta (%).', v_r; END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
END;

-- =========================================================================
RAISE NOTICE '33. Notas de crédito: permiso propio; el Facturador no lo trae; toda nota dice a qué afecta (031)';
-- =========================================================================
DECLARE
    v_ref uuid;
BEGIN
    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.31', 'verificar.sql', false);

    IF NOT EXISTS (SELECT 1 FROM permisos WHERE codigo = 'facturacion.notas_credito') THEN
        RAISE EXCEPTION 'Falta el permiso facturacion.notas_credito.';
    END IF;

    -- v_usr_a es Facturador (comprobación 15): no puede hacer notas.
    IF EXISTS (SELECT 1 FROM permisos_efectivos
                WHERE usuario_id = v_usr_a AND permiso_codigo = 'facturacion.notas_credito') THEN
        RAISE EXCEPTION 'FALLO GRAVE: el rol Facturador trae el permiso de notas de crédito.';
    END IF;

    -- Una nota sin el comprobante al que afecta no se guarda.
    BEGIN
        INSERT INTO documentos (empresa_id, tipo, cliente_id, fecha_emision, moneda)
        VALUES (v_a, '07', v_cli_a, current_date, 'PEN');
        RAISE EXCEPTION 'FALLO GRAVE: se guardó una nota de crédito sin comprobante de referencia.';
    EXCEPTION
        WHEN check_violation THEN NULL;
        WHEN raise_exception THEN
            IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- Con su referencia, sí.
    INSERT INTO documentos (empresa_id, tipo, serie, correlativo, cliente_id, fecha_emision, moneda, ambiente)
    VALUES (v_a, '01', 'F001', 33, v_cli_a, current_date, 'PEN', 'beta') RETURNING id INTO v_ref;
    INSERT INTO documentos (empresa_id, tipo, cliente_id, fecha_emision, moneda, documento_ref_id, tipo_nota, motivo_nota)
    VALUES (v_a, '07', v_cli_a, current_date, 'PEN', v_ref, '01', 'Prueba de verificar.sql');

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

    IF NOT EXISTS (SELECT 1 FROM rol_permiso
                    WHERE rol_id = '11111111-1111-1111-1111-111111111111'
                      AND permiso_codigo = 'facturacion.notas_credito') THEN
        RAISE EXCEPTION 'El Administrador no trae el permiso de notas de crédito.';
    END IF;
END;

-- =========================================================================
RAISE NOTICE '34. Reportes: un permiso por reporte; el Facturador solo trae «Mi día» (032)';
-- =========================================================================
BEGIN
    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.32', 'verificar.sql', false);

    IF (SELECT count(*) FROM permisos WHERE area = 'reportes') < 5 THEN
        RAISE EXCEPTION 'Faltan permisos de reportes.';
    END IF;

    -- v_usr_a es Facturador (comprobación 15).
    IF NOT EXISTS (SELECT 1 FROM permisos_efectivos
                    WHERE usuario_id = v_usr_a AND permiso_codigo = 'reportes.mi_dia') THEN
        RAISE EXCEPTION 'El Facturador no trae el reporte «Mi día».';
    END IF;
    IF EXISTS (SELECT 1 FROM permisos_efectivos
                WHERE usuario_id = v_usr_a AND permiso_codigo = 'reportes.tablero_ventas') THEN
        RAISE EXCEPTION 'FALLO GRAVE: el Facturador ve el tablero de ventas y no debería.';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

    IF (SELECT count(*) FROM rol_permiso
         WHERE rol_id = '11111111-1111-1111-1111-111111111111' AND permiso_codigo LIKE 'reportes.%') < 5 THEN
        RAISE EXCEPTION 'El Administrador no trae todos los reportes.';
    END IF;
END;

-- =========================================================================
RAISE NOTICE '35. Tipo de cambio por día: uno por fecha, y publicado exige venta (033)';
-- =========================================================================
BEGIN
    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.33', 'verificar.sql', false);

    INSERT INTO tipos_cambio (moneda, fecha, compra, venta, publicado)
    VALUES ('USD', date '2000-01-03', 3.500, 3.510, true);

    -- La misma fecha dos veces, no.
    BEGIN
        INSERT INTO tipos_cambio (moneda, fecha, venta, publicado) VALUES ('USD', date '2000-01-03', 3.6, true);
        RAISE EXCEPTION 'FALLO GRAVE: se guardaron dos tipos de cambio para el mismo día.';
    EXCEPTION
        WHEN unique_violation THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- «Publicado» sin venta, no.
    BEGIN
        INSERT INTO tipos_cambio (moneda, fecha, publicado) VALUES ('USD', date '2000-01-04', true);
        RAISE EXCEPTION 'FALLO GRAVE: se guardó un tipo de cambio publicado sin venta.';
    EXCEPTION
        WHEN check_violation THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- Un domingo sin publicación, sí.
    INSERT INTO tipos_cambio (moneda, fecha, publicado) VALUES ('USD', date '2000-01-02', false);

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
END;

-- =========================================================================
RAISE NOTICE '36. Reportes SUNAT y contabilidad: el Facturador solo ve los comprobantes con problema (034)';
-- =========================================================================
BEGIN
    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.34', 'verificar.sql', false);

    IF (SELECT count(*) FROM permisos WHERE area = 'reportes') < 10 THEN
        RAISE EXCEPTION 'Faltan permisos de reportes (034).';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM permisos_efectivos
                    WHERE usuario_id = v_usr_a AND permiso_codigo = 'reportes.con_problema') THEN
        RAISE EXCEPTION 'El Facturador no trae «Comprobantes con problema».';
    END IF;
    IF EXISTS (SELECT 1 FROM permisos_efectivos
                WHERE usuario_id = v_usr_a
                  AND permiso_codigo IN ('reportes.registro_ventas', 'reportes.descarga_masiva', 'reportes.resumen_igv')) THEN
        RAISE EXCEPTION 'FALLO GRAVE: el Facturador ve el Registro de Ventas, el IGV o la descarga masiva.';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
END;

-- =========================================================================
RAISE NOTICE '37. Roles propios: cada empresa los suyos; nunca con la llave del Administrador (035)';
-- =========================================================================
DECLARE
    v_rol uuid;
BEGIN
    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.35', 'verificar.sql', false);

    INSERT INTO roles (empresa_id, nombre, descripcion) VALUES (v_a, 'Caja 035', 'prueba')
    RETURNING id INTO v_rol;
    INSERT INTO rol_permiso (rol_id, permiso_codigo) VALUES (v_rol, 'reportes.mi_dia');

    -- La llave del Administrador, no.
    BEGIN
        INSERT INTO rol_permiso (rol_id, permiso_codigo) VALUES (v_rol, 'facturacion.credenciales_gestionar');
        RAISE EXCEPTION 'FALLO GRAVE: un rol propio recibió credenciales_gestionar.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO rol_permiso (rol_id, permiso_codigo) VALUES (v_rol, 'usuarios.roles_gestionar');
        RAISE EXCEPTION 'FALLO GRAVE: un rol propio recibió roles_gestionar.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- La empresa B no ve el rol de la A.
    PERFORM fijar_contexto(NULL, v_b, '10.0.0.35', 'verificar.sql', false);
    IF EXISTS (SELECT 1 FROM roles WHERE id = v_rol) THEN
        RAISE EXCEPTION 'FALLO GRAVE: la empresa B ve un rol propio de la A.';
    END IF;

    -- Y nadie de empresa toca un rol de la plataforma.
    BEGIN
        UPDATE roles SET nombre = 'Hackeado' WHERE id = '33333333-3333-3333-3333-333333333333';
        IF FOUND THEN RAISE EXCEPTION 'FALLO GRAVE: una empresa cambió un rol de la plataforma.'; END IF;
    EXCEPTION
        WHEN insufficient_privilege OR check_violation THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

    IF NOT EXISTS (SELECT 1 FROM rol_permiso WHERE rol_id = '44444444-4444-4444-4444-444444444444'
                                              AND permiso_codigo = 'reportes.registro_ventas') THEN
        RAISE EXCEPTION 'El rol Contabilidad no trae el Registro de Ventas.';
    END IF;
END;

-- =========================================================================
RAISE NOTICE '38. Reporte de la cartera: quien no tiene cartera no ve ninguna empresa (036)';
-- =========================================================================
BEGIN
    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.36', 'verificar.sql', false);
    IF reporte_cartera(current_date - 30, current_date)::json -> 'filas' IS NULL
       OR json_array_length(reporte_cartera(current_date - 30, current_date)::json -> 'filas') <> 0 THEN
        RAISE EXCEPTION 'FALLO GRAVE: un usuario sin cartera ve empresas en el reporte de la cartera.';
    END IF;
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
END;

-- =========================================================================
RAISE NOTICE '39. Roles ajustados: valen solo en su empresa; el Administrador no se ajusta (037)';
-- =========================================================================
DECLARE
    v_aj uuid;
BEGIN
    -- Beto (empresa B) también es Facturador, con el original.
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    INSERT INTO usuario_rol (usuario_id, rol_id) VALUES (v_usr_b, '22222222-2222-2222-2222-222222222222')
    ON CONFLICT DO NOTHING;

    -- La empresa A ajusta su Facturador: + anular, − emitir.
    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.37', 'verificar.sql', false);
    INSERT INTO roles (empresa_id, nombre, descripcion, basado_en)
    VALUES (v_a, 'Facturador', 'ajuste de prueba', '22222222-2222-2222-2222-222222222222')
    RETURNING id INTO v_aj;
    INSERT INTO rol_permiso (rol_id, permiso_codigo) VALUES (v_aj, 'facturacion.ver'), (v_aj, 'facturacion.anular');

    IF NOT EXISTS (SELECT 1 FROM permisos_efectivos WHERE usuario_id = v_usr_a AND permiso_codigo = 'facturacion.anular')
       OR EXISTS (SELECT 1 FROM permisos_efectivos WHERE usuario_id = v_usr_a AND permiso_codigo = 'facturacion.emitir') THEN
        RAISE EXCEPTION 'El ajuste del Facturador no se aplica en su empresa.';
    END IF;

    -- Dos ajustes del mismo rol en la misma empresa, no.
    BEGIN
        INSERT INTO roles (empresa_id, nombre, basado_en)
        VALUES (v_a, 'Facturador 2', '22222222-2222-2222-2222-222222222222');
        RAISE EXCEPTION 'FALLO GRAVE: dos ajustes activos del mismo rol en una empresa.';
    EXCEPTION
        WHEN unique_violation THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- El Administrador no se ajusta.
    BEGIN
        INSERT INTO roles (empresa_id, nombre, basado_en)
        VALUES (v_a, 'Administrador recortado', '11111111-1111-1111-1111-111111111111');
        RAISE EXCEPTION 'FALLO GRAVE: una empresa ajustó el rol Administrador.';
    EXCEPTION
        WHEN check_violation THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- Y un ajuste tampoco lleva la llave del Administrador (candado 035).
    BEGIN
        INSERT INTO rol_permiso (rol_id, permiso_codigo) VALUES (v_aj, 'facturacion.credenciales_gestionar');
        RAISE EXCEPTION 'FALLO GRAVE: un ajuste recibió credenciales_gestionar.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- La empresa B no ve el ajuste y su Facturador sigue igual que el original.
    PERFORM fijar_contexto(v_usr_b, v_b, '10.0.0.37', 'verificar.sql', false);
    IF EXISTS (SELECT 1 FROM roles WHERE id = v_aj) THEN
        RAISE EXCEPTION 'FALLO GRAVE: la empresa B ve el ajuste de la A.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM permisos_efectivos WHERE usuario_id = v_usr_b AND permiso_codigo = 'facturacion.emitir')
       OR EXISTS (SELECT 1 FROM permisos_efectivos WHERE usuario_id = v_usr_b AND permiso_codigo = 'facturacion.anular') THEN
        RAISE EXCEPTION 'FALLO GRAVE: el ajuste de la empresa A cambió el Facturador de la B.';
    END IF;

    -- «Volver al original»: se desactiva y la A vuelve a emitir.
    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.37', 'verificar.sql', false);
    UPDATE roles SET activo = false WHERE id = v_aj;
    IF NOT EXISTS (SELECT 1 FROM permisos_efectivos WHERE usuario_id = v_usr_a AND permiso_codigo = 'facturacion.emitir')
       OR EXISTS (SELECT 1 FROM permisos_efectivos WHERE usuario_id = v_usr_a AND permiso_codigo = 'facturacion.anular') THEN
        RAISE EXCEPTION 'Volver al original no devolvió el Facturador de la plataforma.';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
END;

-- =========================================================================
RAISE NOTICE '40. Recuperar el acceso: un enlace por minuto, solo la contraseña, y los correos fallidos solo para el super (038)';
-- =========================================================================
DECLARE
    v_viejo  uuid;
    v_nuevo  uuid;
    v_h1     bytea := decode(repeat('a1', 32), 'hex');
    v_h2     bytea := decode(repeat('b2', 32), 'hex');
    v_h3     bytea := decode(repeat('c3', 32), 'hex');
    v_prop   text;
    v_id     uuid;
    v_sec    bytea;
BEGIN
    -- Dos personas de la empresa A: una que ya entraba (con segundo factor) y
    -- una invitada que nunca usó su enlace.
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash, ultimo_ingreso_en, secreto_2fa, dosfa_activo)
    VALUES (v_a, 'vieja@empresa-a.pe', 'Vieja', 'pbkdf2$210000$vieja', now() - interval '2 days',
            decode('0102030405', 'hex'), true)
    RETURNING id INTO v_viejo;
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash)
    VALUES (v_a, 'nueva@empresa-a.pe', 'Nueva', 'pbkdf2$210000$sin-usar')
    RETURNING id INTO v_nuevo;

    -- Sin sesión, como en el login.
    PERFORM fijar_contexto(NULL, NULL, '10.0.0.40', 'verificar.sql', false);

    -- Un correo que no existe: nada que mandar.
    IF EXISTS (SELECT 1 FROM pedir_enlace_acceso('nadie@ningun-lado.pe', v_h3, now() + interval '48 hours', now() + interval '1 hour')) THEN
        RAISE EXCEPTION 'FALLO GRAVE: pedir_enlace_acceso respondió para un correo que no existe.';
    END IF;

    -- Quien ya entraba recibe un enlace SOLO de contraseña.
    SELECT proposito INTO v_prop
      FROM pedir_enlace_acceso('VIEJA@empresa-a.pe', v_h1, now() + interval '48 hours', now() + interval '1 hour');
    IF v_prop IS DISTINCT FROM 'restablecer_clave' THEN
        RAISE EXCEPTION 'Quien ya entraba debía recibir restablecer_clave y recibió %.', v_prop;
    END IF;

    -- El segundo pedido en el mismo minuto no manda nada.
    IF EXISTS (SELECT 1 FROM pedir_enlace_acceso('vieja@empresa-a.pe', v_h3, now() + interval '48 hours', now() + interval '1 hour')) THEN
        RAISE EXCEPTION 'FALLO GRAVE: dos enlaces en el mismo minuto.';
    END IF;

    -- Quien nunca entró recibe el enlace de siempre (contraseña y segundo factor).
    SELECT proposito INTO v_prop
      FROM pedir_enlace_acceso('nueva@empresa-a.pe', v_h2, now() + interval '48 hours', now() + interval '1 hour');
    IF v_prop IS DISTINCT FROM 'invitacion' THEN
        RAISE EXCEPTION 'Quien nunca entró debía recibir invitacion y recibió %.', v_prop;
    END IF;

    -- Un enlace de invitación NO sirve para cambiar solo la contraseña.
    IF consumir_restablecimiento(v_h2, 'pbkdf2$210000$otra', '10.0.0.40'::inet) IS NOT NULL THEN
        RAISE EXCEPTION 'FALLO GRAVE: consumir_restablecimiento aceptó un enlace de invitación.';
    END IF;

    -- El enlace bueno cambia la contraseña y deja el segundo factor como estaba.
    v_id := consumir_restablecimiento(v_h1, 'pbkdf2$210000$nueva', '10.0.0.40'::inet);
    IF v_id IS DISTINCT FROM v_viejo THEN
        RAISE EXCEPTION 'El enlace de restablecer no cambió la contraseña.';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    SELECT secreto_2fa INTO v_sec FROM usuarios WHERE id = v_viejo AND dosfa_activo AND clave_hash = 'pbkdf2$210000$nueva';
    IF v_sec IS DISTINCT FROM decode('0102030405', 'hex') THEN
        RAISE EXCEPTION 'FALLO GRAVE: restablecer la contraseña tocó el segundo factor.';
    END IF;

    -- Y el mismo enlace no sirve dos veces.
    PERFORM fijar_contexto(NULL, NULL, '10.0.0.40', 'verificar.sql', false);
    IF consumir_restablecimiento(v_h1, 'pbkdf2$210000$tercera', '10.0.0.40'::inet) IS NOT NULL THEN
        RAISE EXCEPTION 'FALLO GRAVE: un enlace de restablecer sirvió dos veces.';
    END IF;

    -- Los correos fallidos: se anotan sin sesión, pero nadie sin sesión los lee.
    PERFORM anotar_correo('nueva@empresa-a.pe', 'invitacion', v_nuevo, false, 'Gmail rechazó la contraseña');
    PERFORM anotar_correo('nueva@empresa-a.pe', 'invitacion', v_nuevo, false, 'Gmail rechazó la contraseña');
    BEGIN
        PERFORM count(*) FROM correos_fallidos;
        RAISE EXCEPTION 'FALLO GRAVE: la aplicación lee correos_fallidos sin pasar por las funciones.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;
    IF correos_fallidos_abiertos() IS NOT NULL THEN
        RAISE EXCEPTION 'FALLO GRAVE: alguien que no es super administrador ve los correos fallidos.';
    END IF;

    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.40', 'verificar.sql', false);
    IF correos_fallidos_abiertos() IS NOT NULL THEN
        RAISE EXCEPTION 'FALLO GRAVE: un usuario de empresa ve los correos fallidos.';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF (correos_fallidos_abiertos()::json -> 0 ->> 'intentos')::int IS DISTINCT FROM 2 THEN
        RAISE EXCEPTION 'El segundo fallo al mismo destino debía sumar un intento.';
    END IF;

    -- Cuando sale bien, se resuelve.
    PERFORM anotar_correo('NUEVA@empresa-a.pe', 'invitacion', v_nuevo, true, NULL);
    IF json_array_length(correos_fallidos_abiertos()::json) <> 0 THEN
        RAISE EXCEPTION 'Un correo que salió bien sigue en la lista de fallidos.';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
END;

-- =========================================================================
RAISE NOTICE '41. Recuperar el segundo factor: confirmar por correo, espera, cierre de sesiones y quién la cancela (039)';
-- =========================================================================
DECLARE
    v_per    uuid;     -- la persona que perdió el teléfono (empresa A)
    v_adm    uuid;     -- un administrador de la empresa A
    v_s1     uuid;     -- dos super administradores
    v_s2     uuid;
    v_ses    uuid;
    v_h1     bytea := decode(repeat('d4', 32), 'hex');
    v_h2     bytea := decode(repeat('e5', 32), 'hex');
    v_h3     bytea := decode(repeat('f6', 32), 'hex');
    v_r      json;
    v_id     uuid;
BEGIN
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash, secreto_2fa, dosfa_activo)
    VALUES (v_a, 'perdio@empresa-a.pe', 'Perdió', 'pbkdf2$210000$p', decode('0a0b0c', 'hex'), true)
    RETURNING id INTO v_per;
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash, secreto_2fa, dosfa_activo)
    VALUES (v_a, 'jefa@empresa-a.pe', 'Jefa', 'pbkdf2$210000$j', decode('0d0e0f', 'hex'), true)
    RETURNING id INTO v_adm;
    INSERT INTO usuario_rol (usuario_id, rol_id) VALUES (v_adm, '11111111-1111-1111-1111-111111111111');
    INSERT INTO sesiones (usuario_id, empresa_id, token_hash, expira_en, dosfa_superado)
    VALUES (v_per, v_a, decode(repeat('41', 32), 'hex'), now() + interval '8 hours', true)
    RETURNING id INTO v_ses;

    -- Con la sesión a medias (ya puso su contraseña), pide recuperar.
    PERFORM fijar_contexto(v_per, v_a, '10.0.0.41', 'verificar.sql', false);
    v_r := pedir_recuperacion_2fa(v_h1, now() + interval '1 hour')::json;
    IF v_r->>'resultado' <> 'pedida' OR v_r->'recuperacion'->>'nivel' <> 'usuario'
       OR (v_r->'recuperacion'->>'horasEspera')::int <> 24 THEN
        RAISE EXCEPTION 'Un usuario debía pedir con 24 h de espera (%).', v_r;
    END IF;
    IF NOT (v_r->'recuperacion'->'avisados')::jsonb ? 'jefa@empresa-a.pe' THEN
        RAISE EXCEPTION 'La administradora de su empresa debía recibir el aviso (%).', v_r;
    END IF;
    v_id := (v_r->'recuperacion'->>'id')::uuid;

    -- Un segundo pedido en el mismo minuto no manda otro correo.
    IF pedir_recuperacion_2fa(v_h3, now() + interval '1 hour')::json->>'resultado' <> 'espera' THEN
        RAISE EXCEPTION 'FALLO GRAVE: dos correos de recuperación en el mismo minuto.';
    END IF;

    -- Nadie lee la tabla directamente.
    BEGIN
        PERFORM count(*) FROM recuperaciones_2fa;
        RAISE EXCEPTION 'FALLO GRAVE: la aplicación lee recuperaciones_2fa sin pasar por las funciones.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- Sin confirmar por correo, no hay espera que completar.
    IF completar_recuperacion_2fa(NULL) IS NOT NULL THEN
        RAISE EXCEPTION 'FALLO GRAVE: se completó una recuperación que nadie confirmó por correo.';
    END IF;

    -- El enlace del correo, sin sesión. Uno falso no sirve.
    PERFORM fijar_contexto(NULL, NULL, '10.0.0.41', 'verificar.sql', false);
    IF confirmar_recuperacion_2fa(v_h2, '10.0.0.41'::inet) IS NOT NULL THEN
        RAISE EXCEPTION 'FALLO GRAVE: un enlace que no existe confirmó una recuperación.';
    END IF;
    v_r := confirmar_recuperacion_2fa(v_h1, '10.0.0.41'::inet)::json;
    IF v_r->>'estado' <> 'en_espera' OR (v_r->>'disponibleEn')::timestamptz < now() + interval '23 hours' THEN
        RAISE EXCEPTION 'Al confirmar debía empezar una espera de 24 h (%).', v_r;
    END IF;
    IF confirmar_recuperacion_2fa(v_h1, '10.0.0.41'::inet) IS NOT NULL THEN
        RAISE EXCEPTION 'FALLO GRAVE: el enlace de recuperación sirvió dos veces.';
    END IF;

    -- Al confirmar se cerraron TODAS sus sesiones (la laptop robada queda fuera).
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF EXISTS (SELECT 1 FROM sesiones WHERE usuario_id = v_per AND revocada_en IS NULL) THEN
        RAISE EXCEPTION 'FALLO GRAVE: confirmar la recuperación no cerró sus sesiones.';
    END IF;

    -- Durante la espera no se puede completar, y pedir otra vez no reinicia nada.
    PERFORM fijar_contexto(v_per, v_a, '10.0.0.41', 'verificar.sql', false);
    IF completar_recuperacion_2fa(NULL) IS NOT NULL THEN
        RAISE EXCEPTION 'FALLO GRAVE: se completó la recuperación antes de la espera.';
    END IF;
    IF pedir_recuperacion_2fa(v_h3, now() + interval '1 hour')::json->>'resultado' <> 'en_curso' THEN
        RAISE EXCEPTION 'Con una recuperación en espera, pedir otra debía decir en_curso.';
    END IF;
    IF mi_recuperacion_2fa()::json->>'estado' <> 'en_espera' THEN
        RAISE EXCEPTION 'La persona debía ver su recuperación en espera.';
    END IF;
    -- Y el segundo factor sigue intacto.
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF NOT EXISTS (SELECT 1 FROM usuarios WHERE id = v_per AND dosfa_activo AND secreto_2fa = decode('0a0b0c', 'hex')) THEN
        RAISE EXCEPTION 'FALLO GRAVE: el segundo factor cambió durante la espera.';
    END IF;

    -- Alguien de OTRA empresa ni la ve ni la cancela.
    PERFORM fijar_contexto(v_usr_b, v_b, '10.0.0.41', 'verificar.sql', false);
    IF recuperaciones_2fa_en_curso()::jsonb @> jsonb_build_array(jsonb_build_object('id', v_id)) THEN
        RAISE EXCEPTION 'FALLO GRAVE: otra empresa ve la recuperación.';
    END IF;
    IF cancelar_recuperacion_2fa(v_id) IS NOT NULL THEN
        RAISE EXCEPTION 'FALLO GRAVE: otra empresa canceló la recuperación.';
    END IF;

    -- La administradora de su empresa la ve.
    PERFORM fijar_contexto(v_adm, v_a, '10.0.0.41', 'verificar.sql', false);
    IF NOT recuperaciones_2fa_en_curso()::jsonb @> jsonb_build_array(jsonb_build_object('id', v_id)) THEN
        RAISE EXCEPTION 'La administradora de la empresa debía ver la recuperación.';
    END IF;

    -- Si entra con su teléfono, se cancela sola (quien la pidió no era ella).
    PERFORM fijar_contexto(v_per, v_a, '10.0.0.41', 'verificar.sql', false);
    IF cancelar_mi_recuperacion_2fa()::json->>'estado' <> 'cancelada' THEN
        RAISE EXCEPTION 'Entrar con el segundo factor debía cancelar la recuperación.';
    END IF;
    IF mi_recuperacion_2fa() IS NOT NULL THEN
        RAISE EXCEPTION 'La recuperación cancelada sigue en curso.';
    END IF;

    -- Una nueva, y esta vez la cancela la administradora.
    v_r := pedir_recuperacion_2fa(v_h2, now() + interval '1 hour')::json;
    IF v_r->>'resultado' <> 'pedida' THEN
        RAISE EXCEPTION 'Tras cancelar, debía poder pedir otra (%).', v_r;
    END IF;
    v_id := (v_r->'recuperacion'->>'id')::uuid;
    PERFORM fijar_contexto(v_adm, v_a, '10.0.0.41', 'verificar.sql', false);
    IF cancelar_recuperacion_2fa(v_id)::json->>'estado' <> 'cancelada' THEN
        RAISE EXCEPTION 'La administradora de la empresa debía poder cancelarla.';
    END IF;

    -- La administradora espera 72 h, y avisan también a los super administradores.
    v_r := pedir_recuperacion_2fa(v_h3, now() + interval '1 hour')::json;
    IF v_r->'recuperacion'->>'nivel' <> 'administrador' OR (v_r->'recuperacion'->>'horasEspera')::int <> 72 THEN
        RAISE EXCEPTION 'Un administrador debía esperar 72 h (%).', v_r;
    END IF;
    IF NOT (v_r->'recuperacion'->'avisados')::jsonb ? 'super.029@correo.pe' THEN
        RAISE EXCEPTION 'Los super administradores debían recibir el aviso de un administrador (%).', v_r;
    END IF;
    PERFORM cancelar_mi_recuperacion_2fa();

    -- Un super administrador solo no puede: hace falta otro que la pueda cancelar.
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    UPDATE usuarios SET dosfa_activo = false WHERE es_super_admin AND dosfa_activo;
    INSERT INTO usuarios (correo, nombre, clave_hash, es_super_admin, secreto_2fa, dosfa_activo)
    VALUES ('super1.039@correo.pe', 'Super 1', 'x', true, decode('01', 'hex'), true) RETURNING id INTO v_s1;
    PERFORM fijar_contexto(v_s1, NULL, '10.0.0.41', 'verificar.sql', false);
    IF pedir_recuperacion_2fa(decode(repeat('a7', 32), 'hex'), now() + interval '1 hour')::json->>'resultado' <> 'no_permitida' THEN
        RAISE EXCEPTION 'FALLO GRAVE: el único super administrador pudo recuperar su segundo factor solo.';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    INSERT INTO usuarios (correo, nombre, clave_hash, es_super_admin, secreto_2fa, dosfa_activo)
    VALUES ('super2.039@correo.pe', 'Super 2', 'x', true, decode('02', 'hex'), true) RETURNING id INTO v_s2;
    PERFORM fijar_contexto(v_s1, NULL, '10.0.0.41', 'verificar.sql', false);
    v_r := pedir_recuperacion_2fa(decode(repeat('a7', 32), 'hex'), now() + interval '1 hour')::json;
    IF v_r->'recuperacion'->>'nivel' <> 'super' OR (v_r->'recuperacion'->>'horasEspera')::int <> 168 THEN
        RAISE EXCEPTION 'Con otro super administrador, debía esperar 7 días (%).', v_r;
    END IF;

    -- Y el otro super administrador la ve y la cancela.
    PERFORM fijar_contexto(v_s2, NULL, '10.0.0.41', 'verificar.sql', true);
    IF cancelar_recuperacion_2fa((v_r->'recuperacion'->>'id')::uuid)::json->>'estado' <> 'cancelada' THEN
        RAISE EXCEPTION 'El otro super administrador debía poder cancelarla.';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
END;

-- =========================================================================
RAISE NOTICE '42. Facturas: contado o crédito con cuotas, y la detracción solo en facturas (040, 054)';
-- =========================================================================
DECLARE
    v_f  uuid;
    v_r  text;
BEGIN
    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.42', 'verificar.sql', false);

    -- Lo de siempre sigue igual: al contado y sin detracción.
    INSERT INTO documentos (empresa_id, tipo, cliente_id, fecha_emision, moneda)
    VALUES (v_a, '01', v_cli_a, current_date, 'PEN')
    RETURNING id INTO v_f;
    SELECT forma_pago INTO v_r FROM documentos WHERE id = v_f;
    IF v_r IS DISTINCT FROM 'contado' THEN
        RAISE EXCEPTION 'Un documento nuevo debía quedar al contado y quedó %.', v_r;
    END IF;

    -- A crédito, con cuotas y detracción: se guarda.
    UPDATE documentos
       SET forma_pago = 'credito',
           cuotas = '[{"numero":1,"monto":831,"vence":"2026-12-31"}]',
           detraccion = '{"codigo":"022","porcentaje":12,"cuenta":"00-000-123456","medioPago":"001"}'
     WHERE id = v_f;

    -- Cuotas al contado: no (error 3252 de SUNAT).
    BEGIN
        UPDATE documentos SET forma_pago = 'contado' WHERE id = v_f;
        RAISE EXCEPTION 'FALLO GRAVE: se guardó una factura al contado con cuotas.';
    EXCEPTION
        WHEN check_violation THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- Una forma de pago inventada: no.
    BEGIN
        UPDATE documentos SET forma_pago = 'fiado' WHERE id = v_f;
        RAISE EXCEPTION 'FALLO GRAVE: se aceptó una forma de pago que no existe.';
    EXCEPTION
        WHEN check_violation THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- Detracción en una boleta: no.
    BEGIN
        INSERT INTO documentos (empresa_id, tipo, cliente_id, fecha_emision, moneda, detraccion)
        VALUES (v_a, '03', v_cli_a, current_date, 'PEN',
                '{"codigo":"022","porcentaje":12,"cuenta":"00-000-123456","medioPago":"001"}');
        RAISE EXCEPTION 'FALLO GRAVE: se guardó una boleta con detracción.';
    EXCEPTION
        WHEN check_violation THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- Un código de detracción que no es del catálogo 54 (tres dígitos): no.
    BEGIN
        UPDATE documentos SET detraccion = '{"codigo":"22","porcentaje":12}' WHERE id = v_f;
        RAISE EXCEPTION 'FALLO GRAVE: se aceptó un código de detracción mal formado.';
    EXCEPTION
        WHEN check_violation THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- La empresa recuerda su cuenta de detracciones; una cuenta con letras, no.
    UPDATE empresas SET detraccion_cuenta = '00-000-123456', detraccion_codigo = '022',
                        detraccion_porcentaje = 12
     WHERE id = v_a;
    BEGIN
        UPDATE empresas SET detraccion_cuenta = 'ABC-123' WHERE id = v_a;
        RAISE EXCEPTION 'FALLO GRAVE: se guardó una cuenta de detracciones con letras.';
    EXCEPTION
        WHEN check_violation THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- 054: también el medio de pago habitual (catálogo 59, tres dígitos).
    UPDATE empresas SET detraccion_medio_pago = '003' WHERE id = v_a;
    BEGIN
        UPDATE empresas SET detraccion_medio_pago = 'TR' WHERE id = v_a;
        RAISE EXCEPTION 'FALLO GRAVE: se guardó un medio de pago de detracción mal formado.';
    EXCEPTION
        WHEN check_violation THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- Y la empresa B no ve la cuenta de la A.
    PERFORM fijar_contexto(v_usr_b, v_b, '10.0.0.42', 'verificar.sql', false);
    IF EXISTS (SELECT 1 FROM empresas WHERE detraccion_cuenta = '00-000-123456') THEN
        RAISE EXCEPTION 'FALLO GRAVE: la empresa B ve la cuenta de detracciones de la A.';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
END;

-- =========================================================================
RAISE NOTICE '43. «Ver como» al dueño de una cuenta: se le ve, pero no se le cambia (041)';
-- =========================================================================
DECLARE
    v_cta   uuid;
    v_emp   uuid;
    v_dueno uuid;
    v_sup   uuid;
    v_ses   uuid;
    v_ajena uuid;
BEGIN
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    INSERT INTO cuentas (tipo, titular_tipo, titular_documento, titular_nombre, estado, confirmada_en, origen)
    VALUES ('negocio', 'natural', '43434343', 'Dueña 041', 'activa', now(), 'registro')
    RETURNING id INTO v_cta;
    INSERT INTO empresas (nombre, ruc, razon_social, cuenta_id)
    VALUES ('Bodega 041', '20777777741', 'BODEGA 041', v_cta) RETURNING id INTO v_emp;
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta)
    VALUES (v_cta, 'duena.041@correo.pe', 'Dueña 041', 'x', 'dueno') RETURNING id INTO v_dueno;
    INSERT INTO usuarios (correo, nombre, clave_hash, es_super_admin)
    VALUES ('super.041@correo.pe', 'Super 041', 'x', true) RETURNING id INTO v_sup;

    -- La sesión del super, mirando a la dueña.
    INSERT INTO sesiones (usuario_id, token_hash, expira_en, dosfa_superado,
                          suplantando_usuario_id, suplantacion_inicio)
    VALUES (v_sup, decode(repeat('43', 32), 'hex'), now() + interval '1 hour', true, v_dueno, now())
    RETURNING id INTO v_ses;
    -- Y una sesión cualquiera de otra persona.
    INSERT INTO sesiones (usuario_id, token_hash, expira_en, dosfa_superado)
    VALUES (v_usr_a, decode(repeat('44', 32), 'hex'), now() + interval '1 hour', true)
    RETURNING id INTO v_ajena;

    -- Como lo deja el middleware: el autor (super) sin poderes, en la empresa de ella.
    PERFORM fijar_contexto(v_sup, v_emp, '10.0.0.43', 'verificar.sql', false);

    IF EXISTS (SELECT 1 FROM usuarios WHERE id = v_dueno) THEN
        RAISE EXCEPTION 'Sin fijar_suplantado la dueña ya se veía: la prueba no prueba nada.';
    END IF;

    -- Una sesión que no es la suya no sirve para mirar a nadie.
    IF fijar_suplantado(v_ajena) THEN
        RAISE EXCEPTION 'FALLO GRAVE: fijar_suplantado aceptó una sesión ajena.';
    END IF;
    IF EXISTS (SELECT 1 FROM usuarios WHERE id = v_dueno) THEN
        RAISE EXCEPTION 'FALLO GRAVE: con una sesión ajena se ve a la dueña.';
    END IF;

    -- Con la suya, sí: la persona, sus roles y su cuenta.
    IF NOT fijar_suplantado(v_ses) THEN
        RAISE EXCEPTION 'fijar_suplantado no aceptó la sesión de «ver como».';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM usuarios WHERE id = v_dueno) THEN
        RAISE EXCEPTION 'Con «ver como» la dueña de la cuenta sigue invisible (era el 401).';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM cuentas WHERE id = v_cta) THEN
        RAISE EXCEPTION 'Con «ver como» no se ve la cuenta de la dueña.';
    END IF;

    -- Pero no se la cambia.
    BEGIN
        UPDATE usuarios SET nombre = 'Cambiada' WHERE id = v_dueno;
        RAISE EXCEPTION 'FALLO GRAVE: con «ver como» se cambió a la dueña de la cuenta.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
        WHEN check_violation THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    -- Terminada la suplantación, ya no.
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    UPDATE sesiones SET revocada_en = now(), motivo_revocacion = 'verificar' WHERE id = v_ses;
    PERFORM fijar_contexto(v_sup, v_emp, '10.0.0.43', 'verificar.sql', false);
    IF fijar_suplantado(v_ses) THEN
        RAISE EXCEPTION 'FALLO GRAVE: una sesión cerrada sigue sirviendo para «ver como».';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
END;

-- =========================================================================
RAISE NOTICE '44. Consultas: modo SUNAT por defecto, solo con permiso se cambia, y el padrón no se lee directo (042)';
-- =========================================================================
BEGIN
    IF consultas_modo() <> 'sunat' THEN
        RAISE EXCEPTION 'El modo de consultas debía empezar en «sunat» (sin costo).';
    END IF;

    -- Un usuario de empresa no cambia el modo.
    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.44', 'verificar.sql', false);
    IF fijar_consultas_modo('decolecta') <> 'sin_permiso' THEN
        RAISE EXCEPTION 'FALLO GRAVE: un usuario de empresa cambió el modo de consultas.';
    END IF;
    IF consultas_modo() <> 'sunat' THEN
        RAISE EXCEPTION 'FALLO GRAVE: el modo cambió sin permiso.';
    END IF;

    -- El padrón no se lee directo: solo por padron_consultar.
    BEGIN
        PERFORM count(*) FROM referencia.padron_ruc_a;
        RAISE EXCEPTION 'FALLO GRAVE: la aplicación lee el padrón sin pasar por la función.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;
    IF padron_consultar('1234') IS NOT NULL OR padron_consultar('20100070970'' OR 1=1') IS NOT NULL THEN
        RAISE EXCEPTION 'padron_consultar aceptó algo que no es un RUC.';
    END IF;

    -- El super administrador sí, y vuelve a dejarlo como estaba.
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF fijar_consultas_modo('decolecta') <> 'ok' THEN
        RAISE EXCEPTION 'El super administrador no pudo pasar a modo Decolecta.';
    END IF;
    IF consultas_modo() <> 'decolecta' THEN
        RAISE EXCEPTION 'El modo no quedó en Decolecta.';
    END IF;
    IF fijar_consultas_modo('gratis') <> 'modo_invalido' THEN
        RAISE EXCEPTION 'Se aceptó un modo que no existe.';
    END IF;
    PERFORM fijar_consultas_modo('sunat');

    -- El estado se puede leer sin sesión (lo enseña el panel del servicio).
    PERFORM fijar_contexto(NULL, NULL, '10.0.0.44', 'verificar.sql', false);
    IF (padron_estado()::json ->> 'modo') IS DISTINCT FROM 'sunat' THEN
        RAISE EXCEPTION 'padron_estado no devolvió el modo.';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
END;

-- =========================================================================
RAISE NOTICE '45. WhatsApp: número con forma, sin número no se activa, estados válidos (043)';
-- =========================================================================
PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.45', 'verificar.sql', false);
BEGIN
    UPDATE clientes SET whatsapp = '51987654321', whatsapp_activo = true WHERE id = v_cli_a;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'FALLO: la empresa no pudo guardar el WhatsApp de su cliente.';
    END IF;

    BEGIN
        UPDATE clientes SET whatsapp = '+51 987 654 321' WHERE id = v_cli_a;
        RAISE EXCEPTION 'FALLO: se guardó un WhatsApp con espacios y signos.';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    BEGIN
        UPDATE clientes SET whatsapp = NULL WHERE id = v_cli_a;   -- sigue activo
        RAISE EXCEPTION 'FALLO: quedó «enviar por WhatsApp» activado sin número.';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    BEGIN
        UPDATE documentos SET whatsapp_estado = 'quizas' WHERE empresa_id = v_a;
        IF FOUND THEN
            RAISE EXCEPTION 'FALLO: se guardó un estado de WhatsApp que no existe.';
        END IF;
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    UPDATE clientes SET whatsapp = NULL, whatsapp_activo = false WHERE id = v_cli_a;
END;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

-- =========================================================================
RAISE NOTICE '46. Sucursales: código único por empresa, la 0000 no se elimina, cada empresa ve solo las suyas (044)';
-- =========================================================================
PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.46', 'verificar.sql', false);
BEGIN
    INSERT INTO sucursales (empresa_id, codigo, nombre) VALUES (v_a, '0000', 'Domicilio fiscal');
    INSERT INTO sucursales (empresa_id, codigo, nombre, direccion) VALUES (v_a, '0001', 'Tienda Norte', 'Av. Tupac Amaru 1820');

    BEGIN
        INSERT INTO sucursales (empresa_id, codigo, nombre) VALUES (v_a, '0001', 'Otra');
        RAISE EXCEPTION 'FALLO: se repitió el código de establecimiento 0001 en la misma empresa.';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;

    BEGIN
        INSERT INTO sucursales (empresa_id, codigo, nombre) VALUES (v_a, '0002', 'tienda norte ');
        RAISE EXCEPTION 'FALLO: se repitió el nombre de una sucursal activa.';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;

    BEGIN
        INSERT INTO sucursales (empresa_id, codigo, nombre) VALUES (v_a, '12A4', 'Mal');
        RAISE EXCEPTION 'FALLO: se aceptó un código que no son 4 dígitos.';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    BEGIN
        UPDATE sucursales SET activo = false, eliminada_en = now() WHERE empresa_id = v_a AND codigo = '0000';
        RAISE EXCEPTION 'FALLO: se eliminó la sucursal 0000 (domicilio fiscal).';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    -- Eliminada la 0001, su código se puede volver a usar.
    UPDATE sucursales SET activo = false, eliminada_en = now() WHERE empresa_id = v_a AND codigo = '0001';
    INSERT INTO sucursales (empresa_id, codigo, nombre) VALUES (v_a, '0001', 'Tienda Norte');

    -- Otra empresa no las ve.
    PERFORM fijar_contexto(v_usr_b, v_b, '10.0.0.46', 'verificar.sql', false);
    SELECT count(*) INTO v_n FROM sucursales WHERE empresa_id = v_a;
    IF v_n > 0 THEN
        RAISE EXCEPTION 'FALLO GRAVE: una empresa ve las sucursales de otra.';
    END IF;
END;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

-- =========================================================================
RAISE NOTICE '47. Padrón de locales anexos: carga aparte del de RUC, se lee solo por función y da los locales de un RUC (045)';
-- =========================================================================
-- Sirve igual con el padrón real ya cargado: usa la tabla que le toque y,
-- como todo verificar.sql, se deshace al final (no toca el padrón en uso).
PERFORM fijar_contexto(NULL, NULL, '10.0.0.47', 'verificar.sql', false);
BEGIN
    -- La tabla no se lee directo.
    BEGIN
        PERFORM count(*) FROM referencia.anexos_a;
        RAISE EXCEPTION 'FALLO GRAVE: la aplicación lee el padrón de anexos sin pasar por la función.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;
    IF anexos_consultar('2010007097'' OR 1=1')::json -> 'locales' ->> 0 IS NOT NULL THEN
        RAISE EXCEPTION 'anexos_consultar aceptó algo que no es un RUC.';
    END IF;

    IF (padron_estado()::json -> 'anexos' ->> 'enCurso')::boolean THEN
        RAISE NOTICE '    (hay una carga real del padrón de anexos en curso: se salta la prueba de carga)';
    ELSE
        -- Una carga que no llega al mínimo no pasa a usarse.
        v_txt := anexos_preparar_carga('manual', NULL, 'prueba.zip', 100);
        IF v_txt::json ->> 'tabla' NOT IN ('anexos_a', 'anexos_b') THEN
            RAISE EXCEPTION 'anexos_preparar_carga devolvió algo raro: %', v_txt;
        END IF;
        IF (anexos_preparar_carga('manual', NULL, 'otra.zip', 100)::json ->> 'error') IS DISTINCT FROM 'en_curso' THEN
            RAISE EXCEPTION 'Se aceptaron dos cargas de anexos a la vez.';
        END IF;

        -- Una carga de anexos en curso NO bloquea la del padrón de RUC.
        IF NOT (padron_estado()::json ->> 'enCurso')::boolean
           AND (padron_preparar_carga('manual', NULL, 'ruc.zip', 100)::json ->> 'error') IS NOT NULL THEN
            RAISE EXCEPTION 'Una carga de anexos bloqueó la del padrón de RUC.';
        END IF;

        EXECUTE format('INSERT INTO referencia.%I (ruc, ubigeo, direccion) VALUES
            (''20100070970'', ''150110'', ''AV. TUPAC AMARU NRO. 1820''),
            (''20100070970'', ''150103'', ''CAL. LOS RUBIES NRO. 330''),
            (''20999999999'', ''150101'', ''JR. OTRO NRO. 1'')', v_txt::json ->> 'tabla');
        IF anexos_terminar_carga((v_txt::json ->> 'id')::uuid, 0, 1000) = 'ok' THEN
            RAISE EXCEPTION 'Se aceptó una carga de anexos con menos filas que el mínimo.';
        END IF;

        -- Una buena sí.
        v_txt := anexos_preparar_carga('manual', NULL, 'prueba.zip', 100);
        EXECUTE format('INSERT INTO referencia.%I (ruc, ubigeo, direccion) VALUES
            (''20100070970'', ''150110'', ''AV. TUPAC AMARU NRO. 1820''),
            (''20100070970'', ''150103'', ''CAL. LOS RUBIES NRO. 330''),
            (''20999999999'', ''150101'', ''JR. OTRO NRO. 1'')', v_txt::json ->> 'tabla');
        IF anexos_terminar_carga((v_txt::json ->> 'id')::uuid, 0, 2) <> 'ok' THEN
            RAISE EXCEPTION 'No se terminó una carga de anexos buena.';
        END IF;

        v_txt := anexos_consultar('20100070970');
        IF NOT (v_txt::json ->> 'cargado')::boolean OR json_array_length(v_txt::json -> 'locales') <> 2 THEN
            RAISE EXCEPTION 'anexos_consultar no dio los 2 locales del RUC: %', v_txt;
        END IF;

        -- El estado trae los dos bloques, cada uno con sus cargas.
        v_txt := padron_estado();
        IF json_array_length(v_txt::json -> 'anexos' -> 'cargas') < 2
           OR (v_txt::json -> 'anexos' -> 'enUso' ->> 'filas')::int <> 3 THEN
            RAISE EXCEPTION 'padron_estado no trae bien el bloque de anexos: %', v_txt;
        END IF;
        IF EXISTS (SELECT 1 FROM json_array_elements(v_txt::json -> 'cargas') c WHERE c ->> 'archivo' = 'prueba.zip') THEN
            RAISE EXCEPTION 'Las cargas de anexos aparecen entre las del padrón de RUC.';
        END IF;
    END IF;
END;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

-- =========================================================================
RAISE NOTICE '48. Alcance: cada quien ve sus comprobantes (su sucursal, solo los suyos) y la sucursal elegida arriba recorta (046)';
-- =========================================================================
-- Sigue de la 46: la empresa A tiene la 0000 y la 0001 «Tienda Norte».
PERFORM fijar_contexto(NULL, NULL, '10.0.0.48', 'verificar.sql', true);
DECLARE
    v_s0    uuid;
    v_s1    uuid;
    v_jefe  uuid;
    v_caja  uuid;
    v_d0    uuid;
    v_d1    uuid;
    v_d2    uuid;
    v_js    json;
BEGIN
    SELECT id INTO v_s0 FROM sucursales WHERE empresa_id = v_a AND codigo = '0000' AND eliminada_en IS NULL;
    SELECT id INTO v_s1 FROM sucursales WHERE empresa_id = v_a AND codigo = '0001' AND eliminada_en IS NULL;

    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash)
    VALUES (v_a, 'jefe48@empresa-a.pe', 'Jefe 48', 'pbkdf2$210000$j') RETURNING id INTO v_jefe;
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash)
    VALUES (v_a, 'caja48@empresa-a.pe', 'Caja 48', 'pbkdf2$210000$c') RETURNING id INTO v_caja;
    INSERT INTO usuario_rol (usuario_id, rol_id) VALUES (v_jefe, '55555555-5555-5555-5555-555555555555'),
                                                       (v_caja, '22222222-2222-2222-2222-222222222222');

    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.48', 'verificar.sql', false);
    INSERT INTO usuario_alcance (usuario_id, empresa_id, alcance, sucursal_defecto_id)
    VALUES (v_jefe, v_a, 'sucursal', v_s1), (v_caja, v_a, 'propio', v_s1);
    INSERT INTO usuario_sucursal (empresa_id, usuario_id, sucursal_id) VALUES (v_a, v_jefe, v_s1), (v_a, v_caja, v_s1);

    -- Uno de antes (sin sucursal = la 0000) y uno de la 0001.
    INSERT INTO documentos (empresa_id, tipo, cliente_id, fecha_emision, moneda)
    VALUES (v_a, '01', v_cli_a, current_date, 'PEN') RETURNING id INTO v_d0;
    INSERT INTO documentos (empresa_id, tipo, cliente_id, fecha_emision, moneda, sucursal_id)
    VALUES (v_a, '03', v_cli_a, current_date, 'PEN', v_s1) RETURNING id INTO v_d1;

    -- El jefe de la 0001 ve el de la 0001 y no el de la 0000.
    PERFORM fijar_contexto(v_jefe, v_a, '10.0.0.48', 'verificar.sql', false);
    v_js := fijar_alcance(v_jefe, v_a, NULL)::json;
    IF v_js ->> 'alcance' <> 'sucursal' OR (v_js ->> 'vista')::uuid IS DISTINCT FROM v_s1 THEN
        RAISE EXCEPTION 'fijar_alcance no dio la sucursal del jefe: %', v_js;
    END IF;
    SELECT count(*) INTO v_n FROM documentos WHERE id IN (v_d0, v_d1);
    IF v_n <> 1 OR NOT EXISTS (SELECT 1 FROM documentos WHERE id = v_d1) THEN
        RAISE EXCEPTION 'FALLO GRAVE: el jefe de la 0001 ve % de los 2 (debía ver solo el suyo).', v_n;
    END IF;
    -- Pedir arriba una sucursal que no es suya no sirve: se queda en la suya.
    v_js := fijar_alcance(v_jefe, v_a, v_s0)::json;
    IF (v_js ->> 'vista')::uuid IS DISTINCT FROM v_s1 OR EXISTS (SELECT 1 FROM documentos WHERE id = v_d0) THEN
        RAISE EXCEPTION 'FALLO GRAVE: el jefe pudo elegir arriba una sucursal ajena.';
    END IF;
    -- Lo que emite en su sucursal lo ve (la base deja escribirlo y leerlo).
    INSERT INTO documentos (empresa_id, tipo, cliente_id, fecha_emision, moneda, sucursal_id)
    VALUES (v_a, '03', v_cli_a, current_date, 'PEN', v_s1) RETURNING id INTO v_d2;

    -- La cajera «solo lo suyo»: ve el que creó ella y ninguno más.
    PERFORM fijar_contexto(v_caja, v_a, '10.0.0.48', 'verificar.sql', false);
    PERFORM fijar_alcance(v_caja, v_a, NULL);
    SELECT count(*) INTO v_n FROM documentos WHERE id IN (v_d0, v_d1, v_d2);
    IF v_n <> 0 THEN
        RAISE EXCEPTION 'FALLO GRAVE: «solo lo suyo» ve % comprobantes de otros.', v_n;
    END IF;
    INSERT INTO documentos (empresa_id, tipo, cliente_id, fecha_emision, moneda, sucursal_id)
    VALUES (v_a, '03', v_cli_a, current_date, 'PEN', v_s1) RETURNING id INTO v_doc;
    IF NOT EXISTS (SELECT 1 FROM documentos WHERE id = v_doc) THEN
        RAISE EXCEPTION '«Solo lo suyo» no ve lo que acaba de emitir.';
    END IF;

    -- El administrador (sin fila = toda la empresa): «Todas» ve los dos; con
    -- la 0001 elegida arriba, solo el de la 0001.
    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.48', 'verificar.sql', false);
    v_js := fijar_alcance(v_usr_a, v_a, NULL)::json;
    IF v_js ->> 'alcance' <> 'empresa' OR v_js ->> 'vista' IS NOT NULL THEN
        RAISE EXCEPTION 'Toda la empresa no entra con «Todas»: %', v_js;
    END IF;
    SELECT count(*) INTO v_n FROM documentos WHERE id IN (v_d0, v_d1);
    IF v_n <> 2 THEN RAISE EXCEPTION 'Toda la empresa ve % de 2.', v_n; END IF;
    PERFORM fijar_alcance(v_usr_a, v_a, v_s1);
    SELECT count(*) INTO v_n FROM documentos WHERE id IN (v_d0, v_d1);
    IF v_n <> 1 THEN RAISE EXCEPTION 'Con la 0001 elegida arriba se ven % de 2.', v_n; END IF;

    -- fijar_contexto borra el alcance: nunca queda el de otra persona.
    PERFORM fijar_contexto(v_caja, v_a, '10.0.0.48', 'verificar.sql', false);
    IF current_setting('app.alc_tipo', true) <> '' OR current_setting('app.alc_vista', true) <> '' THEN
        RAISE EXCEPTION 'FALLO GRAVE: fijar_contexto dejó el alcance de la persona anterior.';
    END IF;

    -- Otra empresa no ve nada, se fije lo que se fije.
    PERFORM fijar_contexto(v_usr_b, v_b, '10.0.0.48', 'verificar.sql', false);
    PERFORM fijar_alcance(v_usr_b, v_b, v_s1);
    SELECT count(*) INTO v_n FROM documentos WHERE id IN (v_d0, v_d1, v_d2);
    IF v_n <> 0 THEN RAISE EXCEPTION 'FALLO GRAVE: otra empresa ve comprobantes de la A.'; END IF;
END;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

-- =========================================================================
RAISE NOTICE '49. Ficha del personal, roles nuevos y rol por empresa (046)';
-- =========================================================================
PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.49', 'verificar.sql', false);
BEGIN
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash, tipo_documento, numero_documento, nombres, apellido_paterno)
    VALUES (v_a, 'dni1@empresa-a.pe', 'Luis Quispe', 'x', '1', '70214589', 'Luis', 'Quispe');
    BEGIN
        INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash, tipo_documento, numero_documento)
        VALUES (v_a, 'dni2@empresa-a.pe', 'Otro', 'x', '1', '70214589');
        RAISE EXCEPTION 'FALLO: se repitió un DNI en el personal de la misma empresa.';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash, tipo_documento, numero_documento)
        VALUES (v_a, 'dni3@empresa-a.pe', 'Otro', 'x', '1', '7021');
        RAISE EXCEPTION 'FALLO: se aceptó un DNI que no son 8 dígitos.';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
END;
-- El mismo DNI en otra empresa sí (es otra relación de trabajo).
PERFORM fijar_contexto(v_usr_b, v_b, '10.0.0.49', 'verificar.sql', false);
INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash, tipo_documento, numero_documento)
VALUES (v_b, 'dni4@empresa-b.pe', 'Luis Quispe', 'x', '1', '70214589');

-- Los roles nuevos: el Jefe no trae llaves del Administrador.
IF EXISTS (SELECT 1 FROM rol_permiso WHERE rol_id = '55555555-5555-5555-5555-555555555555' AND activo
             AND (permiso_codigo IN ('usuarios.roles_gestionar', 'facturacion.credenciales_gestionar',
                                    'facturacion.sucursales_gestionar', 'usuarios.crear')
                  OR permiso_codigo LIKE 'plataforma.%')) THEN
    RAISE EXCEPTION 'FALLO GRAVE: el rol Jefe de sucursal trae permisos del Administrador.';
END IF;
IF NOT EXISTS (SELECT 1 FROM rol_permiso WHERE rol_id = '66666666-6666-6666-6666-666666666666' AND permiso_codigo = 'facturacion.notas_credito')
   OR NOT EXISTS (SELECT 1 FROM rol_permiso WHERE rol_id = '77777777-7777-7777-7777-777777777777' AND permiso_codigo = 'usuarios.crear') THEN
    RAISE EXCEPTION 'Faltan los roles que se suman (notas, agrega personal).';
END IF;

-- La aplicación no escribe el rol por empresa (solo las funciones de Mi equipo).
PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.49', 'verificar.sql', false);
BEGIN
    INSERT INTO usuario_rol_empresa (usuario_id, empresa_id, rol_id)
    VALUES (v_usr_a, v_a, '11111111-1111-1111-1111-111111111111');
    RAISE EXCEPTION 'FALLO GRAVE: la aplicación se dio un rol por empresa sin pasar por Mi equipo.';
EXCEPTION
    WHEN insufficient_privilege THEN NULL;
    WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
END;

-- Con un rol por empresa, cuentan sus permisos y no los de siempre.
PERFORM fijar_contexto(NULL, NULL, '10.0.0.49', 'verificar.sql', true);
INSERT INTO usuario_rol_empresa (usuario_id, empresa_id, rol_id)
VALUES (v_usr_a, v_a, '33333333-3333-3333-3333-333333333333');      -- Consulta
PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.49', 'verificar.sql', false);
IF EXISTS (SELECT 1 FROM permisos_efectivos WHERE usuario_id = v_usr_a AND permiso_codigo = 'facturacion.emitir') THEN
    RAISE EXCEPTION 'Con el rol Consulta en esta empresa sigue pudiendo emitir.';
END IF;
PERFORM fijar_contexto(NULL, NULL, '10.0.0.49', 'verificar.sql', true);
UPDATE usuario_rol_empresa SET activo = false WHERE usuario_id = v_usr_a;
PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.49', 'verificar.sql', false);
IF NOT EXISTS (SELECT 1 FROM permisos_efectivos WHERE usuario_id = v_usr_a AND permiso_codigo = 'facturacion.emitir') THEN
    RAISE EXCEPTION 'Sin rol por empresa no volvió a su rol de siempre.';
END IF;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

-- =========================================================================
RAISE NOTICE '50. Mi equipo: ficha y acceso por empresa con su rol; solo el dueño o el colaborador administrador (046)';
-- =========================================================================
DECLARE
    v_cta   uuid;
    v_e1    uuid;
    v_e2    uuid;
    v_dueno uuid;
    v_col   uuid;
    v_s1    uuid;
    v_js    json;
BEGIN
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    INSERT INTO cuentas (tipo, titular_tipo, titular_documento, titular_nombre, estado, confirmada_en, origen)
    VALUES ('profesional', 'natural', '46464646', 'Estudio 046', 'activa', now(), 'registro') RETURNING id INTO v_cta;
    INSERT INTO empresas (nombre, ruc, razon_social, cuenta_id) VALUES ('Uno 046', '20777777046', 'UNO 046', v_cta) RETURNING id INTO v_e1;
    INSERT INTO empresas (nombre, ruc, razon_social, cuenta_id) VALUES ('Dos 046', '20777777047', 'DOS 046', v_cta) RETURNING id INTO v_e2;
    INSERT INTO sucursales (empresa_id, codigo, nombre) VALUES (v_e1, '0000', 'Domicilio fiscal');
    INSERT INTO sucursales (empresa_id, codigo, nombre) VALUES (v_e1, '0001', 'Comas') RETURNING id INTO v_s1;
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta)
    VALUES (v_cta, 'dueno.046@correo.pe', 'Dueño 046', 'x', 'dueno') RETURNING id INTO v_dueno;
    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta, empresa_id)
    VALUES (v_cta, 'jorge.046@correo.pe', 'Jorge', 'x', 'colaborador', v_e1) RETURNING id INTO v_col;
    INSERT INTO usuario_empresa (usuario_id, empresa_id) VALUES (v_col, v_e1);
    INSERT INTO usuario_rol (usuario_id, rol_id) VALUES (v_col, '22222222-2222-2222-2222-222222222222');

    -- El colaborador no edita a nadie (ni se da accesos).
    PERFORM fijar_contexto(v_col, v_e1, '10.0.0.50', 'verificar.sql', false);
    IF equipo_guardar_acceso(v_col, v_e2, '33333333-3333-3333-3333-333333333333', 'empresa', NULL, NULL, false) <> 'sin_permiso' THEN
        RAISE EXCEPTION 'FALLO GRAVE: un colaborador se dio acceso a otra empresa.';
    END IF;

    PERFORM fijar_contexto(v_dueno, NULL, '10.0.0.50', 'verificar.sql', false);
    IF equipo_guardar_ficha(v_col, '{"tipoDocumento":"1","numeroDocumento":"72365418","nombres":"Jorge","apellidoPaterno":"Paz","apellidoMaterno":"Salas","cargo":"Asistente contable"}') <> 'ok' THEN
        RAISE EXCEPTION 'El dueño no pudo guardar la ficha de su colaborador.';
    END IF;
    IF equipo_guardar_ficha(v_dueno, '{"tipoDocumento":"1","numeroDocumento":"72365418","nombres":"Otro","apellidoPaterno":"X"}') <> 'documento_repetido' THEN
        RAISE EXCEPTION 'Se repitió un DNI dentro del equipo de la cuenta.';
    END IF;
    -- Su sucursal en Uno (Facturador, notas) y Consulta en Dos.
    v_res := equipo_guardar_acceso(v_col, v_e1, '22222222-2222-2222-2222-222222222222', 'sucursal', ARRAY[v_s1], v_s1, true);
    IF v_res <> 'ok' THEN RAISE EXCEPTION 'equipo_guardar_acceso (Uno): %', v_res; END IF;
    v_res := equipo_guardar_acceso(v_col, v_e2, '33333333-3333-3333-3333-333333333333', 'empresa', NULL, NULL, false);
    IF v_res <> 'ok' THEN RAISE EXCEPTION 'equipo_guardar_acceso (Dos): %', v_res; END IF;
    IF equipo_guardar_acceso(v_col, v_e2, '11111111-1111-1111-1111-111111111111', 'empresa', NULL, NULL, false) <> 'rol' THEN
        RAISE EXCEPTION 'FALLO GRAVE: se le dio Administrador a un colaborador por Mi equipo.';
    END IF;
    IF equipo_guardar_acceso(v_col, v_e2, '33333333-3333-3333-3333-333333333333', 'sucursal', ARRAY[v_s1], v_s1, false) <> 'sucursal' THEN
        RAISE EXCEPTION 'Se aceptó una sucursal de otra empresa.';
    END IF;
    IF NOT puede_abrir(v_col, v_e2) THEN RAISE EXCEPTION 'Dar acceso no le abrió la empresa Dos.'; END IF;

    v_js := equipo()::json;
    IF json_array_length(v_js) <> 2 THEN RAISE EXCEPTION 'equipo() no trae a las 2 personas: %', v_js; END IF;

    -- En Uno: Facturador con notas, solo su sucursal. En Dos: Consulta.
    PERFORM fijar_contexto(v_col, v_e1, '10.0.0.50', 'verificar.sql', false);
    IF NOT EXISTS (SELECT 1 FROM permisos_efectivos WHERE usuario_id = v_col AND permiso_codigo = 'facturacion.notas_credito')
       OR NOT EXISTS (SELECT 1 FROM permisos_efectivos WHERE usuario_id = v_col AND permiso_codigo = 'facturacion.emitir') THEN
        RAISE EXCEPTION 'En Uno no tiene Facturador + notas.';
    END IF;
    IF fijar_alcance(v_col, v_e1, NULL)::json ->> 'alcance' <> 'sucursal' THEN
        RAISE EXCEPTION 'En Uno no quedó con su sucursal.';
    END IF;
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    UPDATE usuarios SET empresa_id = v_e2 WHERE id = v_col;
    PERFORM fijar_contexto(v_col, v_e2, '10.0.0.50', 'verificar.sql', false);
    IF EXISTS (SELECT 1 FROM permisos_efectivos WHERE usuario_id = v_col AND permiso_codigo = 'facturacion.emitir') THEN
        RAISE EXCEPTION 'En Dos (Consulta) puede emitir.';
    END IF;

    -- Quitarle Dos: deja de poder abrirla.
    PERFORM fijar_contexto(v_dueno, NULL, '10.0.0.50', 'verificar.sql', false);
    v_res := equipo_quitar_acceso(v_col, v_e2);
    IF v_res <> 'ok' THEN RAISE EXCEPTION 'equipo_quitar_acceso: %', v_res; END IF;
    IF puede_abrir(v_col, v_e2) THEN
        RAISE EXCEPTION 'Quitado el acceso, todavía puede abrir Dos.';
    END IF;
END;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

-- =========================================================================
RAISE NOTICE '51. Cargos: maestra por empresa y por estudio, se crean al escribirlos y renombrar cambia todas las fichas (047)';
-- =========================================================================
DECLARE
    v_u1  uuid;
    v_u2  uuid;
    v_c   uuid;
    v_c2  uuid;
BEGIN
    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.51', 'verificar.sql', false);
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash, cargo)
    VALUES (v_a, 'cargo1@empresa-a.pe', 'Uno', 'x', ' cajero ') RETURNING id INTO v_u1;
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash, cargo)
    VALUES (v_a, 'cargo2@empresa-a.pe', 'Dos', 'x', 'CAJERO') RETURNING id INTO v_u2;

    SELECT cargo_id INTO v_c FROM usuarios WHERE id = v_u1;
    IF v_c IS NULL OR (SELECT cargo_id FROM usuarios WHERE id = v_u2) IS DISTINCT FROM v_c THEN
        RAISE EXCEPTION '«cajero» y «CAJERO» no quedaron en el mismo cargo de la maestra.';
    END IF;
    IF (SELECT count(*) FROM cargos WHERE empresa_id = v_a AND lower(nombre) = 'cajero') <> 1 THEN
        RAISE EXCEPTION 'Se duplicó el cargo Cajero en la maestra.';
    END IF;

    -- Renombrar cambia el texto de las dos fichas.
    UPDATE cargos SET nombre = 'Cajero principal', motivo = 'prueba' WHERE id = v_c;
    IF (SELECT count(*) FROM usuarios WHERE cargo_id = v_c AND cargo = 'Cajero principal') <> 2 THEN
        RAISE EXCEPTION 'Renombrar el cargo no cambió todas las fichas que lo usan.';
    END IF;

    -- Otra empresa no ve la maestra de la A.
    PERFORM fijar_contexto(v_usr_b, v_b, '10.0.0.51', 'verificar.sql', false);
    IF EXISTS (SELECT 1 FROM cargos WHERE id = v_c) THEN
        RAISE EXCEPTION 'FALLO GRAVE: una empresa ve los cargos de otra.';
    END IF;
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash, cargo)
    VALUES (v_b, 'cargo3@empresa-b.pe', 'Tres', 'x', 'Cajero principal');
    SELECT cargo_id INTO v_c2 FROM usuarios WHERE correo = 'cargo3@empresa-b.pe';
    IF v_c2 = v_c THEN RAISE EXCEPTION 'FALLO GRAVE: dos empresas comparten un cargo.'; END IF;
END;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

-- =========================================================================
RAISE NOTICE '52. Cambio de correo: lo pide cada quien, vale con el enlace una sola vez y no pisa un correo ocupado (048)';
-- =========================================================================
DECLARE
    v_u  uuid;
    v_js json;
BEGIN
    PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.52', 'verificar.sql', false);
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash)
    VALUES (v_a, 'cambia52@empresa-a.pe', 'Cambia', 'x') RETURNING id INTO v_u;

    -- Nadie pide el cambio de correo de otra persona.
    BEGIN
        INSERT INTO cambios_correo (usuario_id, correo_nuevo, token_hash, expira_en)
        VALUES (v_u, 'robo52@x.pe', decode(repeat('52', 32), 'hex'), now() + interval '1 hour');
        RAISE EXCEPTION 'FALLO GRAVE: se pidió el cambio de correo de otra persona.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
        WHEN raise_exception THEN IF SQLERRM LIKE 'FALLO GRAVE%' THEN RAISE; END IF;
    END;

    PERFORM fijar_contexto(v_u, v_a, '10.0.0.52', 'verificar.sql', false);
    INSERT INTO cambios_correo (usuario_id, correo_nuevo, token_hash, expira_en)
    VALUES (v_u, 'nuevo52@x.pe', decode(repeat('53', 32), 'hex'), now() + interval '1 hour');
    INSERT INTO cambios_correo (usuario_id, correo_nuevo, token_hash, expira_en)
    VALUES (v_u, 'ana@empresa-a.pe', decode(repeat('54', 32), 'hex'), now() + interval '1 hour');

    -- Sin sesión, como al abrir el enlace.
    PERFORM fijar_contexto(NULL, NULL, '10.0.0.52', 'verificar.sql', false);
    IF confirmar_cambio_correo(decode(repeat('54', 32), 'hex'))::json ->> 'resultado' <> 'ocupado' THEN
        RAISE EXCEPTION 'FALLO GRAVE: el cambio de correo pisó un correo que ya tiene otra persona.';
    END IF;
    v_js := confirmar_cambio_correo(decode(repeat('53', 32), 'hex'))::json;
    IF v_js ->> 'resultado' <> 'ok' OR v_js ->> 'anterior' <> 'cambia52@empresa-a.pe' THEN
        RAISE EXCEPTION 'confirmar_cambio_correo no aplicó el cambio: %', v_js;
    END IF;
    IF confirmar_cambio_correo(decode(repeat('53', 32), 'hex'))::json ->> 'resultado' <> 'no_existe' THEN
        RAISE EXCEPTION 'FALLO GRAVE: un enlace de cambio de correo sirvió dos veces.';
    END IF;
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF (SELECT correo FROM usuarios WHERE id = v_u) <> 'nuevo52@x.pe' THEN
        RAISE EXCEPTION 'El correo no cambió.';
    END IF;
END;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

-- =========================================================================
RAISE NOTICE '53. Invitación vencida en Mi equipo y reenvío con enlace nuevo (048)';
-- =========================================================================
DECLARE
    v_dueno uuid;
    v_col   uuid;
    v_js    json;
BEGIN
    SELECT id INTO v_dueno FROM usuarios WHERE correo = 'dueno.046@correo.pe';
    SELECT id INTO v_col   FROM usuarios WHERE correo = 'jorge.046@correo.pe';

    PERFORM fijar_contexto(v_col, NULL, '10.0.0.53', 'verificar.sql', false);
    IF equipo_reinvitar(v_dueno, decode(repeat('63', 32), 'hex'), now() + interval '1 day')::json ->> 'resultado' = 'ok' THEN
        RAISE EXCEPTION 'FALLO GRAVE: un colaborador reenvió la invitación del dueño.';
    END IF;

    PERFORM fijar_contexto(v_dueno, NULL, '10.0.0.53', 'verificar.sql', false);
    SELECT x INTO v_js FROM json_array_elements(equipo()::json) x WHERE (x ->> 'id')::uuid = v_col;
    IF (v_js ->> 'invitacionVencida')::boolean IS NOT TRUE THEN
        RAISE EXCEPTION 'Sin enlace vigente y sin haber entrado, no sale «Invitación vencida»: %', v_js;
    END IF;
    IF equipo_reinvitar(v_col, decode(repeat('64', 32), 'hex'), now() + interval '1 day')::json ->> 'resultado' <> 'ok' THEN
        RAISE EXCEPTION 'El dueño no pudo reenviar la invitación.';
    END IF;
    IF equipo_reinvitar(v_col, decode(repeat('65', 32), 'hex'), now() + interval '1 day')::json ->> 'resultado' <> 'ok' THEN
        RAISE EXCEPTION 'El dueño no pudo reenviar la invitación por segunda vez.';
    END IF;
    SELECT x INTO v_js FROM json_array_elements(equipo()::json) x WHERE (x ->> 'id')::uuid = v_col;
    IF (v_js ->> 'invitacionVencida')::boolean THEN
        RAISE EXCEPTION 'Reenviada la invitación, sigue saliendo «vencida».';
    END IF;
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF (SELECT count(*) FROM tokens_un_uso WHERE usuario_id = v_col AND proposito = 'invitacion'
                                             AND usado_en IS NULL AND anulado_en IS NULL) <> 1 THEN
        RAISE EXCEPTION 'FALLO GRAVE: reenviar la invitación dejó vivos los enlaces anteriores.';
    END IF;
END;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

-- =========================================================================
RAISE NOTICE '54. Entrar con Google: registro en un paso, enlace por correo verificado, sin el super administrador (049)';
-- =========================================================================
DECLARE
    v_js   json;
    v_u    uuid;
    v_cta  uuid;
    v_sup  text;
BEGIN
    PERFORM fijar_contexto(NULL, NULL, '10.0.0.54', 'verificar.sql', false);

    -- Nadie sin sesión lee los registros a medias.
    BEGIN
        PERFORM 1 FROM google_pendientes;
        RAISE EXCEPTION 'FALLO GRAVE: la aplicación lee los registros con Google a medias.';
    EXCEPTION
        WHEN insufficient_privilege THEN NULL;
    END;

    PERFORM google_pendiente_guardar(decode(repeat('71', 32), 'hex'), 'g-54-nuevo', 'nueva.54@gmail.com', 'Nueva Cincuenta', now() + interval '15 minutes');
    IF google_pendiente_ver(decode(repeat('71', 32), 'hex'))::json ->> 'correo' <> 'nueva.54@gmail.com' THEN
        RAISE EXCEPTION 'google_pendiente_ver no devuelve el correo.';
    END IF;
    v_js := registrar_con_google(decode(repeat('71', 32), 'hex'), 'negocio', decode(repeat('72', 32), 'hex'),
                                 now() + interval '1 hour', '10.0.0.54', 'verificar.sql')::json;
    IF v_js ->> 'resultado' <> 'ok' THEN RAISE EXCEPTION 'registrar_con_google: %', v_js; END IF;
    v_u := (v_js ->> 'usuarioId')::uuid;
    IF registrar_con_google(decode(repeat('71', 32), 'hex'), 'negocio', decode(repeat('73', 32), 'hex'),
                            now() + interval '1 hour', '10.0.0.54', 'verificar.sql')::json ->> 'resultado' <> 'no_existe' THEN
        RAISE EXCEPTION 'FALLO GRAVE: un registro con Google sirvió dos veces.';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    SELECT cuenta_id INTO v_cta FROM usuarios WHERE id = v_u AND nivel_cuenta = 'dueno' AND google_sub = 'g-54-nuevo';
    IF v_cta IS NULL OR NOT EXISTS (SELECT 1 FROM cuentas WHERE id = v_cta AND estado = 'activa' AND titular_documento IS NULL)
       OR NOT EXISTS (SELECT 1 FROM sesiones WHERE usuario_id = v_u AND dosfa_superado) THEN
        RAISE EXCEPTION 'El registro con Google no dejó la cuenta activa, su dueño y la sesión.';
    END IF;

    -- Su primera empresa (RUC 10) le pone el titular.
    INSERT INTO empresas (nombre, ruc, razon_social, cuenta_id) VALUES ('Nueva 54', '10456789541', 'NUEVA CINCUENTA', v_cta);
    IF NOT EXISTS (SELECT 1 FROM cuentas WHERE id = v_cta AND titular_tipo = 'natural' AND titular_documento = '45678954') THEN
        RAISE EXCEPTION 'La primera empresa no dejó el titular de la cuenta registrada con Google.';
    END IF;

    -- Entrar otra vez: por su cuenta de Google.
    PERFORM fijar_contexto(NULL, NULL, '10.0.0.54', 'verificar.sql', false);
    IF ingresar_con_google('g-54-nuevo', 'otro@gmail.com', decode(repeat('74', 32), 'hex'), now() + interval '1 hour',
                           '10.0.0.54', 'verificar.sql')::json ->> 'resultado' <> 'ok' THEN
        RAISE EXCEPTION 'No entró con su cuenta de Google.';
    END IF;

    -- Alguien que ya existía: se enlaza por su correo; otra cuenta de Google con ese correo, no.
    IF ingresar_con_google('g-54-ana', 'ANA@empresa-a.pe', decode(repeat('75', 32), 'hex'), now() + interval '1 hour',
                           '10.0.0.54', 'verificar.sql')::json ->> 'resultado' <> 'ok' THEN
        RAISE EXCEPTION 'Un usuario que ya existía no pudo entrar con Google.';
    END IF;
    IF ingresar_con_google('g-54-intruso', 'ana@empresa-a.pe', decode(repeat('76', 32), 'hex'), now() + interval '1 hour',
                           '10.0.0.54', 'verificar.sql')::json ->> 'resultado' <> 'otro_google' THEN
        RAISE EXCEPTION 'FALLO GRAVE: otra cuenta de Google entró a un usuario ya enlazado.';
    END IF;

    -- El super administrador no entra con Google.
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    SELECT correo INTO v_sup FROM usuarios WHERE es_super_admin LIMIT 1;
    PERFORM fijar_contexto(NULL, NULL, '10.0.0.54', 'verificar.sql', false);
    IF v_sup IS NOT NULL AND ingresar_con_google('g-54-super', v_sup, decode(repeat('77', 32), 'hex'), now() + interval '1 hour',
                           '10.0.0.54', 'verificar.sql')::json ->> 'resultado' <> 'super' THEN
        RAISE EXCEPTION 'FALLO GRAVE: el super administrador entró con Google.';
    END IF;

    -- Un correo nuevo sin registro: no existe (la pantalla ofrece crear la cuenta).
    IF ingresar_con_google('g-54-nadie', 'nadie.54@gmail.com', decode(repeat('78', 32), 'hex'), now() + interval '1 hour',
                           '10.0.0.54', 'verificar.sql')::json ->> 'resultado' <> 'no_existe' THEN
        RAISE EXCEPTION 'Un correo sin cuenta no dio «no_existe».';
    END IF;
END;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

-- =========================================================================
RAISE NOTICE '55. Guías de remisión: series T/V, clave única, transporte por empresa, eventos de SOL, cada empresa ve solo las suyas (050, 051)';
-- =========================================================================
PERFORM fijar_contexto(v_usr_a, v_a, '10.0.0.55', 'verificar.sql', false);
DECLARE
    v_g  uuid;
BEGIN
    INSERT INTO guias (empresa_id, tipo, serie, fecha_emision, fecha_traslado, motivo, modalidad,
                       destinatario_tipo, destinatario_doc, destinatario_nombre,
                       partida_direccion, partida_ubigeo, llegada_direccion, llegada_ubigeo, peso_bruto, idempotency_key)
    VALUES (v_a, '09', 'T001', current_date, current_date, '01', '02', '6', '20601234567', 'COMERCIAL NORTE SAC',
            'Av. Tupac Amaru 1820', '150110', 'Av. Industrial 450', '150112', 1325, 'clave-55')
    RETURNING id INTO v_g;
    INSERT INTO guia_bienes (empresa_id, guia_id, numero, descripcion, unidad, cantidad)
    VALUES (v_a, v_g, 1, 'Cemento', 'BG', 20);

    -- 051: el evento hecho en SOL se registra; el número no se repite en la empresa.
    INSERT INTO guia_eventos (empresa_id, guia_id, tipo, numero_sol, fecha, reinicio_direccion, reinicio_ubigeo, placa)
    VALUES (v_a, v_g, 'transbordo', 'EG01-1', current_date, 'Km 40 Panamericana Norte', '150101', 'B7K456');
    BEGIN
        INSERT INTO guia_eventos (empresa_id, guia_id, tipo, numero_sol, fecha, reinicio_direccion, reinicio_ubigeo)
        VALUES (v_a, v_g, 'arribo', 'EG01-1', current_date, 'Km 40 Panamericana Norte', '150101');
        RAISE EXCEPTION 'FALLO: el mismo evento de SOL registrado dos veces.';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO guia_eventos (empresa_id, guia_id, tipo, numero_sol, fecha, reinicio_direccion, reinicio_ubigeo)
        VALUES (v_a, v_g, 'choque', 'EG01-2', current_date, 'Km 40 Panamericana Norte', '150101');
        RAISE EXCEPTION 'FALLO: se aceptó un tipo de evento que no existe.';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    BEGIN
        INSERT INTO guias (empresa_id, tipo, serie, fecha_emision, fecha_traslado, motivo, modalidad,
                           destinatario_tipo, destinatario_doc, destinatario_nombre,
                           partida_direccion, partida_ubigeo, llegada_direccion, llegada_ubigeo, peso_bruto, idempotency_key)
        VALUES (v_a, '09', 'T001', current_date, current_date, '01', '02', '6', '20601234567', 'X',
                'a', '150110', 'b', '150112', 1, 'clave-55');
        RAISE EXCEPTION 'FALLO: dos guías con la misma clave (doble clic = dos guías).';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO guias (empresa_id, tipo, serie, fecha_emision, fecha_traslado, motivo, modalidad,
                           destinatario_tipo, destinatario_doc, destinatario_nombre,
                           partida_direccion, partida_ubigeo, llegada_direccion, llegada_ubigeo, peso_bruto, idempotency_key)
        VALUES (v_a, '09', 'V001', current_date, current_date, '01', '02', '6', '20601234567', 'X',
                'a', '150110', 'b', '150112', 1, 'clave-55b');
        RAISE EXCEPTION 'FALLO: una guía del remitente con serie V (la V es del transportista).';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE guias SET estado = 'anulado' WHERE id = v_g;
        RAISE EXCEPTION 'FALLO: una guía anulada sin fecha de anulación.';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    INSERT INTO vehiculos (empresa_id, placa, descripcion) VALUES (v_a, 'ABC123', 'Furgón');
    INSERT INTO conductores (empresa_id, numero_doc, nombres, apellidos, licencia) VALUES (v_a, '41025874', 'Jorge', 'Paz', 'Q41025874');
    INSERT INTO transportistas (empresa_id, ruc, razon_social) VALUES (v_a, '20512345678', 'TRANSPORTES RAPIDOS SAC');
    BEGIN
        INSERT INTO vehiculos (empresa_id, placa) VALUES (v_a, 'ABC123');
        RAISE EXCEPTION 'FALLO: la misma placa dos veces en la empresa.';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO vehiculos (empresa_id, placa) VALUES (v_a, 'ABC-123');
        RAISE EXCEPTION 'FALLO: se aceptó una placa con guion.';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    -- Otra empresa no ve ni la guía ni el transporte; su misma placa sí la puede guardar.
    PERFORM fijar_contexto(v_usr_b, v_b, '10.0.0.55', 'verificar.sql', false);
    PERFORM fijar_alcance(v_usr_b, v_b, NULL);
    SELECT count(*) INTO v_n FROM guias WHERE id = v_g;
    IF v_n > 0 OR EXISTS (SELECT 1 FROM guia_bienes WHERE guia_id = v_g)
       OR EXISTS (SELECT 1 FROM vehiculos WHERE empresa_id = v_a) OR EXISTS (SELECT 1 FROM conductores WHERE empresa_id = v_a) THEN
        RAISE EXCEPTION 'FALLO GRAVE: una empresa ve las guías o el transporte de otra.';
    END IF;
    INSERT INTO vehiculos (empresa_id, placa) VALUES (v_b, 'ABC123');
    IF EXISTS (SELECT 1 FROM guia_eventos WHERE guia_id = v_g) THEN
        RAISE EXCEPTION 'FALLO GRAVE: una empresa ve los eventos de las guías de otra.';
    END IF;

    -- Los roles con facturación recibieron las guías.
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF NOT EXISTS (SELECT 1 FROM rol_permiso WHERE rol_id = '22222222-2222-2222-2222-222222222222' AND permiso_codigo = 'guias.emitir')
       OR NOT EXISTS (SELECT 1 FROM rol_permiso WHERE rol_id = '11111111-1111-1111-1111-111111111111' AND permiso_codigo = 'guias.transporte')
       OR EXISTS (SELECT 1 FROM rol_permiso WHERE rol_id = '33333333-3333-3333-3333-333333333333' AND permiso_codigo = 'guias.emitir') THEN
        RAISE EXCEPTION 'Los permisos de guías no quedaron como se esperaba.';
    END IF;
END;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

-- =========================================================================
RAISE NOTICE '56. Inventario: stock inicial, venta directa, rechazo, bloqueo, reserva y guía, nota de crédito, traslado y «Recibí», aislamiento (052)';
-- =========================================================================
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
DECLARE
    v_e    uuid;
    v_u    uuid;
    v_s0   uuid;
    v_s1   uuid;
    v_cli  uuid;
    v_p    uuid;
    v_srv  uuid;
    v_op   uuid;
    v_d    uuid;
    v_d2   uuid;
    v_nc   uuid;
    v_g    uuid;
    v_t    uuid;
    v_f    numeric;
    v_r    numeric;
    v_bien boolean;
BEGIN
    INSERT INTO empresas (nombre, ruc, razon_social) VALUES ('Inventario 052', '20555555052', 'INVENTARIO 052') RETURNING id INTO v_e;
    INSERT INTO sucursales (empresa_id, codigo, nombre) VALUES (v_e, '0000', 'Centro') RETURNING id INTO v_s0;
    INSERT INTO sucursales (empresa_id, codigo, nombre) VALUES (v_e, '0001', 'Comas') RETURNING id INTO v_s1;
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash) VALUES (v_e, 'almacen.052@correo.pe', 'Almacén', 'x') RETURNING id INTO v_u;

    PERFORM fijar_contexto(v_u, v_e, '10.0.0.56', 'verificar.sql', false);
    INSERT INTO clientes (empresa_id, tipo_documento, numero_documento, razon_social) VALUES (v_e, '6', '20512345678', 'COMERCIAL NORTE SAC') RETURNING id INTO v_cli;
    INSERT INTO productos (empresa_id, codigo, descripcion, unidad_medida) VALUES (v_e, 'CEM-001', 'Cemento', 'BG') RETURNING id INTO v_p;
    INSERT INTO productos (empresa_id, codigo, descripcion, unidad_medida, controla_stock) VALUES (v_e, 'S-01', 'Instalación', 'ZZ', true) RETURNING id INTO v_srv;
    IF (SELECT controla_stock FROM productos WHERE id = v_srv) THEN
        RAISE EXCEPTION 'FALLO: un servicio (ZZ) quedó controlando stock.';
    END IF;
    IF NOT (SELECT controla_stock FROM productos WHERE id = v_p) THEN
        RAISE EXCEPTION 'FALLO: un bien nuevo no viene con «Controla stock» marcado.';
    END IF;

    -- Apagado: una factura no mueve nada.
    INSERT INTO documentos (empresa_id, tipo, serie, cliente_id, fecha_emision, moneda, sucursal_id)
    VALUES (v_e, '01', 'F001', v_cli, current_date, 'PEN', v_s0) RETURNING id INTO v_d;
    INSERT INTO documento_lineas (documento_id, empresa_id, numero, producto_id, descripcion, cantidad, valor_unitario, precio_unitario)
    VALUES (v_d, v_e, 1, v_p, 'Cemento', 10, 20, 23.6), (v_d, v_e, 2, v_srv, 'Instalación', 1, 100, 118);
    UPDATE documentos SET estado = 'encolado' WHERE id = v_d;
    IF EXISTS (SELECT 1 FROM inv_movimientos WHERE empresa_id = v_e) THEN
        RAISE EXCEPTION 'FALLO: con el inventario apagado, una factura movió stock.';
    END IF;

    -- Encendido (venta directa), stock inicial de 100 en el Centro.
    INSERT INTO inventario_config (empresa_id, activo_inventario) VALUES (v_e, true);
    -- 053: encendido pero SIN el módulo contratado, no cuenta ni bloquea.
    IF inv_activo(v_e) OR inv_bloquea(v_e, v_s0) THEN
        RAISE EXCEPTION 'FALLO: el inventario cuenta sin el módulo «inventario» contratado.';
    END IF;
    -- La empresa no se puede contratar el módulo a sí misma.
    BEGIN
        INSERT INTO empresa_modulo (empresa_id, modulo_codigo, contratado_en) VALUES (v_e, 'inventario', now());
        RAISE EXCEPTION 'FALLO GRAVE: una empresa se contrató el módulo de inventario sola.';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    INSERT INTO empresa_modulo (empresa_id, modulo_codigo, contratado_en) VALUES (v_e, 'inventario', now());
    PERFORM fijar_contexto(v_u, v_e, '10.0.0.56', 'verificar.sql', false);
    IF NOT inv_activo(v_e) THEN
        RAISE EXCEPTION 'FALLO: con el módulo contratado y encendido, el inventario no cuenta.';
    END IF;
    INSERT INTO inv_operaciones (empresa_id, tipo, sucursal_id) VALUES (v_e, 'stock_inicial', v_s0) RETURNING id INTO v_op;
    INSERT INTO inv_operacion_lineas (empresa_id, operacion_id, producto_id, cantidad) VALUES (v_e, v_op, v_p, 100);
    PERFORM inv_aplicar_operacion(v_op);
    BEGIN
        INSERT INTO inv_operaciones (empresa_id, tipo, sucursal_id) VALUES (v_e, 'stock_inicial', v_s0) RETURNING id INTO v_op;
        INSERT INTO inv_operacion_lineas (empresa_id, operacion_id, producto_id, cantidad) VALUES (v_e, v_op, v_p, 5);
        PERFORM inv_aplicar_operacion(v_op);
        RAISE EXCEPTION 'FALLO: el stock inicial se cargó dos veces para el mismo producto y sucursal.';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'INV_YA_INICIAL%' THEN RAISE; END IF;
    END;

    -- Venta directa: sale al emitir; vuelve si SUNAT la rechaza; sale otra vez al reintentar.
    INSERT INTO documentos (empresa_id, tipo, serie, cliente_id, fecha_emision, moneda, sucursal_id)
    VALUES (v_e, '01', 'F001', v_cli, current_date, 'PEN', v_s0) RETURNING id INTO v_d;
    INSERT INTO documento_lineas (documento_id, empresa_id, numero, producto_id, descripcion, cantidad, valor_unitario, precio_unitario)
    VALUES (v_d, v_e, 1, v_p, 'Cemento', 30, 20, 23.6), (v_d, v_e, 2, v_srv, 'Instalación', 1, 100, 118);
    UPDATE documentos SET estado = 'encolado' WHERE id = v_d;
    SELECT fisico INTO v_f FROM inv_stock WHERE producto_id = v_p AND sucursal_id = v_s0;
    IF v_f <> 70 THEN RAISE EXCEPTION 'FALLO: tras vender 30 de 100 quedan % (deberían ser 70).', v_f; END IF;
    UPDATE documentos SET estado = 'aceptado' WHERE id = v_d;
    UPDATE documentos SET estado = 'aceptado' WHERE id = v_d;
    SELECT fisico INTO v_f FROM inv_stock WHERE producto_id = v_p AND sucursal_id = v_s0;
    IF v_f <> 70 THEN RAISE EXCEPTION 'FALLO: el mismo aviso aplicado dos veces movió el stock otra vez (%).', v_f; END IF;
    UPDATE documentos SET estado = 'anulado' WHERE id = v_d;
    SELECT fisico INTO v_f FROM inv_stock WHERE producto_id = v_p AND sucursal_id = v_s0;
    IF v_f <> 100 THEN RAISE EXCEPTION 'FALLO: la baja no devolvió el stock (%).', v_f; END IF;

    -- «Empezar a controlar»: ahora lo que no hay se bloquea al emitir.
    INSERT INTO inv_sucursales (empresa_id, sucursal_id, controla_desde) VALUES (v_e, v_s0, now())
    ON CONFLICT (sucursal_id) DO UPDATE SET controla_desde = now();
    INSERT INTO documentos (empresa_id, tipo, serie, cliente_id, fecha_emision, moneda, sucursal_id)
    VALUES (v_e, '01', 'F001', v_cli, current_date, 'PEN', v_s0) RETURNING id INTO v_d;
    INSERT INTO documento_lineas (documento_id, empresa_id, numero, producto_id, descripcion, cantidad, valor_unitario, precio_unitario)
    VALUES (v_d, v_e, 1, v_p, 'Cemento', 150, 20, 23.6);
    v_bien := false;
    BEGIN
        UPDATE documentos SET estado = 'encolado' WHERE id = v_d;
    EXCEPTION WHEN raise_exception THEN
        v_bien := SQLERRM LIKE 'INV_SIN_STOCK|%';
    END;
    IF NOT v_bien THEN RAISE EXCEPTION 'FALLO: se emitió una factura de 150 con 100 disponibles y la sucursal controlando.'; END IF;
    IF (SELECT estado FROM documentos WHERE id = v_d) <> 'borrador'
       OR (SELECT fisico FROM inv_stock WHERE producto_id = v_p AND sucursal_id = v_s0) <> 100 THEN
        RAISE EXCEPTION 'FALLO: el bloqueo por stock dejó la factura o el saldo a medias.';
    END IF;
    -- El aviso del servicio nunca falla por stock (aceptado sin pasar por encolado).
    UPDATE documentos SET estado = 'aceptado' WHERE id = v_d;
    UPDATE documentos SET estado = 'rechazado' WHERE id = v_d;

    -- Con despacho: la factura reserva 40; la guía saca 25; la nota de crédito por 10 libera 10.
    INSERT INTO documentos (empresa_id, tipo, serie, cliente_id, fecha_emision, moneda, sucursal_id, entrega_despues)
    VALUES (v_e, '01', 'F001', v_cli, current_date, 'PEN', v_s0, true) RETURNING id INTO v_d2;
    INSERT INTO documento_lineas (documento_id, empresa_id, numero, producto_id, descripcion, cantidad, valor_unitario, precio_unitario)
    VALUES (v_d2, v_e, 1, v_p, 'Cemento', 40, 20, 23.6);
    UPDATE documentos SET estado = 'encolado' WHERE id = v_d2;
    SELECT fisico, reservado INTO v_f, v_r FROM inv_stock WHERE producto_id = v_p AND sucursal_id = v_s0;
    IF v_f <> 100 OR v_r <> 40 THEN RAISE EXCEPTION 'FALLO: la reserva dejó físico % y reservado % (100 y 40).', v_f, v_r; END IF;

    INSERT INTO guias (empresa_id, sucursal_id, tipo, serie, fecha_emision, fecha_traslado, motivo, modalidad,
                       destinatario_tipo, destinatario_doc, destinatario_nombre,
                       partida_direccion, partida_ubigeo, llegada_direccion, llegada_ubigeo, peso_bruto, idempotency_key, documento_id)
    VALUES (v_e, v_s0, '09', 'T001', current_date, current_date, '01', '02', '6', '20512345678', 'COMERCIAL NORTE SAC',
            'Av. Tupac Amaru 1820', '150110', 'Av. Industrial 450', '150112', 600, 'clave-56a', v_d2)
    RETURNING id INTO v_g;
    INSERT INTO guia_bienes (empresa_id, guia_id, numero, producto_id, descripcion, unidad, cantidad)
    VALUES (v_e, v_g, 1, v_p, 'Cemento', 'BG', 25);
    SET CONSTRAINTS inv_guia IMMEDIATE;
    SET CONSTRAINTS inv_guia DEFERRED;
    SELECT fisico, reservado INTO v_f, v_r FROM inv_stock WHERE producto_id = v_p AND sucursal_id = v_s0;
    IF v_f <> 75 OR v_r <> 15 THEN RAISE EXCEPTION 'FALLO: el despacho dejó físico % y reservado % (75 y 15).', v_f, v_r; END IF;

    INSERT INTO documentos (empresa_id, tipo, serie, cliente_id, fecha_emision, moneda, sucursal_id, documento_ref_id, tipo_nota, motivo_nota)
    VALUES (v_e, '07', 'FC01', v_cli, current_date, 'PEN', v_s0, v_d2, '07', 'Devolución') RETURNING id INTO v_nc;
    INSERT INTO documento_lineas (documento_id, empresa_id, numero, producto_id, descripcion, cantidad, valor_unitario, precio_unitario)
    VALUES (v_nc, v_e, 1, v_p, 'Cemento', 10, 20, 23.6);
    UPDATE documentos SET estado = 'encolado' WHERE id = v_nc;
    IF (SELECT reservado FROM inv_stock WHERE producto_id = v_p AND sucursal_id = v_s0) <> 15 THEN
        RAISE EXCEPTION 'FALLO: la nota de crédito movió el stock antes de que SUNAT la aceptara.';
    END IF;
    UPDATE documentos SET estado = 'aceptado' WHERE id = v_nc;
    SELECT fisico, reservado INTO v_f, v_r FROM inv_stock WHERE producto_id = v_p AND sucursal_id = v_s0;
    IF v_f <> 75 OR v_r <> 5 THEN RAISE EXCEPTION 'FALLO: la nota sobre lo reservado dejó físico % y reservado % (75 y 5).', v_f, v_r; END IF;

    -- La baja de la factura libera lo que quedaba reservado (5); lo despachado no vuelve solo.
    UPDATE documentos SET estado = 'anulado' WHERE id = v_d2;
    SELECT fisico, reservado INTO v_f, v_r FROM inv_stock WHERE producto_id = v_p AND sucursal_id = v_s0;
    IF v_f <> 75 OR v_r <> 0 THEN RAISE EXCEPTION 'FALLO: la baja dejó físico % y reservado % (75 y 0).', v_f, v_r; END IF;

    -- Traslado 04 de 50 al local de Comas: sale y queda en tránsito; «Recibí» 48, faltan 2 por merma.
    INSERT INTO guias (empresa_id, sucursal_id, tipo, serie, fecha_emision, fecha_traslado, motivo, modalidad,
                       destinatario_tipo, destinatario_doc, destinatario_nombre,
                       partida_direccion, partida_ubigeo, llegada_direccion, llegada_ubigeo, peso_bruto, idempotency_key,
                       inv_origen_id, inv_destino_id)
    VALUES (v_e, v_s0, '09', 'T001', current_date, current_date, '04', '02', '6', '20555555052', 'INVENTARIO 052',
            'Av. Tupac Amaru 1820', '150110', 'Av. Universitaria 900', '150110', 1250, 'clave-56b', v_s0, v_s1)
    RETURNING id INTO v_g;
    INSERT INTO guia_bienes (empresa_id, guia_id, numero, producto_id, descripcion, unidad, cantidad)
    VALUES (v_e, v_g, 1, v_p, 'Cemento', 'BG', 50);
    SET CONSTRAINTS inv_guia IMMEDIATE;
    SET CONSTRAINTS inv_guia DEFERRED;
    SELECT id INTO v_t FROM inv_traslados WHERE guia_id = v_g AND estado = 'en_transito';
    IF v_t IS NULL OR (SELECT fisico FROM inv_stock WHERE producto_id = v_p AND sucursal_id = v_s0) <> 25 THEN
        RAISE EXCEPTION 'FALLO: el traslado no salió del origen o no quedó en tránsito.';
    END IF;
    BEGIN
        PERFORM inv_recibir_traslado(v_t, jsonb_build_array(jsonb_build_object('productoId', v_p, 'recibido', 48)), NULL);
        RAISE EXCEPTION 'FALLO: se recibió con diferencia sin decir por qué.';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'INV_FALTA_MOTIVO%' THEN RAISE; END IF;
    END;
    IF inv_recibir_traslado(v_t, jsonb_build_array(jsonb_build_object('productoId', v_p, 'recibido', 48, 'motivo', 'merma')), NULL) <> 'con_diferencia'
       OR (SELECT fisico FROM inv_stock WHERE producto_id = v_p AND sucursal_id = v_s1) <> 48 THEN
        RAISE EXCEPTION 'FALLO: «Recibí» no dejó 48 en el destino.';
    END IF;
    -- Recibido: registrar después su anulación en SOL no deshace nada.
    UPDATE guias SET estado = 'anulado', anulada_en = current_date WHERE id = v_g;
    SET CONSTRAINTS inv_guia IMMEDIATE;
    SET CONSTRAINTS inv_guia DEFERRED;
    IF (SELECT fisico FROM inv_stock WHERE producto_id = v_p AND sucursal_id = v_s1) <> 48
       OR (SELECT fisico FROM inv_stock WHERE producto_id = v_p AND sucursal_id = v_s0) <> 25 THEN
        RAISE EXCEPTION 'FALLO: anular una guía ya recibida movió el stock.';
    END IF;

    -- El kardex no se toca.
    BEGIN
        UPDATE inv_movimientos SET cantidad = 0 WHERE empresa_id = v_e;
        RAISE EXCEPTION 'FALLO: se pudo cambiar un movimiento del kardex.';
    EXCEPTION WHEN insufficient_privilege OR raise_exception THEN
        IF SQLERRM LIKE 'FALLO%' THEN RAISE; END IF;
    END;
    -- Ni el saldo se escribe a mano (solo el mínimo).
    BEGIN
        UPDATE inv_stock SET fisico = 9999 WHERE producto_id = v_p;
        RAISE EXCEPTION 'FALLO: la aplicación pudo escribir el saldo a mano.';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;
    UPDATE inv_stock SET minimo = 30 WHERE producto_id = v_p AND sucursal_id = v_s1;
    BEGIN
        PERFORM inv_mover(v_e, v_p, v_s0, 'ajuste', 1000, 0, 'operacion', v_p, false);
        RAISE EXCEPTION 'FALLO: la aplicación pudo llamar a inv_mover directamente.';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;

    -- Otra empresa no ve nada de este inventario.
    PERFORM fijar_contexto(v_usr_b, v_b, '10.0.0.56', 'verificar.sql', false);
    IF EXISTS (SELECT 1 FROM inv_stock WHERE empresa_id = v_e) OR EXISTS (SELECT 1 FROM inv_movimientos WHERE empresa_id = v_e)
       OR EXISTS (SELECT 1 FROM inv_traslados WHERE empresa_id = v_e) OR EXISTS (SELECT 1 FROM inventario_config WHERE empresa_id = v_e) THEN
        RAISE EXCEPTION 'FALLO GRAVE: una empresa ve el inventario de otra.';
    END IF;
    BEGIN
        PERFORM inv_recibir_traslado(v_t, '[]'::jsonb, NULL);
        RAISE EXCEPTION 'FALLO GRAVE: otra empresa pudo recibir un traslado ajeno.';
    EXCEPTION WHEN raise_exception THEN
        IF SQLERRM NOT LIKE 'INV_NO_EXISTE%' THEN RAISE; END IF;
    END;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF NOT EXISTS (SELECT 1 FROM rol_permiso WHERE rol_id = '11111111-1111-1111-1111-111111111111' AND permiso_codigo = 'inventario.configurar')
       OR EXISTS (SELECT 1 FROM rol_permiso WHERE rol_id = '33333333-3333-3333-3333-333333333333' AND permiso_codigo = 'inventario.ajustar') THEN
        RAISE EXCEPTION 'Los permisos de inventario no quedaron como se esperaba.';
    END IF;
END;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

-- =========================================================================
RAISE NOTICE '57. Punto de venta: una caja abierta por persona, arqueo cerrado sin cambios, movimientos y pagos solo inserción, aislamiento (055)';
-- =========================================================================
DECLARE
    v_e   uuid;
    v_u   uuid;
    v_u2  uuid;
    v_s   uuid;
    v_c   uuid;
    v_c2  uuid;
    v_ses uuid;
    v_cli uuid;
    v_d   uuid;
    v_v   uuid;
BEGIN
    INSERT INTO empresas (nombre, ruc, razon_social) VALUES ('Caja 055', '20555555055', 'CAJA 055') RETURNING id INTO v_e;
    INSERT INTO sucursales (empresa_id, codigo, nombre) VALUES (v_e, '0000', 'Centro') RETURNING id INTO v_s;
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash) VALUES (v_e, 'caja1.055@correo.pe', 'Cajera', 'x') RETURNING id INTO v_u;
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash) VALUES (v_e, 'caja2.055@correo.pe', 'Cajero', 'x') RETURNING id INTO v_u2;

    PERFORM fijar_contexto(v_u, v_e, '10.0.0.57', 'verificar.sql', false);
    INSERT INTO pos_cajas (empresa_id, sucursal_id, nombre) VALUES (v_e, v_s, 'Caja 01') RETURNING id INTO v_c;
    INSERT INTO pos_cajas (empresa_id, sucursal_id, nombre) VALUES (v_e, v_s, 'Caja 02') RETURNING id INTO v_c2;
    INSERT INTO pos_sesiones (empresa_id, sucursal_id, caja_id, usuario_id, fondo_inicial)
    VALUES (v_e, v_s, v_c, v_u, 200) RETURNING id INTO v_ses;

    -- La misma persona no abre dos cajas; ni dos personas la misma caja.
    BEGIN
        INSERT INTO pos_sesiones (empresa_id, sucursal_id, caja_id, usuario_id) VALUES (v_e, v_s, v_c2, v_u);
        RAISE EXCEPTION 'FALLO: una persona abrió dos cajas a la vez.';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO pos_sesiones (empresa_id, sucursal_id, caja_id, usuario_id) VALUES (v_e, v_s, v_c, v_u2);
        RAISE EXCEPTION 'FALLO: dos personas abrieron la misma caja.';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;

    -- Un retiro, y no se cambia ni se borra.
    INSERT INTO pos_movimientos (empresa_id, sesion_id, tipo, monto, motivo) VALUES (v_e, v_ses, 'retiro', 35, 'Gasto: movilidad');
    BEGIN
        UPDATE pos_movimientos SET monto = 1 WHERE sesion_id = v_ses;
        RAISE EXCEPTION 'FALLO GRAVE: se cambió un retiro de caja.';
    EXCEPTION WHEN insufficient_privilege OR raise_exception THEN
        IF SQLERRM LIKE 'FALLO%' THEN RAISE; END IF;
    END;
    BEGIN
        INSERT INTO pos_movimientos (empresa_id, sesion_id, tipo, monto, motivo) VALUES (v_e, v_ses, 'retiro', -5, 'Gasto');
        RAISE EXCEPTION 'FALLO: se aceptó un retiro negativo.';
    EXCEPTION WHEN check_violation THEN NULL;
    END;

    -- Una venta con su pago.
    INSERT INTO clientes (empresa_id, tipo_documento, numero_documento, razon_social) VALUES (v_e, '0', '00000000', 'CLIENTE VARIOS') RETURNING id INTO v_cli;
    INSERT INTO documentos (empresa_id, tipo, serie, cliente_id, fecha_emision, moneda, sucursal_id)
    VALUES (v_e, '03', 'B001', v_cli, current_date, 'PEN', v_s) RETURNING id INTO v_d;
    INSERT INTO pos_ventas (empresa_id, sesion_id, documento_id, total, recibido, vuelto) VALUES (v_e, v_ses, v_d, 227.25, 250, 22.75) RETURNING id INTO v_v;
    INSERT INTO pos_pagos (empresa_id, venta_id, medio, monto) VALUES (v_e, v_v, 'efectivo', 227.25);
    BEGIN
        INSERT INTO pos_pagos (empresa_id, venta_id, medio, monto) VALUES (v_e, v_v, 'cheque', 1);
        RAISE EXCEPTION 'FALLO: se aceptó un medio de pago que no existe.';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO pos_ventas (empresa_id, sesion_id, documento_id, total) VALUES (v_e, v_ses, v_d, 1);
        RAISE EXCEPTION 'FALLO: un comprobante quedó en dos ventas.';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;

    -- Cerrar con diferencia exige observación; cerrada, no se cambia.
    BEGIN
        UPDATE pos_sesiones SET cerrada_en = now(), cerrada_por = v_u, efectivo_esperado = 392.25,
               efectivo_contado = 387.25, diferencia = -5 WHERE id = v_ses;
        RAISE EXCEPTION 'FALLO: se cerró una caja con diferencia y sin observación.';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    UPDATE pos_sesiones SET cerrada_en = now(), cerrada_por = v_u, efectivo_esperado = 392.25,
           efectivo_contado = 387.25, diferencia = -5, observacion_cierre = 'vuelto de más' WHERE id = v_ses;
    BEGIN
        UPDATE pos_sesiones SET efectivo_contado = 392.25, diferencia = 0 WHERE id = v_ses;
        RAISE EXCEPTION 'FALLO GRAVE: se cambió el arqueo de una caja cerrada.';
    EXCEPTION WHEN check_violation THEN
        IF SQLERRM NOT LIKE 'POS_CERRADA%' THEN RAISE; END IF;
    END;
    -- Cerrada, la misma persona puede abrir otra.
    INSERT INTO pos_sesiones (empresa_id, sucursal_id, caja_id, usuario_id, fondo_inicial) VALUES (v_e, v_s, v_c, v_u, 200);

    -- Otra empresa no ve nada.
    PERFORM fijar_contexto(v_usr_b, v_b, '10.0.0.57', 'verificar.sql', false);
    IF EXISTS (SELECT 1 FROM pos_sesiones WHERE empresa_id = v_e) OR EXISTS (SELECT 1 FROM pos_ventas WHERE empresa_id = v_e)
       OR EXISTS (SELECT 1 FROM pos_movimientos WHERE empresa_id = v_e) OR EXISTS (SELECT 1 FROM pos_cajas WHERE empresa_id = v_e) THEN
        RAISE EXCEPTION 'FALLO GRAVE: una empresa ve la caja de otra.';
    END IF;
    BEGIN
        INSERT INTO pos_movimientos (empresa_id, sesion_id, tipo, monto, motivo) VALUES (v_e, v_ses, 'ingreso', 10, 'Sencillo');
        RAISE EXCEPTION 'FALLO GRAVE: otra empresa metió dinero en una caja ajena.';
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF NOT EXISTS (SELECT 1 FROM rol_permiso WHERE rol_id = '11111111-1111-1111-1111-111111111111' AND permiso_codigo = 'pos.cajas_ver')
       OR NOT EXISTS (SELECT 1 FROM rol_permiso WHERE rol_id = '22222222-2222-2222-2222-222222222222' AND permiso_codigo = 'pos.vender')
       OR EXISTS (SELECT 1 FROM rol_permiso WHERE rol_id = '22222222-2222-2222-2222-222222222222' AND permiso_codigo = 'pos.cajas_ver') THEN
        RAISE EXCEPTION 'Los permisos del punto de venta no quedaron como se esperaba.';
    END IF;
END;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

-- =========================================================================
RAISE NOTICE '58. Mayúsculas: nombres y descripciones se guardan en mayúsculas; correos, teléfonos y códigos de lista no (056)';
-- =========================================================================
DECLARE
    v_e   uuid;
    v_u   uuid;
    v_s   uuid;
    v_c   uuid;
    v_ses uuid;
    v_cli uuid;
    v_p   uuid;
    r     record;
BEGIN
    INSERT INTO empresas (nombre, ruc, razon_social) VALUES ('Mayúsculas 056', '20555555056', 'MAYUSCULAS 056') RETURNING id INTO v_e;
    INSERT INTO sucursales (empresa_id, codigo, nombre, direccion) VALUES (v_e, '0000', 'Tienda del centro', 'av. los álamos 123') RETURNING id INTO v_s;
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash) VALUES (v_e, 'Caja.056@Correo.pe', 'Cajera', 'x') RETURNING id INTO v_u;
    PERFORM fijar_contexto(v_u, v_e, '10.0.0.58', 'verificar.sql', false);

    IF (SELECT nombre || '|' || direccion FROM sucursales WHERE id = v_s) <> 'TIENDA DEL CENTRO|AV. LOS ÁLAMOS 123' THEN
        RAISE EXCEPTION 'FALLO: la sucursal no quedó en mayúsculas.';
    END IF;

    INSERT INTO clientes (empresa_id, tipo_documento, numero_documento, razon_social, direccion, correo)
    VALUES (v_e, '6', '20100000056', 'ferretería ñandú s.a.c.', 'jr. puno 45', 'Ventas@Nandu.pe') RETURNING id INTO v_cli;
    SELECT razon_social, direccion, correo INTO r FROM clientes WHERE id = v_cli;
    IF r.razon_social <> 'FERRETERÍA ÑANDÚ S.A.C.' OR r.direccion <> 'JR. PUNO 45' THEN
        RAISE EXCEPTION 'FALLO: el cliente no quedó en mayúsculas (%, %).', r.razon_social, r.direccion;
    END IF;
    IF r.correo <> 'Ventas@Nandu.pe' THEN
        RAISE EXCEPTION 'FALLO GRAVE: se cambió el correo del cliente (%).', r.correo;
    END IF;
    UPDATE clientes SET direccion = 'calle nueva 9' WHERE id = v_cli;
    IF (SELECT direccion FROM clientes WHERE id = v_cli) <> 'CALLE NUEVA 9' THEN
        RAISE EXCEPTION 'FALLO: al editar, la dirección no quedó en mayúsculas.';
    END IF;

    INSERT INTO productos (empresa_id, codigo, descripcion, unidad_medida, tipo_afectacion, precio_unitario)
    VALUES (v_e, 'p-0001', 'arroz extra x 5 kg', 'NIU', '10', 20) RETURNING id INTO v_p;
    IF (SELECT codigo || '|' || descripcion FROM productos WHERE id = v_p) <> 'P-0001|ARROZ EXTRA X 5 KG' THEN
        RAISE EXCEPTION 'FALLO: el producto no quedó en mayúsculas.';
    END IF;

    -- Caja: el nombre y el motivo, sí; el tipo (código de lista), no.
    INSERT INTO pos_cajas (empresa_id, sucursal_id, nombre) VALUES (v_e, v_s, 'caja 01') RETURNING id INTO v_c;
    INSERT INTO pos_sesiones (empresa_id, sucursal_id, caja_id, usuario_id, fondo_inicial) VALUES (v_e, v_s, v_c, v_u, 100) RETURNING id INTO v_ses;
    INSERT INTO pos_movimientos (empresa_id, sesion_id, tipo, monto, motivo) VALUES (v_e, v_ses, 'retiro', 5, 'pago de agua');
    IF (SELECT nombre FROM pos_cajas WHERE id = v_c) <> 'CAJA 01'
       OR NOT EXISTS (SELECT 1 FROM pos_movimientos WHERE sesion_id = v_ses AND tipo = 'retiro' AND motivo = 'PAGO DE AGUA') THEN
        RAISE EXCEPTION 'FALLO: la caja o el retiro no quedaron como se esperaba.';
    END IF;
END;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

-- =========================================================================
RAISE NOTICE '59. Guía por WhatsApp: el enlace abre solo su guía, caduca, y otra empresa no lo ve (057)';
-- =========================================================================
DECLARE
    v_e   uuid;
    v_u   uuid;
    v_g   uuid;
    r     record;
BEGIN
    INSERT INTO empresas (nombre, ruc, razon_social) VALUES ('Guía 057', '20555555057', 'GUIA 057') RETURNING id INTO v_e;
    INSERT INTO usuarios (empresa_id, correo, nombre, clave_hash) VALUES (v_e, 'guia.057@correo.pe', 'Despacho', 'x') RETURNING id INTO v_u;
    PERFORM fijar_contexto(v_u, v_e, '10.0.0.59', 'verificar.sql', false);
    INSERT INTO guias (empresa_id, tipo, serie, fecha_emision, fecha_traslado, motivo, modalidad,
                       destinatario_tipo, destinatario_doc, destinatario_nombre,
                       partida_direccion, partida_ubigeo, llegada_direccion, llegada_ubigeo, peso_bruto, idempotency_key)
    VALUES (v_e, '09', 'T001', current_date, current_date, '01', '02', '6', '20601234567', 'COMERCIAL NORTE SAC',
            'Av. Tupac Amaru 1820', '150110', 'Av. Industrial 450', '150112', 10, 'clave-57')
    RETURNING id INTO v_g;
    INSERT INTO guia_enlaces (empresa_id, guia_id, hash, para, numero, expira_en)
    VALUES (v_e, v_g, '\x0571'::bytea, 'conductor', '51987654321', now() + interval '30 days'),
           (v_e, v_g, '\x0572'::bytea, 'cliente',   '51987654321', now() - interval '1 day');
    BEGIN
        UPDATE guia_enlaces SET expira_en = now() + interval '1 year' WHERE guia_id = v_g;
        IF FOUND THEN RAISE EXCEPTION 'FALLO GRAVE: la aplicación alargó un enlace.'; END IF;
    EXCEPTION WHEN insufficient_privilege THEN NULL;
    END;

    -- Sin sesión (como llega quien abre el enlace).
    PERFORM fijar_contexto(NULL, NULL, '10.0.0.59', 'verificar.sql', false);
    SELECT * INTO r FROM resolver_enlace_guia('\x0571'::bytea);
    IF r.guia_id IS DISTINCT FROM v_g OR r.empresa_id IS DISTINCT FROM v_e THEN
        RAISE EXCEPTION 'FALLO: el enlace vigente no abrió su guía.';
    END IF;
    IF EXISTS (SELECT 1 FROM resolver_enlace_guia('\x0572'::bytea)) THEN
        RAISE EXCEPTION 'FALLO GRAVE: un enlace caducado abrió la guía.';
    END IF;
    IF EXISTS (SELECT 1 FROM resolver_enlace_guia('\x0573'::bytea)) THEN
        RAISE EXCEPTION 'FALLO GRAVE: un enlace que no existe abrió algo.';
    END IF;
    IF EXISTS (SELECT 1 FROM guia_enlaces WHERE guia_id = v_g) THEN
        RAISE EXCEPTION 'FALLO GRAVE: sin sesión se leen los enlaces.';
    END IF;

    PERFORM fijar_contexto(v_usr_b, v_b, '10.0.0.59', 'verificar.sql', false);
    IF EXISTS (SELECT 1 FROM guia_enlaces WHERE empresa_id = v_e) THEN
        RAISE EXCEPTION 'FALLO GRAVE: una empresa ve los enlaces de otra.';
    END IF;

    PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);
    IF (SELECT usos FROM guia_enlaces WHERE hash = '\x0571'::bytea) <> 1 THEN
        RAISE EXCEPTION 'FALLO: no se anotó el uso del enlace.';
    END IF;
END;
PERFORM fijar_contexto(NULL, NULL, '127.0.0.1', 'verificar.sql', true);

RAISE NOTICE '';
RAISE NOTICE 'TODAS LAS COMPROBACIONES PASARON';

END $$;