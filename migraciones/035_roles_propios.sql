-- =============================================================================
-- 035 — ROLES PROPIOS DE CADA EMPRESA (18 oct 2026, maqueta aprobada)
--
-- Los roles de la plataforma (Administrador, Facturador, Consulta, y ahora
-- Contabilidad) son los mismos para todas las empresas y no se editan. Cada
-- empresa puede COPIARLOS o crear los suyos y marcar qué permisos llevan.
-- La tabla `roles` ya admitía roles de empresa (empresa_id) desde la 002, con
-- su aislamiento: una empresa solo ve y toca los suyos.
--
-- Lo que esta migración añade:
--   · Un candado en la base: un rol PROPIO nunca puede llevar
--       - facturacion.credenciales_gestionar (certificado, series, SOL: la
--         llave para emitir en nombre de la empresa),
--       - usuarios.roles_gestionar (repartir permisos),
--       - nada del área «plataforma».
--     Eso queda solo para el Administrador.
--   · El rol de la plataforma «Contabilidad»: Consulta + todos los reportes.
--
-- Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

CREATE OR REPLACE FUNCTION fn_rol_permiso_propio()
RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.activo
       AND (NEW.permiso_codigo IN ('facturacion.credenciales_gestionar', 'usuarios.roles_gestionar')
            OR NEW.permiso_codigo LIKE 'plataforma.%')
       AND EXISTS (SELECT 1 FROM roles r WHERE r.id = NEW.rol_id AND r.empresa_id IS NOT NULL)
    THEN
        RAISE EXCEPTION 'El permiso % es solo del Administrador: no puede ir en un rol propio.', NEW.permiso_codigo
              USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS candado_rol_permiso_propio ON rol_permiso;
CREATE TRIGGER candado_rol_permiso_propio
    BEFORE INSERT OR UPDATE ON rol_permiso
    FOR EACH ROW EXECUTE FUNCTION fn_rol_permiso_propio();


-- El rol «Contabilidad», de la plataforma: para el estudio contable.
INSERT INTO roles (id, empresa_id, nombre, descripcion, editable)
SELECT '44444444-4444-4444-4444-444444444444', NULL, 'Contabilidad',
       'Para el contador: ve los comprobantes, los descarga y ve todos los reportes. No emite.', true
 WHERE NOT EXISTS (SELECT 1 FROM roles WHERE id = '44444444-4444-4444-4444-444444444444');

INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT '44444444-4444-4444-4444-444444444444', p.codigo
  FROM permisos p
 WHERE (p.codigo IN ('facturacion.ver', 'facturacion.descargar') OR p.area = 'reportes')
   AND NOT EXISTS (SELECT 1 FROM rol_permiso rp
                    WHERE rp.rol_id = '44444444-4444-4444-4444-444444444444'
                      AND rp.permiso_codigo = p.codigo);

INSERT INTO migraciones_aplicadas (nombre) VALUES ('035_roles_propios')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$
DECLARE v_n int;
BEGIN
    SELECT count(*) INTO v_n FROM rol_permiso WHERE rol_id = '44444444-4444-4444-4444-444444444444';
    RAISE NOTICE '035: roles propios listos; rol «Contabilidad» con % permisos. Nada se borró.', v_n;
END $$;