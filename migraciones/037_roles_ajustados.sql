-- =============================================================================
-- 037 — AJUSTAR LOS ROLES DE LA PLATAFORMA, SOLO PARA UNA EMPRESA
--        (18 oct 2026, maqueta aprobada)
--
-- Los roles de la plataforma (Facturador, Consulta, Contabilidad) son los
-- mismos para todas las empresas. Desde aquí, cada empresa puede AJUSTARLOS
-- para ella sola: agregar o quitar permisos. El ajuste es un rol de la empresa
-- con `basado_en` = el rol de la plataforma, y con su mismo nombre.
--
--   · Las personas siguen asignadas al rol de la plataforma (no se mueve
--     nada): la vista `permisos_efectivos` usa el ajuste de SU empresa si lo
--     hay, y el original si no. Quien se asigne mañana a «Facturador» recibe
--     también el ajuste.
--   · Las demás empresas no cambian: el ajuste es de una empresa y el
--     aislamiento (RLS) de `roles` ya hace que otra no lo vea.
--   · Un ajuste activo por empresa y rol. El Administrador no se ajusta.
--   · «Volver al original» desactiva el ajuste. Nada se borra.
--   · El candado de la 035 sigue valiendo: un ajuste es un rol de empresa, así
--     que nunca lleva la llave del Administrador.
--
-- Se puede correr dos veces.
-- =============================================================================

BEGIN;

ALTER TABLE roles ADD COLUMN IF NOT EXISTS basado_en uuid REFERENCES roles (id);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_roles_basado_en') THEN
        ALTER TABLE roles ADD CONSTRAINT ck_roles_basado_en
            CHECK (basado_en IS NULL OR empresa_id IS NOT NULL);
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_roles_ajuste
    ON roles (empresa_id, basado_en) WHERE activo AND basado_en IS NOT NULL;


-- Solo se ajusta un rol de la plataforma que se pueda editar (no el
-- Administrador), y un ajuste no cambia de rol base después.
CREATE OR REPLACE FUNCTION fn_rol_ajuste()
RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.basado_en IS DISTINCT FROM OLD.basado_en THEN
        RAISE EXCEPTION 'Un ajuste no cambia de rol base.' USING ERRCODE = 'check_violation';
    END IF;
    IF NEW.basado_en IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM roles s WHERE s.id = NEW.basado_en AND s.empresa_id IS NULL AND s.editable)
    THEN
        RAISE EXCEPTION 'Solo se ajustan los roles de la plataforma que se pueden editar (el Administrador no).'
              USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS candado_rol_ajuste ON roles;
CREATE TRIGGER candado_rol_ajuste
    BEFORE INSERT OR UPDATE ON roles
    FOR EACH ROW EXECUTE FUNCTION fn_rol_ajuste();


-- Los permisos efectivos: el ajuste de la empresa del usuario, si lo hay.
-- Mismas columnas que antes (la usan menu_usuario y las puertas).
CREATE OR REPLACE VIEW permisos_efectivos WITH (security_invoker = true) AS
SELECT DISTINCT
       u.id         AS usuario_id,
       u.empresa_id,
       rp.permiso_codigo
  FROM usuarios u
  JOIN usuario_rol ur ON ur.usuario_id = u.id AND ur.activo
  JOIN roles       r  ON r.id  = ur.rol_id    AND r.activo
  LEFT JOIN roles  aj ON aj.basado_en = r.id  AND aj.activo AND aj.empresa_id = u.empresa_id
  JOIN rol_permiso rp ON rp.rol_id = COALESCE(aj.id, r.id) AND rp.activo
 WHERE u.activo;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('037_roles_ajustados')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN RAISE NOTICE '037: los roles de la plataforma se pueden ajustar por empresa. Nada se borró.'; END $$;