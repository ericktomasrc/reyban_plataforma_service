-- =============================================================================
-- 028 — FACTURACIÓN CONTRATADA EN EL ALTA: el administrador de la empresa la
-- configura él mismo con el asistente (17 oct)
--
-- ANTES: una empresa que daba de alta el super administrador nacía sin
-- Facturación, y su administrador no podía terminar el asistente: la última
-- puerta (activar_facturacion_propia) solo dejaba a las empresas de una cuenta
-- de registro. Había que entrar al panel del servicio.
--
-- AHORA:
--   - El alta del super administrador deja Facturación CONTRATADA
--     (empresas.facturacion_contratada_en). Contratada no es activa: el
--     módulo se enciende solo cuando el administrador termina «Configurar
--     facturación» (certificado, series, marca, usuario SOL) y el servicio
--     dice que la empresa está lista para emitir.
--   - La puerta deja pasar a las empresas de registro (como antes) y a las
--     contratadas. Lo demás no cambia: sin permiso no entra, y un módulo que
--     el super administrador apagó no se vuelve a encender solo.
--   - Solo el super administrador (o una migración) cambia la marca.
--
-- Las empresas del alta que ya existen y todavía no tienen Facturación quedan
-- contratadas. Las que ya la tienen (activa o apagada) no cambian.
--
-- No se borra nada. Se puede correr dos veces: no cambia nada la segunda.
-- =============================================================================

BEGIN;

ALTER TABLE empresas ADD COLUMN IF NOT EXISTS facturacion_contratada_en timestamptz;

COMMENT ON COLUMN empresas.facturacion_contratada_en IS
    'Cuándo se contrató Facturación en el alta del super administrador. Contratada no es activa: se activa al terminar el asistente.';

-- Las del alta que ya existen y todavía no tienen Facturación.
UPDATE empresas e
   SET facturacion_contratada_en = now()
  FROM cuentas c
 WHERE c.id = e.cuenta_id
   AND c.origen = 'alta'
   AND e.facturacion_contratada_en IS NULL
   AND NOT EXISTS (SELECT 1 FROM empresa_modulo m
                    WHERE m.empresa_id = e.id AND m.modulo_codigo = 'facturacion');

-- EL ALTA DEL SUPER ADMINISTRADOR es la que llega sin cuenta: este disparador
-- le crea su cuenta «alta». Desde hoy, además, la deja con Facturación
-- contratada.
CREATE OR REPLACE FUNCTION fn_empresas_cuenta() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.cuenta_id IS NULL THEN
        INSERT INTO cuentas (tipo, estado, confirmada_en, origen)
        VALUES ('negocio', 'activa', now(), 'alta')
        RETURNING id INTO NEW.cuenta_id;

        NEW.facturacion_contratada_en := COALESCE(NEW.facturacion_contratada_en, now());
    END IF;
    RETURN NEW;
END $$;

-- La marca no la cambia el administrador de la empresa: solo el super
-- administrador o quien corre las migraciones.
CREATE OR REPLACE FUNCTION fn_empresas_contratada() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.facturacion_contratada_en IS DISTINCT FROM OLD.facturacion_contratada_en
       AND NOT ctx_es_super()
       AND NOT (SELECT r.rolsuper FROM pg_roles r WHERE r.rolname = current_user) THEN
        RAISE EXCEPTION 'Solo el super administrador cambia si Facturación está contratada (empresa %).', OLD.id
              USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS tg_empresas_contratada ON empresas;
CREATE TRIGGER tg_empresas_contratada BEFORE UPDATE ON empresas
    FOR EACH ROW EXECUTE FUNCTION fn_empresas_contratada();

-- LA PUERTA: ahora también para las contratadas.
CREATE OR REPLACE FUNCTION activar_facturacion_propia() RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_usuario uuid := ctx_usuario();
    v_empresa uuid := ctx_empresa();
    v_activo  boolean;
BEGIN
    IF v_usuario IS NULL OR v_empresa IS NULL OR NOT EXISTS (
        SELECT 1 FROM usuarios u
         WHERE u.id = v_usuario AND u.activo
           AND (u.empresa_id = v_empresa OR puede_abrir(u.id, v_empresa))) THEN
        RETURN 'sin_empresa';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM permisos_efectivos p
                    WHERE p.usuario_id = v_usuario
                      AND p.permiso_codigo = 'facturacion.credenciales_gestionar') THEN
        RETURN 'sin_permiso';
    END IF;

    -- De registro (la armó su dueño) o contratada en el alta.
    IF NOT EXISTS (SELECT 1 FROM empresas e
                     LEFT JOIN cuentas c ON c.id = e.cuenta_id
                    WHERE e.id = v_empresa
                      AND (c.origen = 'registro' OR e.facturacion_contratada_en IS NOT NULL)) THEN
        RETURN 'no_es_autoservicio';
    END IF;

    SELECT m.activo INTO v_activo
      FROM empresa_modulo m
     WHERE m.empresa_id = v_empresa AND m.modulo_codigo = 'facturacion';

    IF FOUND THEN
        RETURN CASE WHEN v_activo THEN 'ya_activo' ELSE 'desactivado_por_admin' END;
    END IF;

    INSERT INTO empresa_modulo (empresa_id, modulo_codigo, contratado_en)
    VALUES (v_empresa, 'facturacion', now());

    PERFORM registrar_evento('otro'::text, true, NULL, v_empresa, NULL,
        jsonb_build_object('accion', 'facturacion_activada_por_asistente'));

    RETURN 'ok';
END $$;

ALTER FUNCTION activar_facturacion_propia() OWNER TO plataforma_puertas;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('028_facturacion_contratada')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;