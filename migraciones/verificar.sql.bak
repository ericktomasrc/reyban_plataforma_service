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
    v_usr_a    uuid;
    v_usr_b    uuid;
    v_cli_a    uuid;
    v_doc      uuid;
    v_n        integer;
    v_txt      text;
    v_ok       boolean;
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

INSERT INTO empresas (ruc, razon_social) VALUES ('20100000001', 'EMPRESA A')
    RETURNING id INTO v_a;
INSERT INTO empresas (ruc, razon_social) VALUES ('20100000002', 'EMPRESA B')
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


RAISE NOTICE '';
RAISE NOTICE 'TODAS LAS COMPROBACIONES PASARON';

END $$;
