-- =============================================================================
-- 041 — «VER COMO» TAMBIÉN CON EL DUEÑO Y LOS COLABORADORES DE UNA CUENTA
--        (21 oct 2026, encontrado en QA)
--
-- QUÉ PASABA: el super administrador entraba a «Ver como» el dueño de una
-- Cuenta Negocio (quien se registró solo) y la pantalla lo sacaba al login.
--
-- POR QUÉ: con «ver como», a la base le llega como usuario el SUPER (el autor,
-- para la bitácora) y como empresa la del suplantado, sin poderes de super.
-- La regla de `usuarios` deja ver a uno mismo y a la gente de la empresa
-- abierta, pero NO a los de una cuenta (nivel_cuenta: dueño, colaborador),
-- a propósito desde la 018. Así que la persona a la que se estaba mirando
-- era invisible: /api/yo no la encontraba y respondía 401.
--
-- EL ARREGLO: la base sabe a quién se está mirando. `fijar_suplantado(sesión)`
-- lo anota (solo si esa sesión de verdad está en «ver como», es del usuario
-- del contexto y sigue viva) y `ctx_suplantado()` lo lee. Las reglas de
-- usuarios, usuario_rol y cuentas dejan LEER a esa persona, a sus roles y a
-- su cuenta. Escribir no cambia: «ver como» es solo lectura (lo para el
-- middleware) y las cláusulas WITH CHECK quedan como estaban.
--
-- No se borra nada. Se puede correr dos veces.
-- =============================================================================

BEGIN;

CREATE OR REPLACE FUNCTION ctx_suplantado()
RETURNS uuid
LANGUAGE sql STABLE AS $$
    SELECT NULLIF(current_setting('app.suplantado_id', true), '')::uuid;
$$;

GRANT EXECUTE ON FUNCTION ctx_suplantado() TO plataforma_app;


-- Anota a quién se está mirando, para esta transacción. Devuelve si se anotó.
CREATE OR REPLACE FUNCTION fijar_suplantado(p_sesion uuid)
RETURNS boolean
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_objetivo uuid;
BEGIN
    SELECT s.suplantando_usuario_id INTO v_objetivo
      FROM sesiones s
     WHERE s.id = p_sesion
       AND s.usuario_id = ctx_usuario()
       AND s.suplantando_usuario_id IS NOT NULL
       AND s.revocada_en IS NULL
       AND s.expira_en > now();

    PERFORM set_config('app.suplantado_id', COALESCE(v_objetivo::text, ''), true);
    RETURN v_objetivo IS NOT NULL;
END $$;

ALTER FUNCTION fijar_suplantado(uuid) OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION fijar_suplantado(uuid) TO plataforma_app;


-- --- Las tres reglas, con la persona que se está mirando (solo lectura) ----

DROP POLICY IF EXISTS p_usuarios ON usuarios;
CREATE POLICY p_usuarios ON usuarios
    USING (ctx_es_super()
           OR id = ctx_usuario()
           OR id = ctx_suplantado()
           OR (empresa_id = ctx_empresa() AND nivel_cuenta IS NULL))
    WITH CHECK (ctx_es_super()
           OR id = ctx_usuario()
           OR (empresa_id = ctx_empresa() AND nivel_cuenta IS NULL));

DROP POLICY IF EXISTS p_usuario_rol ON usuario_rol;
CREATE POLICY p_usuario_rol ON usuario_rol
    USING (ctx_es_super()
           OR usuario_rol.usuario_id = ctx_usuario()
           OR usuario_rol.usuario_id = ctx_suplantado()
           OR EXISTS (SELECT 1 FROM usuarios u WHERE u.id = usuario_rol.usuario_id
                         AND u.empresa_id = ctx_empresa() AND u.nivel_cuenta IS NULL))
    WITH CHECK (ctx_es_super()
           OR EXISTS (SELECT 1 FROM usuarios u WHERE u.id = usuario_rol.usuario_id
                         AND u.empresa_id = ctx_empresa() AND u.nivel_cuenta IS NULL));

DROP POLICY IF EXISTS p_cuentas ON cuentas;
CREATE POLICY p_cuentas ON cuentas
    USING (ctx_es_super()
           OR id = (SELECT u.cuenta_id FROM usuarios u WHERE u.id = ctx_usuario())
           OR id = (SELECT u.cuenta_id FROM usuarios u WHERE u.id = ctx_suplantado()))
    WITH CHECK (ctx_es_super());


INSERT INTO migraciones_aplicadas (nombre) VALUES ('041_ver_como_cuentas')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN RAISE NOTICE '041: «ver como» también con el dueño y los colaboradores de una cuenta. Nada se borró.'; END $$;