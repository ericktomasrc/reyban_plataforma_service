-- =============================================================================
-- 043 — WHATSAPP DEL CLIENTE Y ENVÍO DE COMPROBANTES POR WHATSAPP (22 oct 2026)
--
-- EN EL CLIENTE:
--   · whatsapp         el número, solo dígitos con el código de país
--                      (51987654321). Opcional: aparte del teléfono.
--   · whatsapp_activo  «enviarle sus comprobantes por WhatsApp». La regla
--                      general del cliente; sin número no se puede activar.
--
-- EN EL COMPROBANTE:
--   · whatsapp_enviar  se decide al hacerlo (viene marcado si el cliente lo
--                      tiene activo; se puede cambiar solo para ese).
--   · whatsapp_estado  pendiente (falta pulsar Enviar en WhatsApp) | enviado
--                      | sin_whatsapp (ese número no tiene WhatsApp) |
--                      no_enviado (se decidió no mandarlo).
--   · whatsapp_numero, whatsapp_en: a qué número y cuándo.
--
-- EL ENVÍO ES SEMIAUTOMÁTICO: la plataforma abre WhatsApp con el número y el
-- mensaje (un enlace al portal) y la persona pulsa Enviar. Con la API de
-- WhatsApp (Meta) se podrá hacer sin ese paso; estas columnas sirven igual.
--
-- La nota de débito (misma entrega) no necesita columnas: documentos ya
-- admitía el tipo 08 con su comprobante de referencia (005).
--
-- Solo añade columnas. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

ALTER TABLE clientes ADD COLUMN IF NOT EXISTS whatsapp        text;
ALTER TABLE clientes ADD COLUMN IF NOT EXISTS whatsapp_activo boolean NOT NULL DEFAULT false;

ALTER TABLE documentos ADD COLUMN IF NOT EXISTS whatsapp_enviar boolean NOT NULL DEFAULT false;
ALTER TABLE documentos ADD COLUMN IF NOT EXISTS whatsapp_estado text;
ALTER TABLE documentos ADD COLUMN IF NOT EXISTS whatsapp_numero text;
ALTER TABLE documentos ADD COLUMN IF NOT EXISTS whatsapp_en     timestamptz;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_clientes_whatsapp') THEN
        ALTER TABLE clientes ADD CONSTRAINT ck_clientes_whatsapp CHECK (
            (whatsapp IS NULL OR whatsapp ~ '^[1-9][0-9]{7,14}$')
            AND (NOT whatsapp_activo OR whatsapp IS NOT NULL));
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_documentos_whatsapp') THEN
        ALTER TABLE documentos ADD CONSTRAINT ck_documentos_whatsapp CHECK (
            (whatsapp_estado IS NULL OR whatsapp_estado IN ('pendiente', 'enviado', 'sin_whatsapp', 'no_enviado'))
            AND (whatsapp_numero IS NULL OR whatsapp_numero ~ '^[1-9][0-9]{7,14}$'));
    END IF;
END $$;

COMMENT ON COLUMN clientes.whatsapp IS
    '043: número de WhatsApp, solo dígitos con el código de país (51987654321). Opcional.';
COMMENT ON COLUMN clientes.whatsapp_activo IS
    '043: enviarle sus comprobantes por WhatsApp (regla general). Sin número, no.';
COMMENT ON COLUMN documentos.whatsapp_enviar IS
    '043: este comprobante se manda por WhatsApp al emitirlo (se decide al hacerlo).';
COMMENT ON COLUMN documentos.whatsapp_estado IS
    '043: pendiente | enviado | sin_whatsapp | no_enviado. NULL: no se pidió.';

INSERT INTO migraciones_aplicadas (nombre) VALUES ('043_whatsapp')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN RAISE NOTICE '043: WhatsApp en clientes y comprobantes. Nada se borró.'; END $$;