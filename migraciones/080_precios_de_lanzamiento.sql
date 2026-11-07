-- =============================================================================
-- 080 — LOS PRECIOS DE LANZAMIENTO ACORDADOS (7 nov 2026, e220)
--
-- Para no tener que escribirlos a mano en cada servidor (tu PC, QA, producción):
--
--   Cuenta Negocio (al mes, con IGV)    lanzamiento   real (tachado)
--     Básico                              S/ 25         S/ 45
--     Pro                                 S/ 39         S/ 69
--     Max                                 S/ 55         S/ 89
--
--   Cartera (por empresa, al mes)       lanzamiento   real
--     1 a 5                               S/ 15         S/ 35
--     6 a 10                              S/ 15         S/ 32
--     11 a 20                             S/ 15         S/ 29
--     21 a 50                             S/ 15         S/ 26
--     51 o más                            S/ 15         S/ 22
--
--   Oferta encendida hasta el 31/03/2027; quien entra la conserva 12 meses pagando.
--
-- Corre UNA vez (migraciones_aplicadas). Después, los precios se cambian en
-- Planes y cobros › Precios y reglas, como siempre. Los tramos que había y no
-- están aquí quedan inactivos (nada se borra).
-- =============================================================================
BEGIN;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM migraciones_aplicadas WHERE nombre = '080_precios_de_lanzamiento') THEN
        RAISE NOTICE '080: ya se aplicó; no se tocan los precios (se cambian en Precios y reglas).';
        RETURN;
    END IF;

    UPDATE planes SET precio_oferta = 25, precio_regular = 45 WHERE codigo = 'basico';
    UPDATE planes SET precio_oferta = 39, precio_regular = 69 WHERE codigo = 'pro';
    UPDATE planes SET precio_oferta = 55, precio_regular = 89 WHERE codigo = 'max';

    UPDATE cartera_tramos SET activo = false WHERE activo AND desde NOT IN (1, 6, 11, 21, 51);
    INSERT INTO cartera_tramos (desde, hasta, precio_oferta, precio_regular) VALUES
        (1, 5, 15, 35), (6, 10, 15, 32), (11, 20, 15, 29), (21, 50, 15, 26), (51, NULL, 15, 22)
    ON CONFLICT (desde) DO UPDATE
        SET hasta = EXCLUDED.hasta, precio_oferta = EXCLUDED.precio_oferta,
            precio_regular = EXCLUDED.precio_regular, activo = true;

    UPDATE plataforma_ajustes
       SET oferta_activa = true, oferta_hasta = DATE '2027-03-31', lanzamiento_meses = 12
     WHERE id = 1;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('080_precios_de_lanzamiento')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '080: precios de lanzamiento (Básico 25/45, Cartera 15 por empresa, real 35 a 22). Nada se borró.';
END $$;