-- =============================================================================
-- 058 — LA CAMPANITA: AVISOS LEÍDOS (e177, 25 oct 2026)
--
-- La campanita enseña lo que pide atención AHORA (certificado por vencer,
-- comprobantes rechazados o sin enviar, bajas sin confirmar, guías
-- rechazadas, stock bajo o en negativo, traslados por recibir, caja sin
-- cerrar, personas nuevas, la ficha incompleta). Esos avisos NO se guardan:
-- la API los calcula al momento, así nunca dicen algo que ya no es verdad.
--
-- Lo único que se guarda es QUÉ VIO CADA PERSONA: cada aviso tiene una clave
-- que cambia cuando cambia la situación (otro rechazo, el certificado más
-- cerca), y así vuelve a contar como «sin leer».
--
-- Los correos del certificado ya salen desde la 025 (30, 15, 7, 1, 0 y -1 días).
--
-- Solo crea. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS avisos_leidos (
    id          uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    usuario_id  uuid        NOT NULL REFERENCES usuarios (id),
    clave       text        NOT NULL,
    leido_en    timestamptz NOT NULL DEFAULT now(),
    creado_en   timestamptz NOT NULL DEFAULT now(),
    creado_por  uuid,

    CONSTRAINT ck_avisos_leidos_clave CHECK (length(clave) BETWEEN 3 AND 200)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_avisos_leidos ON avisos_leidos (usuario_id, clave);

COMMENT ON TABLE avisos_leidos IS '058: qué avisos de la campanita ya vio cada persona (la clave cambia cuando cambia la situación).';

ALTER TABLE avisos_leidos ENABLE ROW LEVEL SECURITY;
ALTER TABLE avisos_leidos FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS p_avisos_leidos ON avisos_leidos;
CREATE POLICY p_avisos_leidos ON avisos_leidos
    USING (ctx_es_super() OR usuario_id = ctx_usuario())
    WITH CHECK (ctx_es_super() OR usuario_id = ctx_usuario());

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_avisos_leidos') THEN
        PERFORM vigilar_tabla_inmutable('avisos_leidos');
    END IF;
END $$;

GRANT SELECT, INSERT ON avisos_leidos TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('058_avisos')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '058: la campanita de avisos (leídos por persona). Nada se borró.';
END $$;