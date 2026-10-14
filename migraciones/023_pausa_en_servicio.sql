-- =============================================================================
-- 023 — LA PAUSA TAMBIÉN EN EL SERVICIO (fase 4c)
--
-- Desde esta entrega, cuando la plataforma pone o quita una pausa, se lo dice
-- al servicio de facturación (POST /v1/configuracion/pausa con la clave de la
-- empresa): en pausa, esa clave solo puede consultar y descargar.
--
-- Para quitar la pausa al recuperar la empresa con el enlace, la API necesita
-- saber de qué empresa es el enlace ANTES de usarlo. La tabla de enlaces no la
-- lee nadie (021), así que hace falta esta puerta estrecha: dado el hash,
-- devuelve solo el id de la empresa. Quien no tiene el enlace no tiene el hash.
--
-- No se borra nada.
-- =============================================================================

BEGIN;

CREATE OR REPLACE FUNCTION empresa_por_enlace(p_token_hash bytea)
RETURNS uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT r.empresa_id FROM enlaces_recuperacion r WHERE r.token_hash = p_token_hash;
$$;

GRANT EXECUTE ON FUNCTION empresa_por_enlace(bytea) TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('023_pausa_en_servicio');

COMMIT;