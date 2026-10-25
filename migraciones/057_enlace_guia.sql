-- =============================================================================
-- 057 — LA GUÍA POR WHATSAPP (e174, 25 oct 2026)
--
-- Como las facturas: un botón «WhatsApp» en la guía que abre el chat con el
-- mensaje y un enlace al PDF. Se elige a quién: al cliente (su número) o al
-- conductor (otro número).
--
-- El enlace lo da la plataforma (el portal del servicio solo enseña facturas,
-- boletas y notas):
--   /api/publico/guias/<enlace>   abre el PDF de ESA guía, sin entrar.
--
-- guia_enlaces guarda el HASH del enlace, nunca el enlace. Caduca (30 días) y
-- cuenta los usos. Solo inserción desde la aplicación: el uso lo anota la
-- función resolver_enlace_guia, que es la única que lo lee sin sesión.
--
-- Solo crea. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS guia_enlaces (
    id             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id     uuid        NOT NULL REFERENCES empresas (id),
    guia_id        uuid        NOT NULL REFERENCES guias (id),
    hash           bytea       NOT NULL,
    para           text        NOT NULL,
    numero         text        NOT NULL,
    expira_en      timestamptz NOT NULL,
    usos           integer     NOT NULL DEFAULT 0,
    ultimo_uso_en  timestamptz,
    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,

    CONSTRAINT ck_guia_enlaces_para CHECK (para IN ('cliente', 'conductor', 'otro'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_guia_enlaces_hash ON guia_enlaces (hash);
CREATE INDEX IF NOT EXISTS ix_guia_enlaces_guia ON guia_enlaces (guia_id);

COMMENT ON TABLE guia_enlaces IS '057: enlaces al PDF de una guía para mandarlos por WhatsApp. Se guarda el hash, nunca el enlace.';

ALTER TABLE guia_enlaces ENABLE ROW LEVEL SECURITY;
ALTER TABLE guia_enlaces FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS p_guia_enlaces ON guia_enlaces;
CREATE POLICY p_guia_enlaces ON guia_enlaces
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());

-- Bitácora al crear (sin el hash). Los usos los anota resolver_enlace_guia.
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_guia_enlaces') THEN
        CREATE TRIGGER auditar_guia_enlaces AFTER INSERT ON guia_enlaces
            FOR EACH ROW EXECUTE FUNCTION fn_auditar('hash');
    END IF;
END $$;

GRANT SELECT, INSERT ON guia_enlaces TO plataforma_app;

-- Sin sesión: de qué empresa y qué guía es un enlace vigente, y anota el uso.
-- Estrecha a propósito: recibe un hash y devuelve dos ids, o nada (no existe,
-- caducó o la guía ya no se puede enseñar: desde fuera se ve igual).
CREATE OR REPLACE FUNCTION resolver_enlace_guia(p_hash bytea)
RETURNS TABLE (empresa_id uuid, guia_id uuid)
LANGUAGE sql SECURITY DEFINER SET search_path = public AS $$
    UPDATE guia_enlaces e
       SET usos = e.usos + 1, ultimo_uso_en = now()
     WHERE e.hash = p_hash
       AND e.expira_en > now()
    RETURNING e.empresa_id, e.guia_id;
$$;

-- La dueña de las puertas (027): salta la seguridad por empresa solo aquí.
GRANT SELECT, UPDATE ON guia_enlaces TO plataforma_puertas;
ALTER FUNCTION resolver_enlace_guia(bytea) OWNER TO plataforma_puertas;
REVOKE ALL ON FUNCTION resolver_enlace_guia(bytea) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION resolver_enlace_guia(bytea) TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('057_enlace_guia')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '057: la guía ya se puede mandar por WhatsApp (al cliente o al conductor). Nada se borró.';
END $$;