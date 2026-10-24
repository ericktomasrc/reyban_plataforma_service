-- =============================================================================
-- 046 — PERSONAL, ALCANCE Y SUCURSAL ACTIVA (23 oct 2026, maqueta aprobada
-- «Personal, equipo y sucursales»)
--
-- Tres cosas, que van juntas porque se apoyan unas en otras:
--
--   1. LA FICHA DE LA PERSONA, en `usuarios`: tipo y número de documento,
--      nombres y apellidos, celular, fecha de nacimiento, dirección y ubigeo,
--      cargo y fecha de ingreso. Las filas de antes quedan con la ficha vacía
--      («ficha incompleta»): no se bloquea a nadie. El documento no se repite
--      dentro de una empresa (su personal) ni dentro de una cuenta (su equipo).
--      Ley 29733: no se guarda foto del DNI.
--
--   2. EL ALCANCE: qué comprobantes ve cada persona en cada empresa.
--        · empresa   todo (lo de siempre; sin fila = esto)
--        · sucursal  lo de sus sucursales (usuario_sucursal)
--        · propio    solo lo que emitió él
--      Y la SUCURSAL ACTIVA (el selector de arriba): con una elegida, todo se
--      ve solo de esa sucursal. Lo aplica la BASE, en la regla de
--      `documentos`: lista, detalle, descargas, notas, WhatsApp, reportes,
--      exportaciones e Inicio quedan recortados sin tocar cada consulta.
--      fijar_alcance() lo calcula una vez por petición (el middleware).
--
--   3. ROL POR EMPRESA para el equipo de una Cuenta Profesional
--      (usuario_rol_empresa): un colaborador puede ser Facturador en una
--      empresa y Consulta en otra. Sin filas, lo de antes (por nivel).
--
-- Roles nuevos de la plataforma: «Jefe de sucursal», y dos que se SUMAN a
-- otro rol (las casillas de la ficha): «Notas de crédito y débito» y
-- «Agrega personal».
--
-- NADA SE BORRA. Se puede correr dos veces.
-- =============================================================================

BEGIN;

-- =============================================================================
-- 1. LA FICHA
-- =============================================================================

ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS tipo_documento   text;
ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS numero_documento text;
ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS nombres          text;
ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS apellido_paterno text;
ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS apellido_materno text;
ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS celular          text;
ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS fecha_nacimiento date;
ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS direccion        text;
ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS ubigeo           text;
ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS cargo            text;
ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS fecha_ingreso    date;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_usuarios_documento') THEN
        -- 1 DNI (8 dígitos) · 4 carné de extranjería · 7 pasaporte. Los dos
        -- campos van juntos: o los dos o ninguno.
        ALTER TABLE usuarios ADD CONSTRAINT ck_usuarios_documento CHECK (
            (tipo_documento IS NULL AND numero_documento IS NULL)
            OR (tipo_documento = '1' AND numero_documento ~ '^[0-9]{8}$')
            OR (tipo_documento = '4' AND numero_documento ~ '^[A-Z0-9]{8,12}$')
            OR (tipo_documento = '7' AND numero_documento ~ '^[A-Z0-9]{6,12}$'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_usuarios_ficha') THEN
        ALTER TABLE usuarios ADD CONSTRAINT ck_usuarios_ficha CHECK (
                (nombres          IS NULL OR length(btrim(nombres))          BETWEEN 1 AND 80)
            AND (apellido_paterno IS NULL OR length(btrim(apellido_paterno)) BETWEEN 1 AND 60)
            AND (apellido_materno IS NULL OR length(btrim(apellido_materno)) BETWEEN 1 AND 60)
            AND (celular          IS NULL OR celular ~ '^\+?[0-9]{6,15}$')
            AND (direccion        IS NULL OR length(direccion) <= 200)
            AND (ubigeo           IS NULL OR ubigeo ~ '^[0-9]{6}$')
            AND (cargo            IS NULL OR length(btrim(cargo)) BETWEEN 1 AND 80)
            AND (fecha_nacimiento IS NULL OR fecha_nacimiento BETWEEN DATE '1900-01-01' AND DATE '2100-01-01')
            AND (fecha_ingreso    IS NULL OR fecha_ingreso    BETWEEN DATE '1950-01-01' AND DATE '2100-01-01'));
    END IF;
END $$;

-- El documento no se repite: en el personal de una empresa, ni en el equipo
-- de una cuenta. Activos o no: a quien vuelve se le reactiva, no se le duplica.
CREATE UNIQUE INDEX IF NOT EXISTS ux_usuarios_documento_empresa
    ON usuarios (empresa_id, tipo_documento, numero_documento)
 WHERE numero_documento IS NOT NULL AND nivel_cuenta IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_usuarios_documento_cuenta
    ON usuarios (cuenta_id, tipo_documento, numero_documento)
 WHERE numero_documento IS NOT NULL AND nivel_cuenta IS NOT NULL;

COMMENT ON COLUMN usuarios.tipo_documento IS
    '046: 1 DNI · 4 carné de extranjería · 7 pasaporte (catálogo 06 de SUNAT). Ley 29733: sin foto del documento.';


-- =============================================================================
-- 2. EL ALCANCE (por persona y por empresa)
-- =============================================================================

CREATE TABLE IF NOT EXISTS usuario_alcance (
    id                   uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    usuario_id           uuid        NOT NULL REFERENCES usuarios (id),
    empresa_id           uuid        NOT NULL REFERENCES empresas (id),
    alcance              text        NOT NULL DEFAULT 'empresa',
    sucursal_defecto_id  uuid        REFERENCES sucursales (id),

    creado_en            timestamptz NOT NULL DEFAULT now(),
    creado_por           uuid,
    modificado_en        timestamptz,
    modificado_por       uuid,
    activo               boolean     NOT NULL DEFAULT true,
    version              integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_usuario_alcance CHECK (alcance IN ('empresa', 'sucursal', 'propio'))
);

-- Una fila por persona y empresa (se cambia, no se agrega otra).
CREATE UNIQUE INDEX IF NOT EXISTS ux_usuario_alcance ON usuario_alcance (usuario_id, empresa_id);

COMMENT ON TABLE usuario_alcance IS
    '046: qué ve cada persona en cada empresa. Sin fila = toda la empresa (lo de antes).';


-- Rol por empresa del equipo de una Cuenta Profesional. Se escribe solo por
-- las funciones de «Mi equipo».
CREATE TABLE IF NOT EXISTS usuario_rol_empresa (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    usuario_id      uuid        NOT NULL REFERENCES usuarios (id),
    empresa_id      uuid        NOT NULL REFERENCES empresas (id),
    rol_id          uuid        NOT NULL REFERENCES roles (id),

    creado_en       timestamptz NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean     NOT NULL DEFAULT true,
    version         integer     NOT NULL DEFAULT 1
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_usuario_rol_empresa ON usuario_rol_empresa (usuario_id, empresa_id, rol_id);

COMMENT ON TABLE usuario_rol_empresa IS
    '046: el rol de un colaborador en UNA empresa de la cartera. Sin filas activas = el de su nivel.';


ALTER TABLE usuario_alcance     ENABLE ROW LEVEL SECURITY;
ALTER TABLE usuario_alcance     FORCE  ROW LEVEL SECURITY;
ALTER TABLE usuario_rol_empresa ENABLE ROW LEVEL SECURITY;
ALTER TABLE usuario_rol_empresa FORCE  ROW LEVEL SECURITY;

DROP POLICY IF EXISTS p_usuario_alcance ON usuario_alcance;
CREATE POLICY p_usuario_alcance ON usuario_alcance
    USING (ctx_es_super() OR empresa_id = ctx_empresa() OR usuario_id = ctx_usuario())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());

-- La aplicación solo la lee (la suya, o la de su empresa); escriben las
-- funciones de «Mi equipo».
DROP POLICY IF EXISTS p_usuario_rol_empresa ON usuario_rol_empresa;
CREATE POLICY p_usuario_rol_empresa ON usuario_rol_empresa
    USING (ctx_es_super() OR usuario_id = ctx_usuario() OR usuario_id = ctx_suplantado()
           OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super());

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'usuario_alcance'::regclass AND NOT tgisinternal) THEN
        PERFORM vigilar_tabla('usuario_alcance');
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'usuario_rol_empresa'::regclass AND NOT tgisinternal) THEN
        PERFORM vigilar_tabla('usuario_rol_empresa');
    END IF;
END $$;

GRANT SELECT, INSERT, UPDATE ON usuario_alcance TO plataforma_app;
-- Escribir, solo el super administrador (la regla de arriba); el resto, por Mi equipo.
GRANT SELECT, INSERT, UPDATE ON usuario_rol_empresa TO plataforma_app;


-- =============================================================================
-- 3. LOS PERMISOS: con el rol por empresa, si lo hay
--
-- Mismas columnas que antes (la usan menu_usuario y las puertas). Para un
-- colaborador con filas en usuario_rol_empresa para la empresa que tiene
-- abierta, cuentan esas; si no, las de siempre.
-- =============================================================================

CREATE OR REPLACE VIEW permisos_efectivos WITH (security_invoker = true) AS
SELECT DISTINCT
       u.id         AS usuario_id,
       u.empresa_id,
       rp.permiso_codigo
  FROM usuarios u
  JOIN LATERAL (
        SELECT ure.rol_id
          FROM usuario_rol_empresa ure
         WHERE ure.usuario_id = u.id AND ure.empresa_id = u.empresa_id AND ure.activo
        UNION ALL
        SELECT ur.rol_id
          FROM usuario_rol ur
         WHERE ur.usuario_id = u.id AND ur.activo
           AND NOT EXISTS (SELECT 1 FROM usuario_rol_empresa x
                            WHERE x.usuario_id = u.id AND x.empresa_id = u.empresa_id AND x.activo)
       ) asignado ON true
  JOIN roles       r  ON r.id  = asignado.rol_id AND r.activo
  LEFT JOIN roles  aj ON aj.basado_en = r.id AND aj.activo AND aj.empresa_id = u.empresa_id
  JOIN rol_permiso rp ON rp.rol_id = COALESCE(aj.id, r.id) AND rp.activo
 WHERE u.activo;


-- =============================================================================
-- 4. ROLES NUEVOS DE LA PLATAFORMA
-- =============================================================================

INSERT INTO roles (id, empresa_id, nombre, descripcion, editable)
SELECT '55555555-5555-5555-5555-555555555555', NULL, 'Jefe de sucursal',
       'Emite, anula y hace notas en su sucursal; ve sus reportes y su personal.', true
 WHERE NOT EXISTS (SELECT 1 FROM roles WHERE id = '55555555-5555-5555-5555-555555555555');

INSERT INTO roles (id, empresa_id, nombre, descripcion, editable)
SELECT '66666666-6666-6666-6666-666666666666', NULL, 'Notas de crédito y débito',
       'Se suma a otro rol: permite hacer notas de crédito y débito.', false
 WHERE NOT EXISTS (SELECT 1 FROM roles WHERE id = '66666666-6666-6666-6666-666666666666');

INSERT INTO roles (id, empresa_id, nombre, descripcion, editable)
SELECT '77777777-7777-7777-7777-777777777777', NULL, 'Agrega personal',
       'Se suma a otro rol: agrega y edita al personal de sus sucursales.', false
 WHERE NOT EXISTS (SELECT 1 FROM roles WHERE id = '77777777-7777-7777-7777-777777777777');

INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT x.rol::uuid, x.permiso
  FROM (
        SELECT '55555555-5555-5555-5555-555555555555' AS rol, p.codigo AS permiso
          FROM permisos p
         WHERE p.codigo IN ('facturacion.ver', 'facturacion.emitir', 'facturacion.anular',
                            'facturacion.notas_credito', 'facturacion.descargar',
                            'facturacion.clientes_gestionar', 'facturacion.productos_gestionar',
                            'usuarios.ver')
            OR p.area = 'reportes'
        UNION ALL
        SELECT '66666666-6666-6666-6666-666666666666', 'facturacion.notas_credito'
        UNION ALL
        SELECT '77777777-7777-7777-7777-777777777777', c
          FROM unnest(ARRAY['usuarios.ver', 'usuarios.crear', 'usuarios.editar', 'usuarios.desactivar']) c
       ) x
 WHERE NOT EXISTS (SELECT 1 FROM rol_permiso rp
                    WHERE rp.rol_id = x.rol::uuid AND rp.permiso_codigo = x.permiso);


-- =============================================================================
-- 5. EL CONTEXTO DEL ALCANCE (por petición)
--
-- Variables locales a la transacción, como las de fijar_contexto:
--   app.alc_tipo        '' (sin recorte) · empresa · sucursal · propio
--   app.alc_usuario     la persona (la efectiva: con «ver como», el cliente)
--   app.alc_sucursales  sus sucursales, separadas por coma
--   app.alc_principal   la 0000 de la empresa (los comprobantes de antes)
--   app.alc_vista       la sucursal elegida arriba ('' = todas)
--
-- SIN FIJAR = SIN RECORTE: los webhooks, las tareas de madrugada y las
-- migraciones siguen viendo todo lo de la empresa, como siempre.
-- =============================================================================

-- fijar_contexto borra además el alcance: así nunca queda el de otra persona.
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
           set_config('app.super',      CASE WHEN p_super THEN '1' ELSE '' END, true),
           set_config('app.alc_tipo',       '', true),
           set_config('app.alc_usuario',    '', true),
           set_config('app.alc_sucursales', '', true),
           set_config('app.alc_principal',  '', true),
           set_config('app.alc_vista',      '', true);
    SELECT NULL::void;
$$;


-- ¿Ve este comprobante? (su sucursal y quién lo creó). La regla de documentos.
CREATE OR REPLACE FUNCTION ctx_ve_documento(p_sucursal uuid, p_creado_por uuid)
RETURNS boolean
LANGUAGE sql STABLE AS $$
    SELECT COALESCE(current_setting('app.alc_tipo', true), '') = ''
        OR (
            -- La sucursal elegida arriba.
            (COALESCE(current_setting('app.alc_vista', true), '') = ''
             OR COALESCE(p_sucursal, NULLIF(current_setting('app.alc_principal', true), '')::uuid)
                = NULLIF(current_setting('app.alc_vista', true), '')::uuid)
            AND CASE current_setting('app.alc_tipo', true)
                    WHEN 'empresa'  THEN true
                    WHEN 'sucursal' THEN
                        COALESCE(p_sucursal, NULLIF(current_setting('app.alc_principal', true), '')::uuid)::text
                        = ANY (string_to_array(current_setting('app.alc_sucursales', true), ','))
                    WHEN 'propio'   THEN
                        p_creado_por = NULLIF(current_setting('app.alc_usuario', true), '')::uuid
                    ELSE false
                END
        );
$$;

GRANT EXECUTE ON FUNCTION ctx_ve_documento(uuid, uuid) TO plataforma_app;


-- Lo calcula y lo fija. Devuelve (json) lo que necesita la pantalla: el
-- alcance, las sucursales que puede elegir, la elegida y la de por defecto.
-- p_vista: la que pide el selector (NULL o una que no le toca = la de siempre).
CREATE OR REPLACE FUNCTION fijar_alcance(p_usuario uuid, p_empresa uuid, p_vista uuid)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_nivel      text;
    v_alcance    text;
    v_defecto    uuid;
    v_principal  uuid;
    v_suyas      uuid[];
    v_elegibles  uuid[];
    v_vista      uuid;
    v_total      int;
BEGIN
    IF p_usuario IS NULL OR p_empresa IS NULL THEN
        RETURN '{}';
    END IF;

    SELECT u.nivel_cuenta INTO v_nivel FROM usuarios u WHERE u.id = p_usuario;

    SELECT a.alcance, a.sucursal_defecto_id INTO v_alcance, v_defecto
      FROM usuario_alcance a
     WHERE a.usuario_id = p_usuario AND a.empresa_id = p_empresa AND a.activo;

    -- El dueño y los colaboradores administradores de la cuenta, y quien tiene
    -- el rol Administrador en la empresa, ven siempre toda la empresa.
    IF v_alcance IS NULL
       OR v_nivel IN ('dueno', 'colaborador_admin')
       OR EXISTS (SELECT 1 FROM usuario_rol ur
                   WHERE ur.usuario_id = p_usuario AND ur.activo
                     AND ur.rol_id = '11111111-1111-1111-1111-111111111111'
                     AND (v_nivel IS NULL
                          OR NOT EXISTS (SELECT 1 FROM usuario_rol_empresa x
                                          WHERE x.usuario_id = p_usuario AND x.empresa_id = p_empresa AND x.activo)))
    THEN
        v_alcance := 'empresa';
    END IF;

    SELECT s.id INTO v_principal FROM sucursales s
     WHERE s.empresa_id = p_empresa AND s.codigo = '0000' AND s.eliminada_en IS NULL;
    -- Sin 0000 todavía (empresa nueva): una marca que no coincide con nada.
    v_principal := COALESCE(v_principal, '00000000-0000-0000-0000-000000000000'::uuid);

    -- Sus sucursales (también desactivadas: sus comprobantes viejos se ven).
    SELECT array_agg(us.sucursal_id ORDER BY s.codigo) INTO v_suyas
      FROM usuario_sucursal us
      JOIN sucursales s ON s.id = us.sucursal_id AND s.eliminada_en IS NULL
     WHERE us.usuario_id = p_usuario AND us.empresa_id = p_empresa AND us.activo;

    IF v_alcance <> 'empresa' AND COALESCE(cardinality(v_suyas), 0) = 0 THEN
        v_suyas := ARRAY[v_principal];
    END IF;

    SELECT count(*) INTO v_total FROM sucursales s
     WHERE s.empresa_id = p_empresa AND s.eliminada_en IS NULL AND s.activo;

    -- Las que puede elegir arriba (activas).
    IF v_alcance = 'empresa' THEN
        SELECT array_agg(s.id ORDER BY s.codigo) INTO v_elegibles
          FROM sucursales s WHERE s.empresa_id = p_empresa AND s.eliminada_en IS NULL AND s.activo;
    ELSE
        SELECT array_agg(s.id ORDER BY s.codigo) INTO v_elegibles
          FROM sucursales s WHERE s.id = ANY (v_suyas) AND s.activo;
        -- Si todas las suyas están desactivadas, igual elige entre ellas (para ver lo de antes).
        IF COALESCE(cardinality(v_elegibles), 0) = 0 THEN
            v_elegibles := v_suyas;
        END IF;
    END IF;

    -- La de por defecto: la anotada, si sigue siendo suya; si no, la primera.
    IF v_defecto IS NULL OR NOT (v_defecto = ANY (COALESCE(v_elegibles, '{}'))) THEN
        SELECT x.id INTO v_defecto
          FROM unnest(COALESCE(v_suyas, '{}')) WITH ORDINALITY x(id, n)
         WHERE x.id = ANY (COALESCE(v_elegibles, '{}'))
         ORDER BY x.n LIMIT 1;
        IF v_defecto IS NULL THEN
            v_defecto := CASE WHEN v_principal = ANY (COALESCE(v_elegibles, '{}')) THEN v_principal
                              ELSE (COALESCE(v_elegibles, '{}'))[1] END;
        END IF;
    END IF;

    -- La elegida arriba.
    IF p_vista IS NOT NULL AND p_vista = ANY (COALESCE(v_elegibles, '{}')) THEN
        v_vista := p_vista;
    ELSIF v_alcance = 'empresa' THEN
        v_vista := NULL;                                       -- «Todas»
    ELSE
        v_vista := v_defecto;                                  -- sin «Todas»
    END IF;

    PERFORM set_config('app.alc_tipo',       v_alcance, true),
            set_config('app.alc_usuario',    p_usuario::text, true),
            set_config('app.alc_sucursales', COALESCE(array_to_string(v_suyas, ','), ''), true),
            set_config('app.alc_principal',  v_principal::text, true),
            set_config('app.alc_vista',      COALESCE(v_vista::text, ''), true);

    RETURN json_build_object(
        'alcance',   v_alcance,
        'vista',     v_vista,
        'defecto',   v_defecto,
        'principal', v_principal,
        'total',     v_total,
        'sucursales', COALESCE((
            SELECT json_agg(json_build_object('id', s.id, 'codigo', s.codigo, 'nombre', s.nombre, 'activo', s.activo)
                            ORDER BY s.codigo)
              FROM sucursales s WHERE s.id = ANY (COALESCE(v_elegibles, '{}'))), '[]'::json)
    )::text;
END $$;

ALTER FUNCTION fijar_alcance(uuid, uuid, uuid) OWNER TO plataforma_puertas;
REVOKE ALL ON FUNCTION fijar_alcance(uuid, uuid, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION fijar_alcance(uuid, uuid, uuid) TO plataforma_app;


-- --- La regla de documentos, con el alcance -----------------------------------
--
-- WITH CHECK queda igual (solo la empresa): escribir lo decide la API. Lo que
-- cambia es qué filas EXISTEN para la persona.
DROP POLICY IF EXISTS p_documentos ON documentos;
CREATE POLICY p_documentos ON documentos
    USING (ctx_es_super() OR (empresa_id = ctx_empresa() AND ctx_ve_documento(sucursal_id, creado_por)))
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());


-- =============================================================================
-- 6. «ADEMÁS ENTRAN DESDE TU ESTUDIO»
--
-- El personal de una empresa no ve a la gente de la cuenta (la regla de
-- usuarios no lo deja). Esto da solo lo que hace falta para el bloque de
-- solo lectura: nombre, nivel, rol y alcance en ESTA empresa.
-- =============================================================================

CREATE OR REPLACE FUNCTION estudio_en_empresa()
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE(json_agg(t ORDER BY t.orden, t.nombre), '[]'::json)::text
      FROM (
        SELECT u.nombre,
               u.nivel_cuenta AS nivel,
               CASE u.nivel_cuenta WHEN 'dueno' THEN 0 WHEN 'colaborador_admin' THEN 1 ELSE 2 END AS orden,
               CASE WHEN u.nivel_cuenta IN ('dueno', 'colaborador_admin') THEN 'Administrador'
                    ELSE COALESCE((SELECT string_agg(r.nombre, ' · ' ORDER BY r.nombre)
                                     FROM usuario_rol_empresa x JOIN roles r ON r.id = x.rol_id
                                    WHERE x.usuario_id = u.id AND x.empresa_id = e.id AND x.activo),
                                  'Facturador') END AS rol,
               CASE WHEN u.nivel_cuenta IN ('dueno', 'colaborador_admin') THEN 'empresa'
                    ELSE COALESCE((SELECT a.alcance FROM usuario_alcance a
                                    WHERE a.usuario_id = u.id AND a.empresa_id = e.id AND a.activo), 'empresa') END AS alcance
          FROM empresas e
          JOIN cuentas c  ON c.id = e.cuenta_id AND c.tipo = 'profesional'
          JOIN usuarios u ON u.cuenta_id = e.cuenta_id AND u.nivel_cuenta IS NOT NULL AND u.activo
         WHERE e.id = ctx_empresa()
           AND puede_abrir(u.id, e.id)
      ) t;
$$;

ALTER FUNCTION estudio_en_empresa() OWNER TO plataforma_puertas;
REVOKE ALL ON FUNCTION estudio_en_empresa() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION estudio_en_empresa() TO plataforma_app;


-- =============================================================================
-- 7. «MI EQUIPO» (Cuenta Profesional)
--
-- Quién puede: el dueño (a todos) y el colaborador administrador (a los
-- colaboradores y a sí mismo, para su ficha). Como las de la 018, devuelven
-- un código y la API lo traduce.
-- =============================================================================

-- ¿Quién llama y qué puede sobre p_usuario? 'admin' · 'si' · 'no'
CREATE OR REPLACE FUNCTION equipo_puede(p_usuario uuid)
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT CASE
        WHEN yo.id IS NULL OR yo.nivel_cuenta NOT IN ('dueno', 'colaborador_admin') OR c.tipo <> 'profesional' THEN 'no'
        WHEN o.id IS NULL THEN 'no_existe'
        WHEN yo.nivel_cuenta = 'dueno' THEN 'si'
        WHEN o.id = yo.id OR o.nivel_cuenta = 'colaborador' THEN 'si'
        ELSE 'no'
    END
      FROM (SELECT 1) uno
      LEFT JOIN usuarios yo ON yo.id = ctx_usuario() AND yo.activo
      LEFT JOIN cuentas  c  ON c.id = yo.cuenta_id
      LEFT JOIN usuarios o  ON o.id = p_usuario AND o.cuenta_id = yo.cuenta_id AND o.nivel_cuenta IS NOT NULL;
$$;


-- ¿Ese documento ya lo tiene alguien del equipo? Devuelve su nombre, o NULL.
CREATE OR REPLACE FUNCTION equipo_documento_de(p_tipo text, p_numero text, p_excepto uuid)
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT o.nombre || CASE WHEN o.activo THEN '' ELSE ' (quitado)' END
      FROM usuarios yo
      JOIN usuarios o ON o.cuenta_id = yo.cuenta_id AND o.nivel_cuenta IS NOT NULL
     WHERE yo.id = ctx_usuario()
       AND o.tipo_documento = p_tipo AND o.numero_documento = p_numero
       AND o.id IS DISTINCT FROM p_excepto
     LIMIT 1;
$$;


-- La ficha. p_ficha: tipoDocumento, numeroDocumento, nombres, apellidoPaterno,
-- apellidoMaterno, celular, fechaNacimiento, direccion, ubigeo, cargo,
-- fechaIngreso (la API ya la validó; la base tiene sus CHECK de respaldo).
-- ok · sin_permiso · no_existe · documento_repetido
CREATE OR REPLACE FUNCTION equipo_guardar_ficha(p_usuario uuid, p_ficha jsonb)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_puede text := equipo_puede(p_usuario);
    v_nombre text;
BEGIN
    IF v_puede = 'no_existe' THEN RETURN 'no_existe'; END IF;
    IF v_puede <> 'si' THEN RETURN 'sin_permiso'; END IF;

    IF equipo_documento_de(p_ficha ->> 'tipoDocumento', p_ficha ->> 'numeroDocumento', p_usuario) IS NOT NULL THEN
        RETURN 'documento_repetido';
    END IF;

    v_nombre := btrim(concat_ws(' ', p_ficha ->> 'nombres', p_ficha ->> 'apellidoPaterno', p_ficha ->> 'apellidoMaterno'));

    UPDATE usuarios SET
        tipo_documento   = NULLIF(p_ficha ->> 'tipoDocumento', ''),
        numero_documento = NULLIF(p_ficha ->> 'numeroDocumento', ''),
        nombres          = NULLIF(p_ficha ->> 'nombres', ''),
        apellido_paterno = NULLIF(p_ficha ->> 'apellidoPaterno', ''),
        apellido_materno = NULLIF(p_ficha ->> 'apellidoMaterno', ''),
        celular          = NULLIF(p_ficha ->> 'celular', ''),
        fecha_nacimiento = NULLIF(p_ficha ->> 'fechaNacimiento', '')::date,
        direccion        = NULLIF(p_ficha ->> 'direccion', ''),
        ubigeo           = NULLIF(p_ficha ->> 'ubigeo', ''),
        cargo            = NULLIF(p_ficha ->> 'cargo', ''),
        fecha_ingreso    = NULLIF(p_ficha ->> 'fechaIngreso', '')::date,
        nombre           = CASE WHEN length(v_nombre) >= 2 THEN v_nombre ELSE nombre END
     WHERE id = p_usuario;

    RETURN 'ok';
END $$;


-- El acceso de un colaborador a UNA empresa: rol, alcance y sucursales.
-- Si todavía no la veía (sin «Todas»), se le da.
-- ok · sin_permiso · no_existe · nivel · empresa_ajena · rol · sucursal · alcance
CREATE OR REPLACE FUNCTION equipo_guardar_acceso(
    p_usuario    uuid,
    p_empresa    uuid,
    p_rol        uuid,
    p_alcance    text,
    p_sucursales uuid[],
    p_defecto    uuid,
    p_notas      boolean
) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_puede  text := equipo_puede(p_usuario);
    v_nivel  text;
    v_todas  boolean;
    v_cuenta uuid;
    v_suc    uuid[] := COALESCE(p_sucursales, '{}');
BEGIN
    IF v_puede = 'no_existe' THEN RETURN 'no_existe'; END IF;
    IF v_puede <> 'si' THEN RETURN 'sin_permiso'; END IF;

    SELECT nivel_cuenta, ve_todas, cuenta_id INTO v_nivel, v_todas, v_cuenta FROM usuarios WHERE id = p_usuario;

    -- El dueño y los colaboradores administradores son administradores en todas.
    IF v_nivel <> 'colaborador' THEN RETURN 'nivel'; END IF;

    IF NOT EXISTS (SELECT 1 FROM empresas e WHERE e.id = p_empresa AND e.cuenta_id = v_cuenta AND e.activo) THEN
        RETURN 'empresa_ajena';
    END IF;

    -- Un rol de la plataforma (menos el Administrador y los que se suman) o
    -- uno propio de ESA empresa.
    IF NOT EXISTS (
        SELECT 1 FROM roles r
         WHERE r.id = p_rol AND r.activo AND r.basado_en IS NULL
           AND ((r.empresa_id IS NULL AND r.id NOT IN ('11111111-1111-1111-1111-111111111111',
                                                      '66666666-6666-6666-6666-666666666666',
                                                      '77777777-7777-7777-7777-777777777777'))
                OR r.empresa_id = p_empresa)) THEN
        RETURN 'rol';
    END IF;

    IF p_alcance NOT IN ('empresa', 'sucursal', 'propio') THEN RETURN 'alcance'; END IF;
    IF p_alcance <> 'empresa' AND cardinality(v_suc) = 0
       AND (SELECT count(*) FROM sucursales s WHERE s.empresa_id = p_empresa AND s.eliminada_en IS NULL AND s.activo) > 1 THEN
        RETURN 'sucursal';
    END IF;
    IF EXISTS (SELECT 1 FROM unnest(v_suc) x(id)
                WHERE NOT EXISTS (SELECT 1 FROM sucursales s
                                   WHERE s.id = x.id AND s.empresa_id = p_empresa AND s.eliminada_en IS NULL AND s.activo)) THEN
        RETURN 'sucursal';
    END IF;
    IF p_defecto IS NOT NULL AND cardinality(v_suc) > 0 AND NOT (p_defecto = ANY (v_suc)) THEN
        RETURN 'sucursal';
    END IF;

    -- Que la vea.
    IF NOT v_todas THEN
        INSERT INTO usuario_empresa (usuario_id, empresa_id) VALUES (p_usuario, p_empresa)
        ON CONFLICT (usuario_id, empresa_id) DO UPDATE SET activo = true;
    END IF;

    -- El rol en esa empresa (y «notas», que se suma).
    UPDATE usuario_rol_empresa SET activo = false
     WHERE usuario_id = p_usuario AND empresa_id = p_empresa AND activo
       AND NOT (rol_id = p_rol OR (p_notas AND rol_id = '66666666-6666-6666-6666-666666666666'));
    INSERT INTO usuario_rol_empresa (usuario_id, empresa_id, rol_id) VALUES (p_usuario, p_empresa, p_rol)
    ON CONFLICT (usuario_id, empresa_id, rol_id) DO UPDATE SET activo = true;
    IF p_notas THEN
        INSERT INTO usuario_rol_empresa (usuario_id, empresa_id, rol_id)
        VALUES (p_usuario, p_empresa, '66666666-6666-6666-6666-666666666666')
        ON CONFLICT (usuario_id, empresa_id, rol_id) DO UPDATE SET activo = true;
    END IF;

    -- El alcance.
    INSERT INTO usuario_alcance (usuario_id, empresa_id, alcance, sucursal_defecto_id)
    VALUES (p_usuario, p_empresa, p_alcance, CASE WHEN cardinality(v_suc) > 0 THEN COALESCE(p_defecto, v_suc[1]) ELSE p_defecto END)
    ON CONFLICT (usuario_id, empresa_id) DO UPDATE
       SET alcance = EXCLUDED.alcance, sucursal_defecto_id = EXCLUDED.sucursal_defecto_id, activo = true;

    -- Sus sucursales en esa empresa.
    UPDATE usuario_sucursal SET activo = false
     WHERE usuario_id = p_usuario AND empresa_id = p_empresa AND activo AND NOT (sucursal_id = ANY (v_suc));
    INSERT INTO usuario_sucursal (empresa_id, usuario_id, sucursal_id)
    SELECT p_empresa, p_usuario, x.id FROM unnest(v_suc) x(id)
     WHERE NOT EXISTS (SELECT 1 FROM usuario_sucursal us
                        WHERE us.usuario_id = p_usuario AND us.sucursal_id = x.id AND us.activo);

    PERFORM registrar_evento('otro'::text, true, p_usuario, p_empresa, NULL,
        jsonb_build_object('accion', 'acceso_equipo', 'alcance', p_alcance));

    RETURN 'ok';
END $$;


-- Quitarle UNA empresa. Con «Todas» no se puede (se quita «Todas» primero).
-- ok · sin_permiso · no_existe · nivel · ve_todas
CREATE OR REPLACE FUNCTION equipo_quitar_acceso(p_usuario uuid, p_empresa uuid)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_puede text := equipo_puede(p_usuario);
    v_nivel text;
    v_todas boolean;
BEGIN
    IF v_puede = 'no_existe' THEN RETURN 'no_existe'; END IF;
    IF v_puede <> 'si' THEN RETURN 'sin_permiso'; END IF;

    SELECT nivel_cuenta, ve_todas INTO v_nivel, v_todas FROM usuarios WHERE id = p_usuario;
    IF v_nivel <> 'colaborador' THEN RETURN 'nivel'; END IF;
    IF v_todas THEN RETURN 've_todas'; END IF;

    UPDATE usuario_empresa     SET activo = false WHERE usuario_id = p_usuario AND empresa_id = p_empresa AND activo;
    UPDATE usuario_rol_empresa SET activo = false WHERE usuario_id = p_usuario AND empresa_id = p_empresa AND activo;
    UPDATE usuario_alcance     SET activo = false WHERE usuario_id = p_usuario AND empresa_id = p_empresa AND activo;
    UPDATE usuario_sucursal    SET activo = false WHERE usuario_id = p_usuario AND empresa_id = p_empresa AND activo;

    PERFORM registrar_evento('otro'::text, true, p_usuario, p_empresa, NULL,
        jsonb_build_object('accion', 'acceso_equipo_quitado'));

    RETURN 'ok';
END $$;


-- El equipo con su ficha y sus accesos (solo el dueño y los colaboradores
-- administradores: la ficha es dato personal).
CREATE OR REPLACE FUNCTION equipo()
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE(json_agg(p ORDER BY p.orden, p.activo DESC, p.nombre), '[]'::json)::text
      FROM (
        SELECT o.id, o.nombre, o.correo, o.alias, o.nivel_cuenta AS nivel, o.ve_todas AS "veTodas",
               o.activo, o.ultimo_ingreso_en IS NULL AS pendiente,
               CASE o.nivel_cuenta WHEN 'dueno' THEN 0 WHEN 'colaborador_admin' THEN 1 ELSE 2 END AS orden,
               o.tipo_documento AS "tipoDocumento", o.numero_documento AS "numeroDocumento",
               o.nombres, o.apellido_paterno AS "apellidoPaterno", o.apellido_materno AS "apellidoMaterno",
               o.celular, o.fecha_nacimiento AS "fechaNacimiento", o.direccion, o.ubigeo, o.cargo,
               o.fecha_ingreso AS "fechaIngreso",
               (o.numero_documento IS NULL OR o.nombres IS NULL OR o.apellido_paterno IS NULL
                OR (o.tipo_documento = '1' AND o.apellido_materno IS NULL)) AS "fichaIncompleta",
               COALESCE((
                 SELECT json_agg(json_build_object(
                          'empresaId', e.id,
                          'empresa', e.nombre,
                          'rolId', COALESCE(rx.rol_id, CASE WHEN o.nivel_cuenta = 'colaborador'
                                                            THEN '22222222-2222-2222-2222-222222222222'::uuid
                                                            ELSE '11111111-1111-1111-1111-111111111111'::uuid END),
                          'rol', COALESCE(rx.nombre, CASE WHEN o.nivel_cuenta = 'colaborador' THEN 'Facturador' ELSE 'Administrador' END),
                          'notas', EXISTS (SELECT 1 FROM usuario_rol_empresa n
                                            WHERE n.usuario_id = o.id AND n.empresa_id = e.id AND n.activo
                                              AND n.rol_id = '66666666-6666-6666-6666-666666666666'),
                          'alcance', CASE WHEN o.nivel_cuenta = 'colaborador' THEN COALESCE(a.alcance, 'empresa') ELSE 'empresa' END,
                          'defecto', a.sucursal_defecto_id,
                          'sucursales', COALESCE((SELECT json_agg(us.sucursal_id) FROM usuario_sucursal us
                                                   WHERE us.usuario_id = o.id AND us.empresa_id = e.id AND us.activo), '[]'::json))
                        ORDER BY e.nombre)
                   FROM empresas e
                   LEFT JOIN LATERAL (
                        SELECT x.rol_id, r.nombre FROM usuario_rol_empresa x JOIN roles r ON r.id = x.rol_id
                         WHERE x.usuario_id = o.id AND x.empresa_id = e.id AND x.activo
                           AND x.rol_id <> '66666666-6666-6666-6666-666666666666'
                         LIMIT 1) rx ON true
                   LEFT JOIN usuario_alcance a ON a.usuario_id = o.id AND a.empresa_id = e.id AND a.activo
                  WHERE e.cuenta_id = o.cuenta_id AND e.activo
                    AND (o.nivel_cuenta = 'dueno' OR o.ve_todas
                         OR EXISTS (SELECT 1 FROM usuario_empresa ue
                                     WHERE ue.usuario_id = o.id AND ue.empresa_id = e.id AND ue.activo))
               ), '[]'::json) AS accesos
          FROM usuarios yo
          JOIN cuentas c  ON c.id = yo.cuenta_id AND c.tipo = 'profesional'
          JOIN usuarios o ON o.cuenta_id = yo.cuenta_id AND o.nivel_cuenta IS NOT NULL
         WHERE yo.id = ctx_usuario() AND yo.activo
           AND yo.nivel_cuenta IN ('dueno', 'colaborador_admin')
      ) p;
$$;


-- Las empresas de la cuenta, con sus sucursales y los roles que se pueden dar
-- en cada una (para el diálogo de acceso).
CREATE OR REPLACE FUNCTION equipo_empresas()
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE(json_agg(json_build_object(
             'id', e.id, 'nombre', e.nombre,
             'sucursales', COALESCE((SELECT json_agg(json_build_object('id', s.id, 'codigo', s.codigo, 'nombre', s.nombre)
                                                     ORDER BY s.codigo)
                                       FROM sucursales s
                                      WHERE s.empresa_id = e.id AND s.eliminada_en IS NULL AND s.activo), '[]'::json),
             'roles', COALESCE((SELECT json_agg(json_build_object('id', r.id, 'nombre', r.nombre,
                                                                  'descripcion', r.descripcion,
                                                                  'deSistema', r.empresa_id IS NULL)
                                                ORDER BY r.empresa_id IS NOT NULL, r.nombre)
                                  FROM roles r
                                 WHERE r.activo AND r.basado_en IS NULL
                                   AND ((r.empresa_id IS NULL AND r.id NOT IN ('11111111-1111-1111-1111-111111111111',
                                                                              '66666666-6666-6666-6666-666666666666',
                                                                              '77777777-7777-7777-7777-777777777777'))
                                        OR r.empresa_id = e.id)), '[]'::json))
           ORDER BY e.nombre), '[]'::json)::text
      FROM usuarios yo
      JOIN cuentas c  ON c.id = yo.cuenta_id AND c.tipo = 'profesional'
      JOIN empresas e ON e.cuenta_id = yo.cuenta_id AND e.activo AND e.pausada_en IS NULL
     WHERE yo.id = ctx_usuario() AND yo.activo
       AND yo.nivel_cuenta IN ('dueno', 'colaborador_admin');
$$;


-- =============================================================================
-- 8. REPORTES DE LA CARTERA: sin las empresas donde el colaborador solo ve su
-- sucursal o lo suyo (los totales de toda la empresa no son para él).
-- Misma firma que en la 036.
-- =============================================================================

CREATE OR REPLACE FUNCTION reporte_cartera(p_desde date, p_hasta date)
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    WITH yo AS (
        SELECT u.id, u.cuenta_id, u.nivel_cuenta, u.ve_todas
          FROM usuarios u
         WHERE u.id = ctx_usuario() AND u.activo AND u.nivel_cuenta IS NOT NULL
    ), emp AS (
        SELECT e.id, e.nombre, e.ruc, e.razon_social, e.pausada_en,
               CASE WHEN e.ambiente = 'produccion' THEN 'produccion' ELSE 'beta' END AS amb,
               cf.certificado_valido_hasta
          FROM yo
          JOIN empresas e ON e.cuenta_id = yo.cuenta_id AND e.activo
          LEFT JOIN credenciales_facturacion cf ON cf.empresa_id = e.id AND cf.activo
         WHERE (yo.nivel_cuenta = 'dueno' OR yo.ve_todas
                OR EXISTS (SELECT 1 FROM usuario_empresa ue
                            WHERE ue.usuario_id = yo.id AND ue.empresa_id = e.id AND ue.activo))
           -- 046: donde solo ve su sucursal o lo suyo, los totales de la
           -- empresa no son para él: esa empresa no sale aquí.
           AND NOT (yo.nivel_cuenta = 'colaborador' AND EXISTS (
                    SELECT 1 FROM usuario_alcance a
                     WHERE a.usuario_id = yo.id AND a.empresa_id = e.id AND a.activo AND a.alcance <> 'empresa'))
    ), doc AS (
        SELECT d.empresa_id, d.tipo, d.estado, d.importe_total, d.moneda, d.tipo_cambio, d.creado_en,
               CASE WHEN d.tipo = '07' THEN -1 ELSE 1 END AS signo
          FROM documentos d JOIN emp ON emp.id = d.empresa_id
         WHERE d.activo AND coalesce(d.ambiente, 'beta') = emp.amb
           AND d.fecha_emision BETWEEN p_desde AND p_hasta
    )
    SELECT json_build_object('filas', coalesce(json_agg(json_build_object(
        'id', emp.id, 'nombre', coalesce(emp.razon_social, emp.nombre), 'ruc', emp.ruc,
        'ambiente', emp.amb, 'pausada', emp.pausada_en IS NOT NULL,
        'certificadoHasta', emp.certificado_valido_hasta,
        'comprobantes', (SELECT count(*) FROM doc WHERE doc.empresa_id = emp.id AND doc.tipo IN ('01', '03')
                          AND doc.estado IN ('encolado', 'aceptado', 'observado')),
        'notas', (SELECT count(*) FROM doc WHERE doc.empresa_id = emp.id AND doc.tipo = '07'
                   AND doc.estado IN ('encolado', 'aceptado', 'observado')),
        'total', coalesce((SELECT sum(round(doc.signo * doc.importe_total *
                                CASE WHEN doc.moneda = 'PEN' THEN 1 ELSE coalesce(doc.tipo_cambio, 0) END, 2))
                             FROM doc WHERE doc.empresa_id = emp.id
                              AND doc.estado IN ('encolado', 'aceptado', 'observado')), 0),
        'rechazados', (SELECT count(*) FROM doc WHERE doc.empresa_id = emp.id AND doc.estado = 'rechazado'),
        'pendientes', (SELECT count(*) FROM doc WHERE doc.empresa_id = emp.id AND doc.estado IN ('encolado', 'error')),
        'ultimo', (SELECT max(doc.creado_en) FROM doc WHERE doc.empresa_id = emp.id))
      ORDER BY coalesce(emp.razon_social, emp.nombre)), '[]'::json))::text
      FROM emp;
$$;


DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY[
        'equipo_puede(uuid)', 'equipo_documento_de(text, text, uuid)', 'equipo_guardar_ficha(uuid, jsonb)',
        'equipo_guardar_acceso(uuid, uuid, uuid, text, uuid[], uuid, boolean)',
        'equipo_quitar_acceso(uuid, uuid)', 'equipo()', 'equipo_empresas()', 'reporte_cartera(date, date)']
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
    END LOOP;
END $$;


INSERT INTO migraciones_aplicadas (nombre) VALUES ('046_personal_y_alcance')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '046: ficha del personal, alcance por sucursal, sucursal activa y rol por empresa listos. Nada se borró.';
END $$;