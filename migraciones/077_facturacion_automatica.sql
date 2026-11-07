-- =============================================================================
-- 077 — FACTURACIÓN AUTOMÁTICA PARA TODAS LAS EMPRESAS (1 nov 2026, e216)
--
-- La plataforma es para todo el país y la gente se registra sola: nadie tiene
-- que llamar para que le activen nada.
--
-- ANTES: activar_facturacion_propia (el último paso del asistente) solo
-- dejaba a las empresas de registro o contratadas en el alta, y si el super
-- administrador había quitado el módulo, decía «escríbenos».
--
-- AHORA: cualquier empresa cuyo administrador termina «Configurar
-- facturación» queda con Facturación activa, también si estaba apagada. La
-- API solo llama aquí cuando el servicio dice que la empresa está LISTA
-- (certificado, usuario SOL, una serie, dirección); si falta algo, la API se
-- lo dice con palabras y no llama.
--
-- Para que una empresa deje de emitir se usa «Suspender» (con su motivo, que
-- la empresa ve), no la casilla del módulo.
--
-- No se borra nada. Se puede correr dos veces.
-- =============================================================================
BEGIN;

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

    -- Una empresa suspendida no se activa nada (ve el motivo al entrar).
    IF EXISTS (SELECT 1 FROM empresas e WHERE e.id = v_empresa AND (NOT e.activo OR e.suspendida_en IS NOT NULL)) THEN
        RETURN 'suspendida';
    END IF;

    SELECT m.activo INTO v_activo
      FROM empresa_modulo m
     WHERE m.empresa_id = v_empresa AND m.modulo_codigo = 'facturacion';

    IF FOUND AND v_activo THEN
        RETURN 'ya_activo';
    ELSIF FOUND THEN
        UPDATE empresa_modulo SET activo = true, vence_en = NULL
         WHERE empresa_id = v_empresa AND modulo_codigo = 'facturacion';
    ELSE
        INSERT INTO empresa_modulo (empresa_id, modulo_codigo, contratado_en)
        VALUES (v_empresa, 'facturacion', now());
    END IF;

    PERFORM registrar_evento('otro'::text, true, NULL, v_empresa, NULL,
        jsonb_build_object('accion', 'facturacion_activada_por_asistente'));

    RETURN 'ok';
END $$;
ALTER FUNCTION activar_facturacion_propia() OWNER TO plataforma_puertas;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('077_facturacion_automatica')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '077: Facturación se activa sola al terminar el asistente, en cualquier empresa. Nada se borró.';
END $$;