-- =============================================================================
-- 051 — EVENTOS DE UNA GUÍA (e152, 24 oct 2026)
--
-- La GRE POR EVENTO (no se pudo llegar al destino, o hubo que pasar la carga a
-- otro vehículo en el camino) SOLO SE EMITE EN SOL: SUNAT no la recibe desde
-- sistemas propios (SEE - Del contribuyente solo admite remitente y
-- transportista). Como con la anulación, aquí se REGISTRA la que se hizo en
-- SOL, para que la guía cuente lo que pasó y el transporte nuevo se guarde.
--
--   · guia_eventos   una fila por evento registrado: el tipo, el número de la
--                    GRE por evento de SOL, dónde se reinició el traslado,
--                    la nueva llegada (si cambió), la placa y el conductor.
--
-- Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS guia_eventos (
    id                  uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id          uuid        NOT NULL REFERENCES empresas (id),
    guia_id             uuid        NOT NULL REFERENCES guias (id),

    -- arribo      imposibilidad de llegar o de entregar en el punto de llegada
    -- transbordo  la carga pasa a otro vehículo por causas no imputables
    -- ambos       las dos cosas
    tipo                text        NOT NULL,
    numero_sol          text        NOT NULL,
    fecha               date        NOT NULL,

    reinicio_direccion  text        NOT NULL,
    reinicio_ubigeo     text        NOT NULL,
    llegada_direccion   text,
    llegada_ubigeo      text,
    placa               text,
    conductor_doc       text,
    conductor_nombre    text,
    licencia            text,
    observacion         text,

    creado_en           timestamptz NOT NULL DEFAULT now(),
    creado_por          uuid,
    modificado_en       timestamptz,
    modificado_por      uuid,
    activo              boolean     NOT NULL DEFAULT true,
    version             integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_guia_eventos_tipo     CHECK (tipo IN ('arribo', 'transbordo', 'ambos')),
    CONSTRAINT ck_guia_eventos_numero   CHECK (numero_sol ~ '^[A-Z0-9]{1,4}-[0-9]{1,8}$'),
    CONSTRAINT ck_guia_eventos_reinicio CHECK (reinicio_ubigeo ~ '^[0-9]{6}$' AND length(btrim(reinicio_direccion)) BETWEEN 5 AND 200),
    CONSTRAINT ck_guia_eventos_llegada  CHECK (llegada_ubigeo IS NULL OR llegada_ubigeo ~ '^[0-9]{6}$'),
    CONSTRAINT ck_guia_eventos_placa    CHECK (placa IS NULL OR placa ~ '^[A-Z0-9]{6,8}$')
);

CREATE INDEX IF NOT EXISTS ix_guia_eventos_guia ON guia_eventos (guia_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_guia_eventos_numero ON guia_eventos (empresa_id, numero_sol);

COMMENT ON TABLE guia_eventos IS '051: GRE por evento hechas en SOL y registradas aquí (no se emiten por API).';

ALTER TABLE guia_eventos ENABLE ROW LEVEL SECURITY;
ALTER TABLE guia_eventos FORCE ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'p_guia_eventos') THEN
        CREATE POLICY p_guia_eventos ON guia_eventos
            USING (ctx_es_super() OR empresa_id = ctx_empresa())
            WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'guia_eventos'::regclass AND NOT tgisinternal) THEN
        PERFORM vigilar_tabla('guia_eventos');
    END IF;
END $$;

GRANT SELECT, INSERT, UPDATE ON guia_eventos TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('051_guia_eventos')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;