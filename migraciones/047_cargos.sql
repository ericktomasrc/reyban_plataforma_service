-- =============================================================================
-- 047 — CARGOS COMO TABLA MAESTRA (24 oct 2026)
--
-- Como los «Puestos de trabajo» de Odoo: el cargo de una persona apunta a una
-- tabla, que es DE CADA EMPRESA (su personal) o DEL ESTUDIO (el equipo de una
-- Cuenta Profesional). Son maestras distintas.
--
--   · Al escribir el cargo en la ficha se busca en la maestra que toca (sin
--     distinguir mayúsculas ni espacios de más); si no está, se crea sola.
--     Lo hace la BASE (disparador en usuarios): la API y las funciones de Mi
--     equipo siguen escribiendo el texto `usuarios.cargo` como antes.
--   · `usuarios.cargo` queda como copia del nombre, para leer sin juntar
--     tablas. Al renombrar un cargo, se cambia en todas las fichas que lo usan.
--   · Renombrar uno en uso pide motivo (queda en la bitácora). Para un cambio
--     de puesto de verdad: «Pasar a otro cargo» y desactivar el viejo.
--   · No se desactiva un cargo que usa alguien activo. Nada se borra.
--
-- Las fichas de antes con cargo escrito se pasan a la maestra aquí.
-- Se puede correr dos veces.
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS cargos (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id      uuid        REFERENCES empresas (id),
    cuenta_id       uuid        REFERENCES cuentas (id),
    nombre          text        NOT NULL,
    -- El porqué del último cambio de nombre (la bitácora guarda el antes y el después).
    motivo          text,

    creado_en       timestamptz NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean     NOT NULL DEFAULT true,
    version         integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_cargos_dueno  CHECK ((empresa_id IS NULL) <> (cuenta_id IS NULL)),
    CONSTRAINT ck_cargos_nombre CHECK (length(btrim(nombre)) BETWEEN 1 AND 80),
    CONSTRAINT ck_cargos_motivo CHECK (motivo IS NULL OR length(motivo) <= 300)
);

-- Sin repetir nombres (activos o no: uno desactivado se reactiva, no se duplica).
CREATE UNIQUE INDEX IF NOT EXISTS ux_cargos_empresa
    ON cargos (empresa_id, lower(btrim(nombre))) WHERE empresa_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_cargos_cuenta
    ON cargos (cuenta_id, lower(btrim(nombre))) WHERE cuenta_id IS NOT NULL;

COMMENT ON TABLE cargos IS
    '047: maestra de cargos. empresa_id = de esa empresa (Personal); cuenta_id = del estudio (Mi equipo).';

ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS cargo_id uuid REFERENCES cargos (id);
CREATE INDEX IF NOT EXISTS ix_usuarios_cargo ON usuarios (cargo_id) WHERE cargo_id IS NOT NULL;

ALTER TABLE cargos ENABLE ROW LEVEL SECURITY;
ALTER TABLE cargos FORCE  ROW LEVEL SECURITY;

-- La aplicación ve y escribe los de su empresa. Los del estudio, por las
-- funciones de Mi equipo.
DROP POLICY IF EXISTS p_cargos ON cargos;
CREATE POLICY p_cargos ON cargos
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'cargos'::regclass AND NOT tgisinternal) THEN
        PERFORM vigilar_tabla('cargos');
    END IF;
END $$;

GRANT SELECT, INSERT, UPDATE ON cargos TO plataforma_app;


-- =============================================================================
-- EL CARGO DE LA FICHA → LA MAESTRA (disparador en usuarios)
-- =============================================================================

-- Busca el cargo en la maestra de esa persona (su empresa, o el estudio si es
-- de una cuenta); si no está, lo crea; si estaba desactivado, lo reactiva.
CREATE OR REPLACE FUNCTION cargo_de(p_empresa uuid, p_cuenta uuid, p_nivel text, p_nombre text)
RETURNS uuid
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_nombre text := regexp_replace(btrim(coalesce(p_nombre, '')), '\s+', ' ', 'g');
    v_id     uuid;
    v_activo boolean;
BEGIN
    IF v_nombre = '' THEN RETURN NULL; END IF;

    IF p_nivel IS NOT NULL AND p_cuenta IS NOT NULL THEN
        SELECT id, activo INTO v_id, v_activo FROM cargos
         WHERE cuenta_id = p_cuenta AND lower(btrim(nombre)) = lower(v_nombre);
        IF v_id IS NULL THEN
            INSERT INTO cargos (cuenta_id, nombre) VALUES (p_cuenta, v_nombre) RETURNING id INTO v_id;
        END IF;
    ELSIF p_empresa IS NOT NULL THEN
        SELECT id, activo INTO v_id, v_activo FROM cargos
         WHERE empresa_id = p_empresa AND lower(btrim(nombre)) = lower(v_nombre);
        IF v_id IS NULL THEN
            INSERT INTO cargos (empresa_id, nombre) VALUES (p_empresa, v_nombre) RETURNING id INTO v_id;
        END IF;
    ELSE
        RETURN NULL;    -- el super administrador no tiene maestra
    END IF;

    IF v_activo = false THEN
        UPDATE cargos SET activo = true WHERE id = v_id;
    END IF;
    RETURN v_id;
END $$;

CREATE OR REPLACE FUNCTION fn_usuarios_cargo()
RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF TG_OP = 'INSERT' OR NEW.cargo IS DISTINCT FROM OLD.cargo
       OR (NEW.cargo IS NOT NULL AND NEW.cargo_id IS NULL) THEN
        NEW.cargo_id := cargo_de(NEW.empresa_id, NEW.cuenta_id, NEW.nivel_cuenta, NEW.cargo);
        NEW.cargo := (SELECT nombre FROM cargos WHERE id = NEW.cargo_id);
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS cargo_usuarios ON usuarios;
CREATE TRIGGER cargo_usuarios
    BEFORE INSERT OR UPDATE OF cargo, cargo_id ON usuarios
    FOR EACH ROW EXECUTE FUNCTION fn_usuarios_cargo();

-- Al renombrar un cargo, la copia del nombre cambia en todas sus fichas.
CREATE OR REPLACE FUNCTION fn_cargos_renombrar()
RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NEW.nombre IS DISTINCT FROM OLD.nombre THEN
        UPDATE usuarios SET cargo = NEW.nombre WHERE cargo_id = NEW.id AND cargo IS DISTINCT FROM NEW.nombre;
    END IF;
    RETURN NULL;
END $$;

DROP TRIGGER IF EXISTS renombrar_cargos ON cargos;
CREATE TRIGGER renombrar_cargos
    AFTER UPDATE OF nombre ON cargos
    FOR EACH ROW EXECUTE FUNCTION fn_cargos_renombrar();


-- =============================================================================
-- CARGOS DEL ESTUDIO (Mi equipo). Solo el dueño y los colaboradores
-- administradores. Devuelven un código, como las de la 018 y la 046.
-- =============================================================================

CREATE OR REPLACE FUNCTION estudio_cuenta_admin()
RETURNS uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT u.cuenta_id FROM usuarios u JOIN cuentas c ON c.id = u.cuenta_id AND c.tipo = 'profesional'
     WHERE u.id = ctx_usuario() AND u.activo AND u.nivel_cuenta IN ('dueno', 'colaborador_admin');
$$;

-- [{id, nombre, activo, usos, usosActivos, motivo}]
CREATE OR REPLACE FUNCTION cargos_estudio()
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE(json_agg(json_build_object(
             'id', c.id, 'nombre', c.nombre, 'activo', c.activo, 'motivo', c.motivo,
             'usos', (SELECT count(*) FROM usuarios u WHERE u.cargo_id = c.id),
             'usosActivos', (SELECT count(*) FROM usuarios u WHERE u.cargo_id = c.id AND u.activo),
             'personas', COALESCE((SELECT json_agg(json_build_object('id', u.id, 'nombre', u.nombre, 'activo', u.activo) ORDER BY u.nombre)
                                     FROM usuarios u WHERE u.cargo_id = c.id), '[]'::json))
           ORDER BY c.activo DESC, lower(c.nombre)), '[]'::json)::text
      FROM cargos c
     WHERE c.cuenta_id = estudio_cuenta_admin();
$$;

-- Crear (p_id NULL) o renombrar. ok · sin_permiso · no_existe · repetido · motivo · nombre
CREATE OR REPLACE FUNCTION cargo_estudio_guardar(p_id uuid, p_nombre text, p_motivo text)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := estudio_cuenta_admin();
    v_nombre text := regexp_replace(btrim(coalesce(p_nombre, '')), '\s+', ' ', 'g');
BEGIN
    IF v_cuenta IS NULL THEN RETURN 'sin_permiso'; END IF;
    IF length(v_nombre) NOT BETWEEN 1 AND 80 THEN RETURN 'nombre'; END IF;
    IF EXISTS (SELECT 1 FROM cargos WHERE cuenta_id = v_cuenta AND lower(btrim(nombre)) = lower(v_nombre)
                AND id IS DISTINCT FROM p_id) THEN
        RETURN 'repetido';
    END IF;

    IF p_id IS NULL THEN
        INSERT INTO cargos (cuenta_id, nombre) VALUES (v_cuenta, v_nombre);
        RETURN 'ok';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM cargos WHERE id = p_id AND cuenta_id = v_cuenta) THEN RETURN 'no_existe'; END IF;
    IF EXISTS (SELECT 1 FROM usuarios WHERE cargo_id = p_id) AND length(btrim(coalesce(p_motivo, ''))) < 5 THEN
        RETURN 'motivo';
    END IF;

    UPDATE cargos SET nombre = v_nombre, motivo = NULLIF(btrim(coalesce(p_motivo, '')), '') WHERE id = p_id;
    RETURN 'ok';
END $$;

-- ok · sin_permiso · no_existe · en_uso
CREATE OR REPLACE FUNCTION cargo_estudio_activo(p_id uuid, p_activo boolean)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := estudio_cuenta_admin();
BEGIN
    IF v_cuenta IS NULL THEN RETURN 'sin_permiso'; END IF;
    IF NOT EXISTS (SELECT 1 FROM cargos WHERE id = p_id AND cuenta_id = v_cuenta) THEN RETURN 'no_existe'; END IF;
    IF NOT p_activo AND EXISTS (SELECT 1 FROM usuarios WHERE cargo_id = p_id AND activo) THEN RETURN 'en_uso'; END IF;
    UPDATE cargos SET activo = p_activo WHERE id = p_id;
    RETURN 'ok';
END $$;

-- Pasar personas de un cargo a otro (p_usuarios NULL = todas).
-- ok · sin_permiso · no_existe · mismo
CREATE OR REPLACE FUNCTION cargo_estudio_pasar(p_desde uuid, p_hacia uuid, p_usuarios uuid[])
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_cuenta uuid := estudio_cuenta_admin();
    v_nombre text;
BEGIN
    IF v_cuenta IS NULL THEN RETURN 'sin_permiso'; END IF;
    IF p_desde = p_hacia THEN RETURN 'mismo'; END IF;
    SELECT nombre INTO v_nombre FROM cargos WHERE id = p_hacia AND cuenta_id = v_cuenta;
    IF v_nombre IS NULL OR NOT EXISTS (SELECT 1 FROM cargos WHERE id = p_desde AND cuenta_id = v_cuenta) THEN
        RETURN 'no_existe';
    END IF;

    UPDATE usuarios SET cargo = v_nombre, cargo_id = p_hacia
     WHERE cargo_id = p_desde AND cuenta_id = v_cuenta
       AND (p_usuarios IS NULL OR id = ANY (p_usuarios));
    RETURN 'ok';
END $$;


DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY[
        'cargo_de(uuid, uuid, text, text)', 'fn_usuarios_cargo()', 'fn_cargos_renombrar()',
        'estudio_cuenta_admin()', 'cargos_estudio()', 'cargo_estudio_guardar(uuid, text, text)',
        'cargo_estudio_activo(uuid, boolean)', 'cargo_estudio_pasar(uuid, uuid, uuid[])']
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
    END LOOP;
END $$;


-- --- Las fichas de antes con cargo escrito → la maestra ------------------------
UPDATE usuarios SET cargo_id = NULL WHERE cargo IS NOT NULL AND cargo_id IS NULL;   -- dispara la búsqueda

INSERT INTO migraciones_aplicadas (nombre) VALUES ('047_cargos')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '047: cargos como maestra (% cargos). Nada se borró.', (SELECT count(*) FROM cargos);
END $$;