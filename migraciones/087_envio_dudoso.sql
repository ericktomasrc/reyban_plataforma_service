-- =============================================================================
-- 087 — ARREGLOS DE LA REVISIÓN QA DE EMISIÓN (8 nov 2026, e238)
--
-- documentos.envio_dudoso: el último envío al servicio falló SIN saber si
-- llegó (sin respuesta o error 5xx). Mientras sea así, el comprobante no se
-- puede cambiar: solo reintentar tal cual. Si ya se había emitido, el servicio
-- devuelve el mismo (misma clave) y la plataforma no guarda datos distintos a
-- los que tiene SUNAT.
--
-- El resto de esta entrega está en la API (no cambia la base):
--   · Las notas no se editan desde la pantalla de comprobantes, ni una factura
--     se vuelve nota (ni al revés); el atajo de «doble clic» de una nota solo
--     encuentra notas de ESE comprobante. Así nadie salta el tope ni el
--     bloqueo de pago emitiendo una factura por la ruta de las notas.
--   · Dos notas de crédito a la vez sobre el mismo comprobante: la segunda
--     espera a la primera y no se pasan del total.
--   · Una emisión ya pedida no se corta aunque se cierre el navegador.
--
-- Se puede correr dos veces. Nada se borra.
-- =============================================================================
BEGIN;

ALTER TABLE documentos ADD COLUMN IF NOT EXISTS envio_dudoso boolean NOT NULL DEFAULT false;
COMMENT ON COLUMN documentos.envio_dudoso IS
    '087: el último envío falló sin saber si llegó al servicio. Solo se reintenta tal cual; no se edita.';

INSERT INTO migraciones_aplicadas (nombre) VALUES ('087_envio_dudoso')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;