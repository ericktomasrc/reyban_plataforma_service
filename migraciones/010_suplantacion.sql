-- =============================================================================
-- 010 — SUPLANTACIÓN
--
-- «Ver la plataforma con los ojos de otro», para dar soporte sin pedirle a
-- nadie su contraseña.
--
-- LA IDEA: LA SESIÓN NO CAMBIA DE DUEÑO, SE LE AÑADE UN DISFRAZ.
--
-- No se crea una sesión nueva ni se entra otra vez. A la sesión que ya existe
-- se le apunta a quién está viendo, y se le quita el apunte al terminar. Una
-- sola cookie, un solo camino de vuelta.
--
-- La alternativa —abrir una segunda sesión con el usuario suplantado— habría
-- sido peor de tres formas: dos cookies que se pisan, ninguna forma de saber
-- que la segunda nació de la primera, y una sesión indistinguible de la que
-- abriría el propio usuario si alguien roba su contraseña.
--
-- LO QUE DECIDE ESTA MIGRACIÓN Y HAY QUE ENTENDER ANTES DE TOCARLA:
--
--   La autoría y el alcance se SEPARAN.
--
--   Mientras hay suplantación, el contexto de PostgreSQL se fija con la
--   empresa del suplantado —para que el aislamiento recorte igual que a él— y
--   con `super` en falso —para que no siga viéndolo todo—, pero el usuario de
--   la autoría sigue siendo EL DE VERDAD.
--
--   Consecuencia: si algo se escribiera durante una suplantación, en la
--   bitácora aparecería el nombre del super administrador, nunca el del
--   cliente. Nadie puede hacer que una fila diga que la escribió otro.
--
--   Y sobre eso, la aplicación no deja escribir nada: la suplantación es de
--   solo lectura. Las dos defensas juntas, no una.
-- =============================================================================

BEGIN;


-- --- Las dos columnas -------------------------------------------------------
--
-- Van en `sesiones` y no en una tabla aparte porque son parte del estado de
-- esta sesión: nacen y mueren con ella. Una tabla `suplantaciones` habría
-- obligado a una consulta más en CADA petición de la plataforma, y a que
-- alguien recordara borrar la fila cuando la sesión caduca sola.
--
-- El historial de quién suplantó a quién NO se pierde por esto: lo guardan los
-- eventos `suplantacion_iniciada` y `suplantacion_terminada`, y además la
-- bitácora, en cuanto se amplíe su disparador unas líneas más abajo.

ALTER TABLE sesiones
    ADD COLUMN suplantando_usuario_id uuid REFERENCES usuarios(id),
    ADD COLUMN suplantacion_inicio    timestamptz;

COMMENT ON COLUMN sesiones.suplantando_usuario_id IS
    'Con valor, esta sesion esta viendo la plataforma como ese usuario. El dueno de la sesion sigue siendo usuario_id.';

COMMENT ON COLUMN sesiones.suplantacion_inicio IS
    'Cuando empezo. Se usa para ensenarlo en la barra de aviso y para poder cortar suplantaciones olvidadas.';


-- LAS DOS COLUMNAS VAN JUNTAS O NINGUNA. Sin esta restricción, media
-- suplantación —con usuario y sin fecha, o al revés— sería un estado posible,
-- y el código tendría que defenderse de él en todos los sitios.
ALTER TABLE sesiones
    ADD CONSTRAINT ck_sesiones_suplantacion CHECK (
        (suplantando_usuario_id IS NULL AND suplantacion_inicio IS NULL) OR
        (suplantando_usuario_id IS NOT NULL AND suplantacion_inicio IS NOT NULL)
    );


-- NADIE SE SUPLANTA A SÍ MISMO. No haría daño, pero dejaría una sesión en un
-- estado raro —de solo lectura sin motivo— que alguien tardaría en entender.
ALTER TABLE sesiones
    ADD CONSTRAINT ck_sesiones_suplantacion_ajena CHECK (
        suplantando_usuario_id IS NULL OR suplantando_usuario_id <> usuario_id
    );


-- --- LA SESIÓN PROPIA SIEMPRE ES PROPIA ------------------------------------
--
-- ESTO SALIÓ DE UNA REVISIÓN Y ERA EL AGUJERO GRAVE DE ESTE ARCHIVO. Conviene
-- entender la cadena entera, porque no es evidente ni de lejos:
--
--   1. Un super administrador no pertenece a ninguna empresa: la restricción de
--      la 002 obliga a `empresa_id IS NULL`. Su fila de `sesiones` también.
--   2. La política de la 003 dice: se ve una sesión si eres super, si no hay
--      empresa en el contexto, o si la empresa de la fila es la del contexto.
--   3. Suplantando, las tres son FALSAS: `super` está apagado, el contexto SÍ
--      tiene empresa (la del cliente), y la fila tiene `empresa_id` nulo, así
--      que `NULL = '<uuid del cliente>'` no es cierto.
--
--   Resultado: suplantando, la sesión NO PUEDE VERSE A SÍ MISMA. Y como el Row
--   Level Security no da error —simplemente no hay filas—, todo lo que escribe
--   sobre ella se convierte en un UPDATE de cero filas, en silencio:
--
--     · `terminar_suplantacion` no termina nada. El botón de volver no hace
--       nada y no hay forma de salir hasta que la sesión caduque.
--     · «Salir» no revoca la sesión. El navegador pierde la cookie y parece que
--       salió, pero el token sigue valiendo doce horas, suplantando.
--     · `ultima_actividad` se congela justo en las sesiones que más interesa
--       vigilar.
--
-- LA CORRECCIÓN es una línea, y además es cierta por sí sola, al margen de la
-- suplantación: TU PROPIA SESIÓN ES TUYA. Suplantando, `ctx_usuario()` es el
-- super administrador —la autoría no cambia— y la fila es suya, así que encaja.
--
-- No amplía nada indebido: sin suplantación, quien ya se veía por empresa se
-- sigue viendo, y nadie gana acceso a una sesión ajena.
--
-- POR QUÉ NO SE ARREGLÓ CON `SECURITY DEFINER` en `terminar_suplantacion`:
-- porque `sesiones` tiene FORCE ROW LEVEL SECURITY, que aplica las políticas
-- también al propietario de la tabla. Una función marcada solo se las salta si
-- su dueño es superusuario de verdad — cierto al correr las migraciones con
-- `psql -U postgres`, y FALSO en un PostgreSQL gestionado, donde `rds_superuser`
-- no trae BYPASSRLS. Habría funcionado en desarrollo y dejado a alguien
-- atrapado en producción.

DROP POLICY IF EXISTS p_sesiones ON sesiones;

CREATE POLICY p_sesiones ON sesiones
    USING (ctx_es_super()
           OR ctx_empresa() IS NULL
           OR empresa_id = ctx_empresa()
           OR usuario_id = ctx_usuario())
    WITH CHECK (ctx_es_super()
           OR ctx_empresa() IS NULL
           OR empresa_id = ctx_empresa()
           OR usuario_id = ctx_usuario());


-- --- Y QUE LA BITÁCORA LAS VIGILE ------------------------------------------
--
-- ESTO SE ME HABRÍA OLVIDADO SI NO LO HUBIERA PROBADO, y conviene dejar escrito
-- por qué, porque el mismo tropiezo espera a la siguiente columna que se añada
-- a esta tabla.
--
-- `sesiones` no se audita en UPDATE como las demás tablas. Tiene un disparador
-- con LISTA DE COLUMNAS —`AFTER UPDATE OF revocada_en, ...`— y es una decisión
-- correcta de la migración 003: `ultima_actividad` se escribe casi en cada
-- petición, y auditarla llenaría la bitácora de filas que no dicen nada y
-- taparían las que sí.
--
-- El precio de esa decisión es este: una columna nueva NO entra en la lista
-- sola. Añadirla a la tabla y creer que queda auditada es el error fácil, y no
-- avisa de nada — simplemente no aparece ninguna fila.
--
-- Se recrea el disparador con las dos columnas dentro. Empezar y terminar una
-- suplantación son cosas raras, así que no hay riesgo de inundar nada.

DROP TRIGGER IF EXISTS auditar_sesiones_cambios ON sesiones;

CREATE TRIGGER auditar_sesiones_cambios
    AFTER UPDATE OF revocada_en, motivo_revocacion, dosfa_superado, expira_en,
                    suplantando_usuario_id, suplantacion_inicio
    ON sesiones
    FOR EACH ROW EXECUTE FUNCTION fn_auditar('token_hash');


-- Para la pantalla de «quién está suplantando ahora mismo», que todavía no
-- existe pero se va a querer. Parcial: las filas sin suplantación —que son
-- casi todas— no entran en el índice.
CREATE INDEX ix_sesiones_suplantando
    ON sesiones (suplantando_usuario_id)
 WHERE suplantando_usuario_id IS NOT NULL;


-- =============================================================================
-- resolver_sesion, AMPLIADA
--
-- Es la función que el middleware llama en cada petición para saber quién
-- pregunta. Ahora, cuando hay suplantación, DEVUELVE LA IDENTIDAD EFECTIVA:
-- el usuario y la empresa del suplantado, y `es_super_admin` en falso.
--
-- POR QUÉ AQUÍ Y NO EN C#:
--
-- Porque así no hay forma de olvidarlo. Si la función devolviera al super
-- administrador y el middleware tuviera que cambiarlo después, existiría un
-- camino —un `if` mal puesto, un endpoint que resuelve la sesión por su
-- cuenta— en el que la suplantación se queda a medias: viendo como el cliente
-- pero con los permisos de super administrador. Eso es lo peor que podría
-- pasar aquí, y esta decisión lo hace imposible.
--
-- LAS DOS BANDERAS QUE NO SON DEL SUPLANTADO, y no se tratan igual:
--
--   `dosfa_superado` se toma de la sesión de verdad. No es «lo que ve el
--   cliente»: es la prueba de que quien está al teclado es quien dice ser, y el
--   super administrador ya la dio al entrar.
--
--   `clave_cambio_forzado` se pone en FALSO, que no es lo mismo que heredarla,
--   y es a propósito. Si se heredara la del suplantado, quien suplanta quedaría
--   atrapado en una pantalla de cambio de contraseña que no puede resolver
--   —no sabe la del cliente—. Si se heredara la del suplantador, quedaría
--   atrapado igual: esa pantalla vive FUERA del marco de la aplicación, o sea
--   sin la barra de aviso y sin el botón de volver.
--
--   El precio de ponerla en falso es que un cambio obligatorio que aparezca en
--   mitad de una suplantación no se aplica hasta que termine. Se acota donde
--   corresponde: `iniciar_suplantacion` no deja EMPEZAR a quien ya debe uno.
-- =============================================================================

DROP FUNCTION IF EXISTS resolver_sesion(bytea);

CREATE FUNCTION resolver_sesion(p_token_hash bytea)
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
    empresa_operativa    boolean,

    -- Nulos cuando no hay suplantación, que es el caso normal.
    suplantador_id       uuid,
    suplantador_nombre   text,
    suplantado_nombre    text,
    suplantacion_inicio  timestamptz
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT s.id,

           -- La identidad EFECTIVA: a quién ve la aplicación.
           COALESCE(sup.id, dueno.id),
           COALESCE(sup.empresa_id, dueno.empresa_id),

           -- Suplantando, el super administrador deja de serlo. Es la línea
           -- que hace que vea lo que ve el cliente y no la plataforma entera.
           CASE WHEN sup.id IS NOT NULL THEN false ELSE dueno.es_super_admin END,

           -- Estas dos son del dueño de la sesión, no del suplantado: son la
           -- prueba de identidad de quien está al teclado.
           s.dosfa_superado,
           CASE WHEN sup.id IS NOT NULL THEN false ELSE dueno.clave_cambio_forzado END,

           s.expira_en,
           s.revocada_en,

           -- LOS DOS TIENEN QUE ESTAR ACTIVOS. Si al cliente lo desactivan en
           -- mitad de una suplantación, la sesión deja de valer: seguir
           -- mirando por los ojos de una cuenta cerrada no tiene sentido.
           dueno.activo AND COALESCE(sup.activo, true),

           -- La empresa que cuenta es la de la identidad efectiva.
           COALESCE(
               CASE WHEN sup.id IS NOT NULL
                    THEN (e_sup.activo AND e_sup.suspendida_en IS NULL)
                    ELSE (e_due.activo AND e_due.suspendida_en IS NULL)
               END,
               true),

           -- Y quién está de verdad detrás, para la barra de aviso.
           CASE WHEN sup.id IS NOT NULL THEN dueno.id END,
           CASE WHEN sup.id IS NOT NULL THEN dueno.nombre END,
           sup.nombre,
           s.suplantacion_inicio

      FROM sesiones s
      JOIN usuarios dueno   ON dueno.id = s.usuario_id
      LEFT JOIN usuarios sup ON sup.id  = s.suplantando_usuario_id
      LEFT JOIN empresas e_due ON e_due.id = dueno.empresa_id
      LEFT JOIN empresas e_sup ON e_sup.id = sup.empresa_id
     WHERE s.token_hash = p_token_hash;
$$;

COMMENT ON FUNCTION resolver_sesion IS
    'SECURITY DEFINER a proposito: para saber de quien es la sesion hay que leerla antes de poder fijar el contexto. Con suplantacion devuelve la identidad EFECTIVA (el suplantado, sin super) y aparte quien esta detras.';

GRANT EXECUTE ON FUNCTION resolver_sesion(bytea) TO plataforma_app;


-- =============================================================================
-- iniciar_suplantacion — LA CUARTA PUERTA
--
-- El mismo problema de siempre, otra vez: para suplantar a alguien hay que
-- leer su fila, y en el instante de decidirlo el contexto todavía es el del
-- super administrador. Él sí puede verla —`ctx_es_super()` se lo permite—,
-- así que el aislamiento no estorba aquí.
--
-- ENTONCES, ¿POR QUÉ UNA FUNCIÓN MARCADA Y NO UN UPDATE NORMAL?
--
-- Por las comprobaciones. Son cuatro, tienen que pasar TODAS, y tienen que
-- pasar en el mismo momento en que se escribe la columna. Hacerlas en C#
-- dejaría un hueco entre el `SELECT` que comprueba y el `UPDATE` que escribe
-- —pequeño, pero real— en el que la cuenta se desactiva y la suplantación se
-- inicia igual.
--
-- `FOR UPDATE` sobre la sesión cierra ese hueco del todo.
--
-- DEVUELVE TEXTO, NO UN BOOLEANO: con cuatro motivos distintos de rechazo, un
-- booleano obligaría a repetir las cuatro consultas en C# solo para redactar
-- el mensaje.
-- =============================================================================

CREATE OR REPLACE FUNCTION iniciar_suplantacion(
    p_sesion_id  uuid,
    p_actor      uuid,
    p_objetivo   uuid
) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_dueno       uuid;
    v_dueno_super boolean;
    v_dueno_debe  boolean;
    v_activo      boolean;
    v_super       boolean;
    v_empresa     uuid;
    v_operativa   boolean;
BEGIN
    -- La sesión, bloqueada: nadie la cambia mientras decidimos.
    SELECT s.usuario_id, u.es_super_admin, u.clave_cambio_forzado
      INTO v_dueno, v_dueno_super, v_dueno_debe
      FROM sesiones s
      JOIN usuarios u ON u.id = s.usuario_id
     WHERE s.id = p_sesion_id AND s.revocada_en IS NULL AND s.expira_en > now()
       FOR UPDATE OF s;

    IF v_dueno IS NULL THEN
        RETURN 'sesion_invalida';
    END IF;

    -- LA SESIÓN TIENE QUE SER DE QUIEN LLAMA.
    --
    -- Sin esto, un super administrador podría pasar el identificador de la
    -- sesión de OTRO y dejarle plantada una suplantación: la víctima se
    -- encuentra viendo la cuenta de un cliente sin haber pedido nada.
    -- Hoy la aplicación siempre manda la sesión propia, pero esta función está
    -- concedida a `plataforma_app` y no puede fiarse de eso.
    IF v_dueno <> p_actor THEN
        RETURN 'sesion_ajena';
    END IF;

    -- Y QUIEN SUPLANTA TIENE QUE SER SUPER ADMINISTRADOR.
    --
    -- En C# lo guarda `RequierePermiso(plataforma.suplantar)`, y hoy eso pasa
    -- por la rama de super administrador porque ningún rol asignable lleva
    -- permisos del área `plataforma`. O sea: está cerrado por casualidad, no
    -- por diseño. El día que alguien conceda ese permiso a un rol de empresa,
    -- sin esta línea un usuario normal de la empresa A podría suplantar a uno
    -- de la empresa B — `resolver_sesion` le cambiaría la empresa del contexto
    -- y le abriría la empresa B entera en lectura. Fuga entre clientes con un
    -- solo INSERT, y la base sin decir nada.
    IF NOT v_dueno_super THEN
        RETURN 'no_eres_super';
    END IF;

    -- NI DEBER UN CAMBIO DE CONTRASEÑA.
    --
    -- Suplantando, esa obligación queda en pausa (mira el comentario de
    -- `resolver_sesion`), así que dejar empezar aquí sería dar la forma de
    -- posponerla indefinidamente.
    IF v_dueno_debe THEN
        RETURN 'debes_cambiar_clave';
    END IF;

    IF v_dueno = p_objetivo THEN
        RETURN 'eres_tu';
    END IF;

    SELECT u.activo, u.es_super_admin, u.empresa_id,
           COALESCE(e.activo AND e.suspendida_en IS NULL, true)
      INTO v_activo, v_super, v_empresa, v_operativa
      FROM usuarios u
      LEFT JOIN empresas e ON e.id = u.empresa_id
     WHERE u.id = p_objetivo;

    IF v_activo IS NULL THEN
        RETURN 'no_existe';
    END IF;

    IF NOT v_activo THEN
        RETURN 'desactivado';
    END IF;

    -- NO SE SUPLANTA A OTRO SUPER ADMINISTRADOR.
    --
    -- No aportaría nada —ve lo mismo que quien suplanta— y sí quitaría algo:
    -- sería la forma de saltarse el rastro. Dos personas con la misma potestad
    -- y una capaz de actuar como la otra hace que la bitácora deje de poder
    -- distinguirlas, que es justo lo que la bitácora existe para hacer.
    IF v_super THEN
        RETURN 'es_super';
    END IF;

    IF v_empresa IS NULL THEN
        RETURN 'sin_empresa';
    END IF;

    IF NOT v_operativa THEN
        RETURN 'empresa_suspendida';
    END IF;

    UPDATE sesiones
       SET suplantando_usuario_id = p_objetivo,
           suplantacion_inicio    = now()
     WHERE id = p_sesion_id;

    RETURN 'ok';
END;
$$;

COMMENT ON FUNCTION iniciar_suplantacion IS
    'Marca una sesion como suplantando a otro usuario. Comprueba al sujeto (sesion propia, super admin, sin cambio de clave pendiente) y al objetivo (existe, activo, no super, con empresa operativa), y escribe en la misma transaccion con la sesion bloqueada.';

GRANT EXECUTE ON FUNCTION iniciar_suplantacion(uuid, uuid, uuid) TO plataforma_app;


-- =============================================================================
-- terminar_suplantacion
--
-- SIN COMPROBACIONES Y SIN PERMISO, a propósito.
--
-- Esto no abre ninguna puerta: la cierra. Lo peor que consigue quien lo llame
-- sin motivo es devolver a alguien a su propia cuenta. Ponerle condiciones
-- sería crear la posibilidad de quedarse atrapado dentro de una suplantación,
-- que es el único fallo verdaderamente grave que este archivo puede tener.
--
-- Marcada igualmente porque, suplantando, el contexto ya no es de super
-- administrador: la sesión pertenece a un usuario que el aislamiento de la
-- empresa suplantada no deja ver.
-- =============================================================================

CREATE OR REPLACE FUNCTION terminar_suplantacion(p_sesion_id uuid)
RETURNS boolean
LANGUAGE sql SECURITY DEFINER SET search_path = public AS $$
    WITH cortada AS (
        UPDATE sesiones
           SET suplantando_usuario_id = NULL,
               suplantacion_inicio    = NULL
         WHERE id = p_sesion_id
           AND suplantando_usuario_id IS NOT NULL
        RETURNING 1
    )
    SELECT EXISTS (SELECT 1 FROM cortada);
$$;

COMMENT ON FUNCTION terminar_suplantacion IS
    'Quita el disfraz. Sin comprobaciones: quedarse atrapado en una suplantacion seria peor que cualquier uso indebido de esto.';

GRANT EXECUTE ON FUNCTION terminar_suplantacion(uuid) TO plataforma_app;


-- =============================================================================
-- fn_auditar: LA EMPRESA NULA ES UNA RESPUESTA, NO UNA FALTA DE RESPUESTA
--
-- Esto también salió de la revisión, es lo más delicado del archivo porque toca
-- una función que vigila TODAS las tablas, y es un fallo que ya existía antes de
-- la suplantación — ella solo lo hizo evidente.
--
-- LO QUE HACÍA LA 001:
--
--     v_empresa := COALESCE(despues->>'empresa_id', antes->>'empresa_id',
--                           ctx_empresa());
--
-- Ese COALESCE mete dos casos muy distintos en el mismo saco:
--
--   (a) La tabla NO TIENE columna `empresa_id` —los catálogos: `permisos`,
--       `modulos`—. Ahí sí hay que recurrir al contexto: es lo único que se
--       sabe. Para esto se escribió.
--
--   (b) La tabla SÍ la tiene y vale NULL. Eso NO es desconocimiento: es la
--       respuesta. Un super administrador no pertenece a ninguna empresa, un rol
--       de sistema tampoco, y su sesión tampoco. NULL significa «de la
--       plataforma», y el COALESCE lo pisaba con la empresa del contexto.
--
-- LO QUE PROVOCABA, con un ejemplo real que se reprodujo contra una base:
--
--   El super administrador suplanta a Ana, de la empresa A, y pulsa «Salir». El
--   contexto tiene la empresa A. Se escribe en SU fila de `sesiones`, que no
--   tiene empresa, así que el COALESCE le estampa la empresa A — y la fila
--   entra en LA BITÁCORA DEL CLIENTE, con el `antes` y el `despues` completos:
--   el identificador del super administrador, su IP y su navegador. El
--   administrador de la empresa A lo abre desde su pantalla de auditoría, que
--   ya tiene, y se lo lleva.
--
--   Y no era exclusivo de la suplantación: editar un rol de SISTEMA mientras el
--   contexto tenía una empresa archivaba ese cambio dentro de esa empresa.
--
-- LA CORRECCIÓN: preguntar si la CLAVE EXISTE, no si el valor es nulo. En jsonb
-- eso es el operador `?`. Si existe, manda su valor aunque sea NULL; si no
-- existe, entonces —y solo entonces— se recurre al contexto.
--
-- Todo lo demás de la función queda igual, empezando por el orden de calcular
-- los cambios antes de tapar los valores sensibles, que es el error caro que ya
-- advertía la 001.
-- =============================================================================

CREATE OR REPLACE FUNCTION fn_auditar() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE
    -- ESTAS DECLARACIONES SON LAS DE LA 001, LETRA POR LETRA.
    --
    -- El COALESCE de `v_sensibles` hace falta: sin él, un disparador sin
    -- argumentos deja el array en NULL y el FOREACH de más abajo revienta.
    --
    -- Y `v_ruido` NO LLEVA `ultima_actividad`, aunque tentaría añadirla: por eso
    -- `sesiones` se vigila con lista de columnas en vez de a lo bruto. Cambiar
    -- esta lista aquí habría alterado en silencio qué se audita en TODAS las
    -- tablas, que es lo último que debe hacer una migración que venía a arreglar
    -- otra cosa.
    v_antes     jsonb;
    v_despues   jsonb;
    v_cambios   text[];
    v_sensibles text[] := COALESCE(TG_ARGV, ARRAY[]::text[]);
    v_col       text;
    v_fila_id   text;
    v_empresa   uuid;

    v_ruido text[] := ARRAY['modificado_en','modificado_por','version'];
BEGIN
    IF TG_OP IN ('UPDATE','DELETE') THEN v_antes   := to_jsonb(OLD); END IF;
    IF TG_OP IN ('INSERT','UPDATE') THEN v_despues := to_jsonb(NEW); END IF;

    -- Primero QUÉ cambió, sobre los valores de verdad; después se tapan. Al
    -- revés, un cambio de contraseña compararía '[oculto]' contra '[oculto]' y
    -- no se registraría nada.
    IF TG_OP = 'UPDATE' THEN
        SELECT array_agg(d.clave ORDER BY d.clave)
          INTO v_cambios
          FROM jsonb_each(v_despues) AS d(clave, valor)
         WHERE v_antes -> d.clave IS DISTINCT FROM d.valor
           AND NOT (d.clave = ANY (v_ruido));

        IF v_cambios IS NULL THEN
            RETURN NULL;
        END IF;
    END IF;

    FOREACH v_col IN ARRAY v_sensibles LOOP
        IF v_antes   ? v_col THEN v_antes   := jsonb_set(v_antes,   ARRAY[v_col], '"[oculto]"'); END IF;
        IF v_despues ? v_col THEN v_despues := jsonb_set(v_despues, ARRAY[v_col], '"[oculto]"'); END IF;
    END LOOP;

    v_fila_id := COALESCE(
        v_despues ->> 'id',     v_antes ->> 'id',
        v_despues ->> 'codigo', v_antes ->> 'codigo'
    );

    -- AQUÍ ESTÁ EL CAMBIO. `?` pregunta si la clave existe.
    IF v_despues ? 'empresa_id' THEN
        v_empresa := NULLIF(v_despues ->> 'empresa_id', '')::uuid;
    ELSIF v_antes ? 'empresa_id' THEN
        v_empresa := NULLIF(v_antes ->> 'empresa_id', '')::uuid;
    ELSE
        v_empresa := ctx_empresa();
    END IF;

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

    RETURN NULL;
END $$;

COMMENT ON FUNCTION fn_auditar IS
    'Escribe una fila de bitacora por cada cambio. La empresa sale de la columna de la propia fila cuando esa columna EXISTE, aunque valga NULL (NULL significa de la plataforma, no desconocido); solo se recurre al contexto cuando la tabla no tiene esa columna.';


INSERT INTO migraciones_aplicadas (nombre) VALUES ('010_suplantacion');

COMMIT;
