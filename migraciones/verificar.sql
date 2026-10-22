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
RAISE NOTICE '42. Facturas: contado o crédito con cuotas, y la detracción solo en facturas (040)';
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

RAISE NOTICE '';
RAISE NOTICE 'TODAS LAS COMPROBACIONES PASARON';

END $$;