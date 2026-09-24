-- =============================================================================
-- 003 — SESIONES, TOKENS Y EVENTOS DE SEGURIDAD
--
-- LA REGLA QUE ORDENA TODO ESTE ARCHIVO:
--
--     Un token se guarda como HASH. Nunca como valor.
--
-- El usuario recibe el token una vez, en su cookie o en su correo. La base
-- guarda solo el SHA-256. Para validarlo se hashea lo que llega y se compara.
--
-- Consecuencia: quien robe un volcado de la base **no puede** suplantar a
-- nadie, porque de un hash no se saca el token. Si en cambio guardáramos el
-- token tal cual, un respaldo filtrado sería una sesión abierta por cada
-- usuario conectado.
--
-- SHA-256 a secas basta aquí, y no hace falta PBKDF2, porque un token lo
-- elegimos nosotros: son 32 bytes aleatorios y no hay diccionario que los
-- adivine. Las contraseñas son lo contrario —las elige una persona— y por eso
-- allí sí van 210.000 iteraciones.
--
-- LA EXCEPCIÓN, Y ESTÁ EN LA MIGRACIÓN 005: la clave de API del servicio de
-- facturación se guarda CIFRADA, no hasheada. Es la única forma, porque hay
-- que poder recuperarla para enviarla en cada llamada. Un hash no se
-- desanda.
-- =============================================================================


-- =============================================================================
-- SESIONES
--
-- La cookie lleva un token aleatorio; esta tabla dice si sigue siendo válido.
--
-- POR QUÉ NO UN JWT AUTOCONTENIDO: un JWT vale hasta que expira, y no hay
-- forma de anularlo. Si despides a alguien a las 10 de la mañana, su token
-- sigue funcionando hasta que caduque. Con una fila en una tabla, cerrarle
-- la puerta es un UPDATE.
-- =============================================================================

CREATE TABLE sesiones (
    id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    usuario_id        uuid        NOT NULL REFERENCES usuarios (id),
    empresa_id        uuid        REFERENCES empresas (id),

    -- SHA-256 del token que viaja en la cookie. 32 bytes.
    token_hash        bytea       NOT NULL,

    creada_en         timestamptz NOT NULL DEFAULT now(),
    expira_en         timestamptz NOT NULL,
    ultima_actividad  timestamptz NOT NULL DEFAULT now(),

    ip                inet,
    agente            text,

    -- El segundo factor es un estado de la sesión, no del usuario: una sesión
    -- recién creada existe pero todavía no ha superado el 2FA, y hasta que lo
    -- haga no puede hacer nada más que validarlo.
    dosfa_superado    boolean     NOT NULL DEFAULT false,

    revocada_en       timestamptz,
    motivo_revocacion text
);

CREATE UNIQUE INDEX ux_sesiones_token ON sesiones (token_hash);
CREATE INDEX ix_sesiones_usuario ON sesiones (usuario_id, creada_en DESC);
CREATE INDEX ix_sesiones_vivas   ON sesiones (expira_en) WHERE revocada_en IS NULL;

COMMENT ON TABLE sesiones IS
    'Se actualiza (ultima_actividad, revocada_en) pero nunca se borra: el historial de sesiones es parte de la auditoría.';


-- =============================================================================
-- TOKENS DE UN SOLO USO
--
-- Restablecer contraseña, verificar correo, invitar a un usuario. Todos son
-- el mismo mecanismo y por eso comparten tabla: un valor aleatorio, con
-- propósito, con caducidad, y que se quema al usarse.
--
-- LO QUE HACE QUE SEA "DE UN SOLO USO" es la columna `usado_en`, no la buena
-- voluntad del código. Un enlace de restablecer contraseña que funcione dos
-- veces es un enlace que sigue en la bandeja de entrada del correo y sirve
-- meses después.
-- =============================================================================

CREATE TABLE tokens_un_uso (
    id           uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    usuario_id   uuid        NOT NULL REFERENCES usuarios (id),

    proposito    text        NOT NULL CHECK (proposito IN (
                                 'restablecer_clave',
                                 'verificar_correo',
                                 'invitacion'
                             )),

    token_hash   bytea       NOT NULL,

    creado_en    timestamptz NOT NULL DEFAULT now(),
    creado_por   uuid,
    expira_en    timestamptz NOT NULL,
    usado_en     timestamptz,
    usado_desde  inet,

    -- Un enlace nuevo invalida el anterior. Sin esto, pedir tres veces
    -- "olvidé mi contraseña" deja tres llaves buenas circulando.
    anulado_en   timestamptz
);

CREATE UNIQUE INDEX ux_tokens_un_uso ON tokens_un_uso (token_hash);
CREATE INDEX ix_tokens_usuario ON tokens_un_uso (usuario_id, proposito, creado_en DESC);


-- =============================================================================
-- CÓDIGOS DE RECUPERACIÓN DEL SEGUNDO FACTOR
--
-- Ocho por usuario, de un solo uso. Sin ellos, perder el teléfono significa
-- quedarse fuera para siempre.
--
-- Se guardan hasheados por la misma razón que todo lo demás en este archivo.
-- =============================================================================

CREATE TABLE codigos_recuperacion (
    id          uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    usuario_id  uuid        NOT NULL REFERENCES usuarios (id),
    codigo_hash bytea       NOT NULL,

    creado_en   timestamptz NOT NULL DEFAULT now(),
    usado_en    timestamptz,
    usado_desde inet
);

CREATE UNIQUE INDEX ux_codigos_recuperacion ON codigos_recuperacion (codigo_hash);
CREATE INDEX ix_codigos_usuario ON codigos_recuperacion (usuario_id) WHERE usado_en IS NULL;


-- =============================================================================
-- EVENTOS DE SEGURIDAD
--
-- La bitácora de la migración 001 registra cambios en FILAS. Esta tabla
-- registra INTENTOS, que es otra cosa y no deja rastro en ninguna fila:
--
--   · un ingreso fallido no modifica nada
--   · un 403 por falta de permiso no modifica nada
--   · un token caducado que alguien intentó usar no modifica nada
--
-- Y son exactamente los que se quieren ver cuando algo huele mal.
--
-- SOLO-INSERCIÓN, igual que la bitácora.
-- =============================================================================

CREATE TABLE eventos_seguridad (
    id          bigserial   PRIMARY KEY,
    ocurrido_en timestamptz NOT NULL DEFAULT now(),

    tipo        text        NOT NULL,
    exito       boolean     NOT NULL,

    -- Nullable a propósito: un intento de ingreso con un correo que no existe
    -- no tiene usuario, y ese caso es justo el que interesa vigilar.
    usuario_id  uuid,
    empresa_id  uuid,
    correo      text,

    ip          inet,
    agente      text,

    -- Contexto libre. NUNCA contraseñas, tokens ni claves: lo que se guarda
    -- aquí se guarda para siempre y se lee desde una pantalla de soporte.
    detalle     jsonb,

    CONSTRAINT ck_eventos_tipo CHECK (tipo IN (
        -- Ingreso
        'ingreso',
        'ingreso_clave_incorrecta',
        'ingreso_usuario_inexistente',
        'ingreso_usuario_inactivo',
        'ingreso_usuario_bloqueado',
        'ingreso_empresa_suspendida',
        'cierre_sesion',
        'sesion_expirada',
        'sesion_revocada',
        -- Segundo factor
        'dosfa_superado',
        'dosfa_fallido',
        'dosfa_activado',
        'dosfa_desactivado',
        'codigo_recuperacion_usado',
        -- Contraseñas
        'clave_cambiada',
        'clave_restablecer_solicitado',
        'clave_restablecer_usado',
        'clave_restablecer_invalido',
        -- Autorización
        'acceso_denegado',
        'permiso_otorgado',
        'permiso_retirado',
        'rol_asignado',
        'rol_retirado',
        'suplantacion_iniciada',
        'suplantacion_terminada',
        -- Empresas y credenciales
        'empresa_suspendida',
        'empresa_reactivada',
        'credencial_facturacion_guardada',
        'credencial_facturacion_rotada',
        'credencial_facturacion_usada_fallo',
        -- Genérico para lo que venga
        'otro'
    ))
);

CREATE INDEX ix_eventos_fecha   ON eventos_seguridad (ocurrido_en DESC);
CREATE INDEX ix_eventos_usuario ON eventos_seguridad (usuario_id, ocurrido_en DESC);
CREATE INDEX ix_eventos_empresa ON eventos_seguridad (empresa_id, ocurrido_en DESC);
CREATE INDEX ix_eventos_tipo    ON eventos_seguridad (tipo, ocurrido_en DESC);
CREATE INDEX ix_eventos_ip      ON eventos_seguridad (ip, ocurrido_en DESC);

-- Los fallos son lo que se consulta bajo presión. Un índice parcial los deja
-- en una fracción del tamaño del índice completo.
CREATE INDEX ix_eventos_fallos ON eventos_seguridad (ocurrido_en DESC) WHERE NOT exito;

CREATE TRIGGER eventos_seguridad_inmutable
    BEFORE UPDATE OR DELETE ON eventos_seguridad
    FOR EACH ROW EXECUTE FUNCTION fn_solo_insercion();


-- --- Función de registro -----------------------------------------------------
--
-- Existe para que el código no arme el INSERT a mano en veinte sitios. Toma
-- la IP, el agente y el usuario del contexto si no se le pasan.

CREATE OR REPLACE FUNCTION registrar_evento(
    p_tipo       text,
    p_exito      boolean,
    p_usuario_id uuid  DEFAULT NULL,
    p_empresa_id uuid  DEFAULT NULL,
    p_correo     text  DEFAULT NULL,
    p_detalle    jsonb DEFAULT NULL
) RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_id bigint;
BEGIN
    INSERT INTO eventos_seguridad
        (tipo, exito, usuario_id, empresa_id, correo, ip, agente, detalle)
    VALUES
        (p_tipo, p_exito,
         COALESCE(p_usuario_id, ctx_usuario()),
         COALESCE(p_empresa_id, ctx_empresa()),
         p_correo, ctx_ip(), ctx_agente(), p_detalle)
    RETURNING id INTO v_id;

    RETURN v_id;
END $$;


-- =============================================================================
-- LAS DOS PUERTAS DEL INGRESO
--
-- EL PROBLEMA QUE RESUELVEN, que es consecuencia directa de tener el
-- aislamiento bien puesto:
--
-- Cuando alguien escribe su correo y su contraseña, el sistema TODAVÍA NO
-- SABE de qué empresa es. Sin empresa en el contexto, las políticas de RLS
-- hacen que la tabla `usuarios` esté vacía para esa consulta — y el ingreso
-- responde «usuario o contraseña incorrectos» a todo el mundo, para siempre.
--
-- Lo mismo al llegar una petición con su cookie: para saber a quién pertenece
-- la sesión hay que leerla antes de poder fijar el contexto.
--
-- LA SALIDA: dos funciones, y solo dos, marcadas SECURITY DEFINER. Corren con
-- los permisos del propietario y por tanto ven todas las filas.
--
-- Se aceptan porque son estrechas y están a la vista:
--
--   · devuelven exactamente los campos que hacen falta, ni uno más
--   · no devuelven el secreto del segundo factor ni nada cifrado
--   · se buscan por un valor que solo tiene quien ya lo tenía: el correo
--     completo, o el hash de un token de 32 bytes aleatorios
--
-- Todo lo demás del sistema sigue bajo RLS. Estas dos son la puerta, y una
-- puerta se reconoce porque está marcada.
-- =============================================================================

CREATE OR REPLACE FUNCTION buscar_usuario_para_ingreso(p_correo text)
RETURNS TABLE (
    id                   uuid,
    empresa_id           uuid,
    nombre               text,
    clave_hash           text,
    activo               boolean,
    es_super_admin       boolean,
    intentos_fallidos    integer,
    bloqueado_hasta      timestamptz,
    dosfa_activo         boolean,
    clave_cambio_forzado boolean,
    empresa_operativa    boolean
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT u.id,
           u.empresa_id,
           u.nombre,
           u.clave_hash,
           u.activo,
           u.es_super_admin,
           u.intentos_fallidos,
           u.bloqueado_hasta,
           u.dosfa_activo,
           u.clave_cambio_forzado,
           -- Un super administrador no tiene empresa: para él, siempre true.
           COALESCE(e.activo AND e.suspendida_en IS NULL, true)
      FROM usuarios u
      LEFT JOIN empresas e ON e.id = u.empresa_id
     WHERE lower(u.correo) = lower(p_correo);
$$;

COMMENT ON FUNCTION buscar_usuario_para_ingreso IS
    'SECURITY DEFINER a propósito: durante el ingreso todavía no hay empresa en el contexto. No devuelve secreto_2fa ni nada cifrado.';


CREATE OR REPLACE FUNCTION resolver_sesion(p_token_hash bytea)
RETURNS TABLE (
    sesion_id            uuid,
    usuario_id           uuid,
    empresa_id           uuid,
    es_super_admin       boolean,
    dosfa_superado       boolean,
    clave_cambio_forzado boolean,
    expira_en            timestamptz,
    revocada_en          timestamptz,
    usuario_activo       boolean,
    empresa_operativa    boolean
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT s.id,
           s.usuario_id,
           s.empresa_id,
           u.es_super_admin,
           s.dosfa_superado,
           -- Viaja en cada petición porque un cambio de contraseña obligatorio
           -- que el usuario puede ignorar no es obligatorio. Con este dato, el
           -- filtro de autorización cierra todo salvo la propia pantalla de
           -- cambio.
           u.clave_cambio_forzado,
           s.expira_en,
           s.revocada_en,
           u.activo,
           COALESCE(e.activo AND e.suspendida_en IS NULL, true)
      FROM sesiones s
      JOIN usuarios u ON u.id = s.usuario_id
      LEFT JOIN empresas e ON e.id = u.empresa_id
     WHERE s.token_hash = p_token_hash;
$$;

COMMENT ON FUNCTION resolver_sesion IS
    'SECURITY DEFINER a propósito: para saber de quién es la sesión hay que leerla antes de poder fijar el contexto. Se busca por el hash de un token de 32 bytes aleatorios.';


-- --- La tercera puerta, y la más pequeña ------------------------------------
--
-- EL PROBLEMA: el correo es único en TODA la plataforma, pero el aislamiento
-- solo deja ver los usuarios de la propia empresa. Así que al crear un
-- usuario, la comprobación «¿está libre este correo?» responde que sí aunque
-- lo tenga alguien de otra empresa — y el INSERT se estrella contra el índice
-- único con un error que no le dice nada a nadie.
--
-- NO BASTA CON ESCRIBIR LA CONSULTA EN SQL CRUDO. El Row Level Security actúa
-- en el motor, por debajo de EF: da igual si la consulta la escribe EF o se
-- manda a mano. Ignorar los filtros de EF con IgnoreQueryFilters() tampoco
-- sirve, porque esos son otra capa. La única forma es una función marcada.
--
-- DEVUELVE UN BOOLEANO Y NADA MÁS. No dice de quién es el correo ni de qué
-- empresa: solo si está ocupado, que es lo que el índice único ya haría
-- público de todas formas.

CREATE OR REPLACE FUNCTION correo_disponible(p_correo text)
RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT NOT EXISTS (
        SELECT 1 FROM usuarios WHERE lower(correo) = lower(p_correo)
    );
$$;

COMMENT ON FUNCTION correo_disponible IS
    'SECURITY DEFINER: el correo es único globalmente pero el aislamiento solo deja ver la propia empresa. Devuelve solo un booleano.';


-- =============================================================================
-- VIGILANCIA Y AISLAMIENTO
--
-- Las sesiones y los tokens llevan RLS igual que todo lo demás. Un
-- administrador de empresa debe poder ver y cerrar las sesiones de SU gente,
-- nunca las de otra.
-- =============================================================================

ALTER TABLE sesiones             ENABLE ROW LEVEL SECURITY;
ALTER TABLE tokens_un_uso        ENABLE ROW LEVEL SECURITY;
ALTER TABLE codigos_recuperacion ENABLE ROW LEVEL SECURITY;
ALTER TABLE eventos_seguridad    ENABLE ROW LEVEL SECURITY;

ALTER TABLE sesiones             FORCE ROW LEVEL SECURITY;
ALTER TABLE tokens_un_uso        FORCE ROW LEVEL SECURITY;
ALTER TABLE codigos_recuperacion FORCE ROW LEVEL SECURITY;
ALTER TABLE eventos_seguridad    FORCE ROW LEVEL SECURITY;

-- OJO con la política de sesiones: durante el ingreso todavía no hay empresa
-- en el contexto, porque se está averiguando quién es. Por eso se permite la
-- fila cuando el contexto está vacío — es el único momento en que eso pasa, y
-- dura una consulta.
CREATE POLICY p_sesiones ON sesiones
    USING (ctx_es_super() OR ctx_empresa() IS NULL OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR ctx_empresa() IS NULL OR empresa_id = ctx_empresa());

CREATE POLICY p_tokens_un_uso ON tokens_un_uso
    USING (true) WITH CHECK (true);

CREATE POLICY p_codigos_recuperacion ON codigos_recuperacion
    USING (true) WITH CHECK (true);

COMMENT ON TABLE tokens_un_uso IS
    'Sin filtro por empresa: al usar un enlace de restablecer contraseña, el usuario todavía no ha iniciado sesión y no hay empresa en el contexto. La seguridad la da el hash del token, que es lo único que permite encontrar la fila.';

CREATE POLICY p_eventos_seguridad ON eventos_seguridad
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (true);   -- registrar siempre se puede; leer, no


SELECT vigilar_tabla_inmutable('eventos_seguridad');

-- Las sesiones y los tokens SÍ se actualizan, así que van con el disparador
-- normal. Se ocultan los hashes: una bitácora con el hash del token de sesión
-- dentro sería tan peligrosa como la tabla original.
-- LA BITÁCORA DE SESIONES IGNORA `ultima_actividad` A PROPÓSITO.
--
-- Esa columna se toca cada pocos minutos mientras alguien usa el sistema. Si
-- cada roce dejara una fila, la bitácora se llenaría de ruido que tapa lo que
-- sí importa: cuándo se abrió una sesión, cuándo se revocó y cuándo superó el
-- segundo factor.
--
-- `UPDATE OF <columnas>` hace que el disparador solo salte cuando cambia algo
-- de eso.

CREATE TRIGGER auditar_sesiones
    AFTER INSERT OR DELETE ON sesiones
    FOR EACH ROW EXECUTE FUNCTION fn_auditar('token_hash');

CREATE TRIGGER auditar_sesiones_cambios
    AFTER UPDATE OF revocada_en, motivo_revocacion, dosfa_superado, expira_en
    ON sesiones
    FOR EACH ROW EXECUTE FUNCTION fn_auditar('token_hash');

CREATE TRIGGER auditar_tokens_un_uso
    AFTER INSERT OR UPDATE OR DELETE ON tokens_un_uso
    FOR EACH ROW EXECUTE FUNCTION fn_auditar('token_hash');

CREATE TRIGGER auditar_codigos_recuperacion
    AFTER INSERT OR UPDATE OR DELETE ON codigos_recuperacion
    FOR EACH ROW EXECUTE FUNCTION fn_auditar('codigo_hash');


GRANT SELECT, INSERT, UPDATE ON sesiones, tokens_un_uso, codigos_recuperacion
    TO plataforma_app;
GRANT SELECT, INSERT ON eventos_seguridad TO plataforma_app;
GRANT USAGE ON SEQUENCE eventos_seguridad_id_seq TO plataforma_app;
GRANT EXECUTE ON FUNCTION registrar_evento(text, boolean, uuid, uuid, text, jsonb)
    TO plataforma_app;
GRANT EXECUTE ON FUNCTION buscar_usuario_para_ingreso(text) TO plataforma_app;
GRANT EXECUTE ON FUNCTION resolver_sesion(bytea)            TO plataforma_app;
GRANT EXECUTE ON FUNCTION correo_disponible(text)           TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('003_seguridad');
