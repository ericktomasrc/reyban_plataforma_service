-- =============================================================================
-- 001 — FUNDACIÓN
--
-- Esta migración no crea ni una sola tabla de negocio. Crea las reglas que
-- todas las demás van a obedecer:
--
--   · el contexto de sesión (quién es el usuario, de qué empresa, desde dónde)
--   · el marcado automático de los campos de auditoría
--   · la bitácora general, que registra TODO cambio en TODA tabla
--   · el bloqueo de escritura sobre la bitácora
--
-- POR QUÉ VA PRIMERO Y SEPARADO:
--
-- Si los campos de auditoría los rellenara la aplicación, serían una promesa.
-- Rellenados por un disparador, son un hecho: no hay forma de escribir una
-- fila mintiendo sobre quién la creó, ni siquiera con un INSERT a mano desde
-- psql.
-- =============================================================================


-- --- Extensiones -------------------------------------------------------------
-- gen_random_uuid() es nativo desde PostgreSQL 13. pgcrypto solo hace falta
-- para digest(), que usamos al guardar hashes de tokens.

CREATE EXTENSION IF NOT EXISTS pgcrypto;


-- --- El rol de la aplicación -------------------------------------------------
--
-- La aplicación NO se conecta como propietario de las tablas. Se conecta con
-- este rol, que tiene exactamente los permisos que necesita y ninguno más.
--
-- Es lo que hace que "la bitácora es solo-inserción" sea cierto: no es una
-- convención del código, es que el rol no tiene el permiso.
--
-- IMPORTANTE: este rol NO debe tener BYPASSRLS ni SUPERUSER. Si lo tuviera,
-- el aislamiento entre empresas dejaría de existir sin que nadie se entere.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'plataforma_app') THEN
        CREATE ROLE plataforma_app LOGIN PASSWORD 'CAMBIAR_EN_DESPLIEGUE';
    END IF;
END $$;

ALTER ROLE plataforma_app NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE;


-- =============================================================================
-- EL CONTEXTO DE SESIÓN
--
-- Antes de cada petición, el middleware ejecuta:
--
--     SELECT fijar_contexto(:usuario, :empresa, :ip, :agente);
--
-- Esos valores viven en la conexión mientras dure la transacción y los leen
-- dos cosas: las políticas RLS (para filtrar por empresa) y el disparador de
-- auditoría (para saber quién hizo el cambio).
--
-- El tercer parámetro de set_config es `true` = local a la transacción. Es
-- deliberado: con un pool de conexiones, un valor que sobreviviera a la
-- transacción se filtraría a la petición del siguiente usuario.
-- =============================================================================

CREATE OR REPLACE FUNCTION fijar_contexto(
    p_usuario uuid,
    p_empresa uuid,
    p_ip      text    DEFAULT NULL,
    p_agente  text    DEFAULT NULL,
    p_super   boolean DEFAULT false
) RETURNS void
LANGUAGE sql AS $$
    SELECT set_config('app.usuario_id', COALESCE(p_usuario::text, ''), true),
           set_config('app.empresa_id', COALESCE(p_empresa::text, ''), true),
           set_config('app.ip',         COALESCE(p_ip,            ''), true),
           set_config('app.agente',     COALESCE(p_agente,        ''), true),
           set_config('app.super',      CASE WHEN p_super THEN '1' ELSE '' END, true);
    SELECT NULL::void;
$$;

-- Lectores del contexto. Devuelven NULL en vez de reventar cuando no se fijó,
-- porque las migraciones y los scripts de mantenimiento corren sin contexto.

CREATE OR REPLACE FUNCTION ctx_usuario() RETURNS uuid
LANGUAGE sql STABLE AS $$
    SELECT NULLIF(current_setting('app.usuario_id', true), '')::uuid;
$$;

CREATE OR REPLACE FUNCTION ctx_empresa() RETURNS uuid
LANGUAGE sql STABLE AS $$
    SELECT NULLIF(current_setting('app.empresa_id', true), '')::uuid;
$$;

CREATE OR REPLACE FUNCTION ctx_ip() RETURNS inet
LANGUAGE sql STABLE AS $$
    SELECT NULLIF(current_setting('app.ip', true), '')::inet;
$$;

CREATE OR REPLACE FUNCTION ctx_agente() RETURNS text
LANGUAGE sql STABLE AS $$
    SELECT NULLIF(current_setting('app.agente', true), '');
$$;

-- El super administrador ve todas las empresas. Es la única excepción al
-- aislamiento, y por eso vive en su propia bandera y no en un permiso más:
-- se quiere que cueste escribirla y que se vea de lejos en cada política.
CREATE OR REPLACE FUNCTION ctx_es_super() RETURNS boolean
LANGUAGE sql STABLE AS $$
    SELECT COALESCE(NULLIF(current_setting('app.super', true), ''), '0') = '1';
$$;


-- =============================================================================
-- LOS CAMPOS DE AUDITORÍA, RELLENADOS POR LA BASE
--
-- Toda tabla de negocio lleva estas seis columnas:
--
--     creado_en        timestamptz  NOT NULL
--     creado_por       uuid         (NULL = lo hizo el sistema, no una persona)
--     modificado_en    timestamptz
--     modificado_por   uuid
--     activo           boolean      NOT NULL DEFAULT true
--     version          integer      NOT NULL DEFAULT 1
--
-- VERSION ES PARA CONCURRENCIA OPTIMISTA. Dos usuarios abren el mismo cliente,
-- los dos guardan: sin esta columna, el segundo pisa al primero en silencio.
-- Con ella, EF Core detecta el choque y avisa.
--
-- ACTIVO ES PARA NO BORRAR NUNCA. Una fila desactivada sigue ahí, con su
-- historia intacta. Borrar de verdad rompe las referencias de los documentos
-- viejos, que es justo lo que una auditoría tributaria va a mirar.
-- =============================================================================

CREATE OR REPLACE FUNCTION fn_marcar_auditoria() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_usuario uuid := ctx_usuario();
BEGIN
    IF TG_OP = 'INSERT' THEN
        NEW.creado_en      := now();
        NEW.creado_por     := v_usuario;
        NEW.modificado_en  := NULL;
        NEW.modificado_por := NULL;
        NEW.version        := 1;

    ELSIF TG_OP = 'UPDATE' THEN
        -- Quien creó la fila no cambia jamás, diga lo que diga la aplicación.
        NEW.creado_en      := OLD.creado_en;
        NEW.creado_por     := OLD.creado_por;

        -- UN UPDATE QUE NO CAMBIA NADA NO ES UN CAMBIO.
        --
        -- Se compara la fila entera ignorando las columnas de auditoría. Si
        -- son iguales, se devuelve OLD y la fila queda exactamente como
        -- estaba: misma versión, misma fecha de modificación.
        --
        -- Importa por dos motivos. Uno, que `version` y la bitácora cuenten
        -- la misma historia: sería raro que la bitácora dijera "aquí no pasó
        -- nada" y la versión hubiera subido. Dos, que abrir un formulario y
        -- guardarlo sin tocar nada no haga fallar el guardado de otro
        -- usuario por conflicto de concurrencia.
        IF (to_jsonb(NEW) - 'modificado_en' - 'modificado_por' - 'version')
         = (to_jsonb(OLD) - 'modificado_en' - 'modificado_por' - 'version') THEN
            RETURN OLD;
        END IF;

        NEW.modificado_en  := now();
        NEW.modificado_por := v_usuario;
        NEW.version        := OLD.version + 1;
    END IF;

    RETURN NEW;
END $$;


-- =============================================================================
-- LA BITÁCORA GENERAL
--
-- Una fila por cada INSERT, UPDATE y DELETE de cada tabla vigilada, con la
-- fila completa antes y después.
--
-- POR QUÉ UNA SOLA TABLA GENÉRICA Y NO UNA POR CADA COSA:
--
-- Porque "registrar todo sin excepción" y "acordarse de registrar" son
-- incompatibles. El día que alguien añada una tabla y olvide su bitácora, el
-- agujero no se nota hasta que hace falta. Aquí solo hay que acordarse de
-- enganchar un disparador, y eso se revisa con una consulta (ver el final del
-- archivo).
--
-- ESTO NO SUSTITUYE A LOS HISTORIALES DE NEGOCIO. La bitácora responde "quién
-- tocó esta fila y qué cambió". Un historial de documento responde "por qué
-- este comprobante está rechazado". Son preguntas distintas y ambas hacen
-- falta.
-- =============================================================================

CREATE TABLE bitacora (
    id               bigserial    PRIMARY KEY,
    ocurrido_en      timestamptz  NOT NULL DEFAULT now(),

    tabla            text         NOT NULL,
    fila_id          text         NOT NULL,
    operacion        char(1)      NOT NULL CHECK (operacion IN ('I','U','D')),

    -- Quién y desde dónde. NULL en usuario significa que no había sesión:
    -- una migración, un trabajo de fondo o un script de mantenimiento.
    empresa_id       uuid,
    usuario_id       uuid,
    ip               inet,
    agente           text,

    antes            jsonb,
    despues          jsonb,
    campos_cambiados text[]
);

COMMENT ON TABLE bitacora IS
    'Solo-inserción. Nunca UPDATE ni DELETE. El rol de la aplicación no tiene esos permisos.';

CREATE INDEX ix_bitacora_tabla_fila ON bitacora (tabla, fila_id, ocurrido_en DESC);
CREATE INDEX ix_bitacora_usuario    ON bitacora (usuario_id, ocurrido_en DESC);
CREATE INDEX ix_bitacora_empresa    ON bitacora (empresa_id, ocurrido_en DESC);
CREATE INDEX ix_bitacora_fecha      ON bitacora (ocurrido_en DESC);


-- --- El disparador de auditoría ----------------------------------------------
--
-- Se engancha así, listando entre comillas las columnas que NO deben quedar
-- copiadas en la bitácora:
--
--     CREATE TRIGGER aud_usuarios
--         AFTER INSERT OR UPDATE OR DELETE ON usuarios
--         FOR EACH ROW EXECUTE FUNCTION fn_auditar('clave_hash','secreto_2fa');
--
-- ESA LISTA DE OCULTOS ES LA PARTE MÁS IMPORTANTE DE TODO EL ARCHIVO.
--
-- Sin ella, la bitácora copiaría el hash de cada contraseña, el secreto del
-- segundo factor y la clave de API cifrada — y los guardaría para siempre, en
-- una tabla que nadie revisa y que se respalda a diario. Se habría construido
-- una auditoría que es, ella misma, la peor filtración del sistema.

CREATE OR REPLACE FUNCTION fn_auditar() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_antes     jsonb;
    v_despues   jsonb;
    v_cambios   text[];
    v_sensibles text[] := COALESCE(TG_ARGV, ARRAY[]::text[]);
    v_col       text;
    v_fila_id   text;
    v_empresa   uuid;

    -- Columnas que cambian en todo UPDATE y no dicen nada por sí solas.
    -- Si son las únicas que cambiaron, no hubo cambio real.
    v_ruido text[] := ARRAY['modificado_en','modificado_por','version'];
BEGIN
    IF TG_OP IN ('UPDATE','DELETE') THEN v_antes   := to_jsonb(OLD); END IF;
    IF TG_OP IN ('INSERT','UPDATE') THEN v_despues := to_jsonb(NEW); END IF;

    -- EL ORDEN DE ESTOS DOS PASOS NO ES INTERCAMBIABLE, y es el error que más
    -- caro sale de todo este archivo.
    --
    -- Primero se calcula QUÉ cambió, sobre los valores de verdad. Solo
    -- después se tapan.
    --
    -- Al revés —tapando primero— un cambio de contraseña compararía
    -- '[oculto]' contra '[oculto]', el disparador concluiría que no cambió
    -- nada, y **no registraría absolutamente nada**. Justo el movimiento que
    -- más falta hace vigilar sería el único invisible.

    IF TG_OP = 'UPDATE' THEN
        SELECT array_agg(d.clave ORDER BY d.clave)
          INTO v_cambios
          FROM jsonb_each(v_despues) AS d(clave, valor)
         WHERE v_antes -> d.clave IS DISTINCT FROM d.valor
           AND NOT (d.clave = ANY (v_ruido));

        -- Nada cambió de verdad: no ensuciar la bitácora.
        IF v_cambios IS NULL THEN
            RETURN NULL;
        END IF;
    END IF;

    -- Ahora sí, tapar los valores sensibles. Queda el hecho de que cambiaron
    -- —en `campos_cambiados`— sin quedar el valor.
    FOREACH v_col IN ARRAY v_sensibles LOOP
        IF v_antes   ? v_col THEN v_antes   := jsonb_set(v_antes,   ARRAY[v_col], '"[oculto]"'); END IF;
        IF v_despues ? v_col THEN v_despues := jsonb_set(v_despues, ARRAY[v_col], '"[oculto]"'); END IF;
    END LOOP;

    -- Casi todas las tablas tienen `id`. Los catálogos (permisos, modulos)
    -- usan `codigo` como clave, y también se vigilan.
    v_fila_id := COALESCE(
        v_despues ->> 'id',     v_antes ->> 'id',
        v_despues ->> 'codigo', v_antes ->> 'codigo'
    );

    -- La empresa sale de la propia fila; si la tabla no la tiene (catálogos
    -- globales), se usa la del contexto.
    v_empresa := COALESCE(
        NULLIF(v_despues ->> 'empresa_id', '')::uuid,
        NULLIF(v_antes   ->> 'empresa_id', '')::uuid,
        ctx_empresa()
    );

    INSERT INTO bitacora (
        tabla, fila_id, operacion,
        empresa_id, usuario_id, ip, agente,
        antes, despues, campos_cambiados
    ) VALUES (
        TG_TABLE_NAME,
        COALESCE(v_fila_id, '(sin id)'),
        LEFT(TG_OP, 1),
        v_empresa, ctx_usuario(), ctx_ip(), ctx_agente(),
        v_antes, v_despues, v_cambios
    );

    RETURN NULL;   -- AFTER trigger: el valor de retorno se ignora
END $$;


-- --- La bitácora no se toca --------------------------------------------------
--
-- Dos candados, a propósito redundantes:
--
--   1. El rol de la aplicación solo tiene INSERT y SELECT (más abajo).
--   2. Este disparador, que salta aunque quien lo intente sea el propietario.
--
-- El segundo existe porque el primero se puede deshacer con un GRANT
-- despistado. Un candado que depende de que nadie se equivoque no es un
-- candado.

CREATE OR REPLACE FUNCTION fn_solo_insercion() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION
        'La tabla % es de solo inserción. No se puede % una fila ya escrita.',
        TG_TABLE_NAME, TG_OP;
END $$;

CREATE TRIGGER bitacora_inmutable
    BEFORE UPDATE OR DELETE ON bitacora
    FOR EACH ROW EXECUTE FUNCTION fn_solo_insercion();


-- =============================================================================
-- AYUDA PARA ENGANCHAR LOS DISPARADORES
--
-- Las migraciones siguientes llaman a esto en vez de repetir el CREATE TRIGGER
-- tres veces por tabla.
-- =============================================================================

CREATE OR REPLACE FUNCTION vigilar_tabla(
    p_tabla     text,
    p_sensibles text[] DEFAULT ARRAY[]::text[]
) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_args text := '';
BEGIN
    IF array_length(p_sensibles, 1) > 0 THEN
        SELECT string_agg(quote_literal(c), ', ') INTO v_args
          FROM unnest(p_sensibles) AS c;
    END IF;

    EXECUTE format(
        'CREATE TRIGGER marcar_%1$s BEFORE INSERT OR UPDATE ON %1$I
             FOR EACH ROW EXECUTE FUNCTION fn_marcar_auditoria()', p_tabla);

    EXECUTE format(
        'CREATE TRIGGER auditar_%1$s AFTER INSERT OR UPDATE OR DELETE ON %1$I
             FOR EACH ROW EXECUTE FUNCTION fn_auditar(%2$s)', p_tabla, v_args);
END $$;

COMMENT ON FUNCTION vigilar_tabla IS
    'Engancha el marcado de auditoría y la bitácora. El segundo parámetro lista las columnas que NO deben copiarse a la bitácora.';


-- Variante para tablas de solo-inserción (historiales, eventos): no llevan
-- campos de modificación, así que no necesitan el marcado, solo la bitácora
-- y el candado.

CREATE OR REPLACE FUNCTION vigilar_tabla_inmutable(
    p_tabla     text,
    p_sensibles text[] DEFAULT ARRAY[]::text[]
) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_args text := '';
BEGIN
    IF array_length(p_sensibles, 1) > 0 THEN
        SELECT string_agg(quote_literal(c), ', ') INTO v_args
          FROM unnest(p_sensibles) AS c;
    END IF;

    EXECUTE format(
        'CREATE TRIGGER inmutable_%1$s BEFORE UPDATE OR DELETE ON %1$I
             FOR EACH ROW EXECUTE FUNCTION fn_solo_insercion()', p_tabla);

    EXECUTE format(
        'CREATE TRIGGER auditar_%1$s AFTER INSERT ON %1$I
             FOR EACH ROW EXECUTE FUNCTION fn_auditar(%2$s)', p_tabla, v_args);
END $$;


-- =============================================================================
-- LA CONSULTA QUE DETECTA TABLAS SIN VIGILAR
--
-- Correrla después de cada migración. Si devuelve filas, alguien creó una
-- tabla y olvidó su bitácora.
--
--   SELECT * FROM tablas_sin_vigilar;
-- =============================================================================

CREATE OR REPLACE VIEW tablas_sin_vigilar AS
SELECT c.relname AS tabla
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
 WHERE n.nspname = 'public'
   AND c.relkind = 'r'
   AND c.relname NOT IN ('bitacora', 'migraciones_aplicadas')
   AND NOT EXISTS (
        SELECT 1 FROM pg_trigger t
         WHERE t.tgrelid = c.oid
           AND NOT t.tgisinternal
           AND t.tgname LIKE 'auditar_%'
   )
 ORDER BY 1;


-- =============================================================================
-- REGISTRO DE MIGRACIONES
--
-- Las migraciones se corren a mano, en orden, una sola vez. Esta tabla es lo
-- que hace que "una sola vez" sea comprobable.
-- =============================================================================

CREATE TABLE migraciones_aplicadas (
    nombre      text        PRIMARY KEY,
    aplicada_en timestamptz NOT NULL DEFAULT now()
);

INSERT INTO migraciones_aplicadas (nombre) VALUES ('001_fundacion');


-- =============================================================================
-- PERMISOS DEL ROL DE APLICACIÓN
-- =============================================================================

GRANT USAGE ON SCHEMA public TO plataforma_app;

GRANT SELECT, INSERT ON bitacora TO plataforma_app;
GRANT USAGE ON SEQUENCE bitacora_id_seq TO plataforma_app;
GRANT SELECT ON migraciones_aplicadas TO plataforma_app;
GRANT SELECT ON tablas_sin_vigilar TO plataforma_app;

GRANT EXECUTE ON FUNCTION fijar_contexto(uuid, uuid, text, text, boolean) TO plataforma_app;
GRANT EXECUTE ON FUNCTION ctx_usuario()  TO plataforma_app;
GRANT EXECUTE ON FUNCTION ctx_empresa()  TO plataforma_app;
GRANT EXECUTE ON FUNCTION ctx_ip()       TO plataforma_app;
GRANT EXECUTE ON FUNCTION ctx_agente()   TO plataforma_app;
GRANT EXECUTE ON FUNCTION ctx_es_super() TO plataforma_app;
