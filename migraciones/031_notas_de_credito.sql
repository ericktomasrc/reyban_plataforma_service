-- =============================================================================
-- 031 — NOTAS DE CRÉDITO DESDE LA PLATAFORMA (18 oct 2026)
--
-- Diseño: maqueta «Nota de crédito», aprobada el 18 oct.
--
--   · Permiso propio «facturacion.notas_credito»: se marca por rol. El
--     Administrador lo recibe; el Facturador (la cajera) NO: puede facturar
--     sin poder hacer notas. Cada empresa puede dárselo a otro rol.
--   · Índice para encontrar rápido las notas de un comprobante (el saldo).
--
-- La tabla documentos ya tenía documento_ref_id, motivo_nota y tipo_nota desde
-- la 005, con la regla de que una nota (07/08) siempre dice a qué documento
-- afecta. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

INSERT INTO permisos (codigo, area, descripcion)
VALUES ('facturacion.notas_credito', 'facturacion', 'Emitir notas de crédito')
ON CONFLICT (codigo) DO NOTHING;

-- Solo al Administrador de sistema (el de todas las empresas).
INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT '11111111-1111-1111-1111-111111111111', 'facturacion.notas_credito'
 WHERE NOT EXISTS (
       SELECT 1 FROM rol_permiso
        WHERE rol_id = '11111111-1111-1111-1111-111111111111'
          AND permiso_codigo = 'facturacion.notas_credito');

CREATE INDEX IF NOT EXISTS ix_documentos_ref
    ON documentos (documento_ref_id)
 WHERE documento_ref_id IS NOT NULL;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('031_notas_de_credito')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$
DECLARE v_n int;
BEGIN
    SELECT count(*) INTO v_n FROM rol_permiso
     WHERE permiso_codigo = 'facturacion.notas_credito';
    RAISE NOTICE '031: permiso «Emitir notas de crédito» listo (% rol(es) lo tienen). Nada se borró.', v_n;
END $$;