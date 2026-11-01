-- =============================================================================
-- 074 — LA PÁGINA PÚBLICA (e210, 1 nov 2026)
--
-- Las páginas que se ven SIN iniciar sesión (facturacion.reybanconsulting.com):
-- inicio, precios, términos, privacidad, devoluciones y contacto. Las pide
-- Culqi para aprobar la cuenta y Google para publicar «Entrar con Google».
--
--   · plataforma_ajustes  + los datos de la empresa que se muestran (razón
--                           social, RUC, dirección, correo, WhatsApp) y la
--                           fecha de «última actualización» de los textos.
--   · publico_vitrina()    lo que necesita la página pública: los precios
--                           (los mismos de «Precios y reglas») y esos datos.
--                           Sin sesión: no devuelve nada de ninguna cuenta.
--
-- Solo crea. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS publica_razon_social text NOT NULL DEFAULT 'Reyban Consulting SAC';
ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS publica_ruc          text;
ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS publica_direccion    text NOT NULL DEFAULT 'Lima, Perú';
ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS publica_correo       text NOT NULL DEFAULT 'reybanconsulting@gmail.com';
ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS publica_whatsapp     text;
ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS publica_actualizado  date NOT NULL DEFAULT DATE '2026-11-01';

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ajustes_publica') THEN
        ALTER TABLE plataforma_ajustes ADD CONSTRAINT ck_ajustes_publica CHECK (
            length(btrim(publica_razon_social)) BETWEEN 2 AND 200
            AND (publica_ruc IS NULL OR ruc_valido(publica_ruc))
            AND length(btrim(publica_direccion)) BETWEEN 2 AND 300
            AND publica_correo ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$'
            AND (publica_whatsapp IS NULL OR publica_whatsapp ~ '^\+?[0-9 ]{6,20}$'));
    END IF;
END $$;

-- Lo que ve la página pública. Sin sesión, a propósito: es la vitrina.
CREATE OR REPLACE FUNCTION publico_vitrina() RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT jsonb_build_object(
        'catalogo', planes_catalogo(),
        'empresa', (SELECT jsonb_build_object(
                        'razonSocial', a.publica_razon_social, 'ruc', a.publica_ruc,
                        'direccion', a.publica_direccion, 'correo', a.publica_correo,
                        'whatsapp', a.publica_whatsapp, 'actualizado', a.publica_actualizado)
                      FROM plataforma_ajustes a WHERE a.id = 1),
        'medios', (SELECT jsonb_build_object('tarjeta', k.acepta_tarjeta, 'transferencia', k.acepta_transferencia,
                                             'yape', k.acepta_yape)
                     FROM cobros_ajustes k WHERE k.id = 1));
$$;
ALTER FUNCTION publico_vitrina() OWNER TO plataforma_puertas;
REVOKE ALL ON FUNCTION publico_vitrina() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION publico_vitrina() TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('074_pagina_publica')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '074: página pública (precios, términos, privacidad, devoluciones y contacto). Nada se borró.';
END $$;