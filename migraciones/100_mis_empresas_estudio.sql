-- =============================================================================
-- 100 — «MI EMPRESA» SE RECONOCE EN TODA LA CARTERA (e287, 28 nov 2026)
--
-- Arreglo de e283/e285/e286: para saber cuál empresa es «Mi empresa» se leía
-- empresas.es_del_estudio directo, pero la seguridad por filas (RLS) solo deja
-- ver la empresa ABIERTA. Las demás llegaban sin ese dato, y por eso «Mi
-- empresa» seguía saliendo en Mi cartera, en el selector y en los avisos como
-- si fuera de un cliente.
--
--   · mis_empresas_extra(): de las empresas de mi cuenta, cuál es la del
--     estudio y cuándo se creó (con permisos de puerta, solo mi cuenta).
--
-- Se puede correr dos veces. Nada se borra.
-- =============================================================================

BEGIN;

CREATE OR REPLACE FUNCTION mis_empresas_extra()
RETURNS TABLE (id uuid, ruc text, es_del_estudio boolean, creado_en timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT e.id, e.ruc::text, e.es_del_estudio, e.creado_en
      FROM empresas e
      JOIN usuarios u ON u.cuenta_id = e.cuenta_id
     WHERE u.id = ctx_usuario() AND u.activo AND e.activo
$$;
COMMENT ON FUNCTION mis_empresas_extra() IS '100: de las empresas de mi cuenta, cuál es «Mi empresa» (la del estudio) y cuándo se creó.';

DO $$ BEGIN
    ALTER FUNCTION mis_empresas_extra() OWNER TO plataforma_puertas;
    REVOKE ALL ON FUNCTION mis_empresas_extra() FROM PUBLIC;
    GRANT EXECUTE ON FUNCTION mis_empresas_extra() TO plataforma_app;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('100_mis_empresas_estudio')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '100: «Mi empresa» se reconoce en Mi cartera, el selector y los avisos. Nada se borró.';
END $$;