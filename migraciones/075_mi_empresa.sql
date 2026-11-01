-- =============================================================================
-- 075 — MI EMPRESA (e212, 1 nov 2026)
--
-- La empresa de Reyban (RUC 20, o RUC 10 de persona natural con negocio) que
-- cobra los planes y emite la factura o boleta de cada pago. Se configura en
-- el menú «Mi empresa» del super administrador, aparte de «Empresas» (los
-- clientes).
--
--   · plataforma_ajustes.empresa_propia   cuál es (una sola).
--   · mi_empresa_fijar(empresa)            la marca como «Mi empresa» y como la
--                                          que emite los comprobantes de cobros.
--   · mi_empresa()                         lo que se enseña en el menú.
--   · cobros_fijar_comprobante(…)          las series y la descripción de los
--                                          comprobantes de cobros (las series
--                                          viven en el servicio de facturación:
--                                          aquí solo se guarda cuáles se usan).
--   · cobros_guardar_config                ya no toca la empresa que emite ni
--                                          sus series: eso es de «Mi empresa».
--
-- SEGURIDAD: el menú solo CONFIGURA (datos, certificado, SOL, series). No hay
-- forma de que el super administrador emita a mano: los comprobantes de cobros
-- los emite el sistema solo, cuando un cliente paga (parte 2 de Cobros).
--
-- Solo crea. Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

ALTER TABLE plataforma_ajustes ADD COLUMN IF NOT EXISTS empresa_propia uuid REFERENCES empresas(id);

-- Marcar cuál es «Mi empresa». La que emite los cobros pasa a ser esa.
CREATE OR REPLACE FUNCTION mi_empresa_fijar(p_empresa uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM empresas WHERE id = p_empresa AND activo) THEN
        RAISE EXCEPTION 'COBRO_DATOS: Esa empresa no existe.';
    END IF;
    UPDATE plataforma_ajustes SET empresa_propia = p_empresa WHERE id = 1;
    UPDATE cobros_ajustes
       SET emisora_empresa = p_empresa,
           serie_factura = CASE WHEN emisora_empresa IS DISTINCT FROM p_empresa THEN NULL ELSE serie_factura END,
           serie_boleta  = CASE WHEN emisora_empresa IS DISTINCT FROM p_empresa THEN NULL ELSE serie_boleta END
     WHERE id = 1;
END $$;

-- Lo que enseña el menú «Mi empresa» (los datos del servicio los pide la API).
CREATE OR REPLACE FUNCTION mi_empresa() RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE v_id uuid := (SELECT empresa_propia FROM plataforma_ajustes WHERE id = 1);
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF v_id IS NULL THEN RETURN jsonb_build_object('empresa', NULL); END IF;
    RETURN jsonb_build_object(
        'empresa', (SELECT jsonb_build_object('id', e.id, 'nombre', e.nombre, 'ruc', e.ruc, 'razonSocial', e.razon_social,
                        'nombreComercial', e.nombre_comercial, 'ambiente', e.ambiente, 'creadaEn', e.creado_en,
                        'tipo', CASE WHEN e.ruc LIKE '20%' THEN 'ruc20' ELSE 'ruc10' END)
                      FROM empresas e WHERE e.id = v_id),
        'credencial', (SELECT jsonb_build_object('pista', c.pista, 'altaConfirmadaEn', c.alta_confirmada_en,
                           'certificadoValidoHasta', c.certificado_valido_hasta)
                         FROM credenciales_facturacion c WHERE c.empresa_id = v_id AND c.activo LIMIT 1),
        'cobros', (SELECT jsonb_build_object('serieFactura', k.serie_factura, 'serieBoleta', k.serie_boleta,
                       'enviarCorreo', k.enviar_correo, 'descripcion', k.descripcion)
                     FROM cobros_ajustes k WHERE k.id = 1));
END $$;

-- Las series y la descripción de los comprobantes de cobros.
CREATE OR REPLACE FUNCTION cobros_fijar_comprobante(p_factura text, p_boleta text, p_enviar boolean, p_descripcion text)
RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_sf text := NULLIF(upper(btrim(COALESCE(p_factura, ''))), '');
    v_sb text := NULLIF(upper(btrim(COALESCE(p_boleta, ''))), '');
    v_d  text := btrim(COALESCE(p_descripcion, ''));
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF (SELECT empresa_propia FROM plataforma_ajustes WHERE id = 1) IS NULL THEN
        RAISE EXCEPTION 'COBRO_CONFIG: Primero registra tu empresa en «Mi empresa».';
    END IF;
    IF v_sf IS NOT NULL AND v_sf !~ '^F[A-Z0-9]{3}$' THEN
        RAISE EXCEPTION 'COBRO_CONFIG: La serie de factura empieza con F y tiene 4 letras o números (F001).';
    END IF;
    IF v_sb IS NOT NULL AND v_sb !~ '^B[A-Z0-9]{3}$' THEN
        RAISE EXCEPTION 'COBRO_CONFIG: La serie de boleta empieza con B y tiene 4 letras o números (B001).';
    END IF;
    IF length(v_d) NOT BETWEEN 5 AND 120 THEN
        RAISE EXCEPTION 'COBRO_CONFIG: La descripción del comprobante va de 5 a 120 letras.';
    END IF;
    UPDATE cobros_ajustes
       SET emisora_empresa = (SELECT empresa_propia FROM plataforma_ajustes WHERE id = 1),
           serie_factura = v_sf, serie_boleta = v_sb,
           enviar_correo = COALESCE(p_enviar, true), descripcion = v_d
     WHERE id = 1;
END $$;

-- El panel de pagos ya no toca la empresa que emite, sus series, el correo ni
-- la descripción: todo eso es de «Mi empresa».
CREATE OR REPLACE FUNCTION cobros_guardar_config(p jsonb, p_produccion boolean) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_modo  text := p->>'modo';
    v_reint integer[];
    v_gr    integer := (p->>'graciaDias')::int;
    v_av    integer := (p->>'avisoDias')::int;
    v_banco text := NULLIF(btrim(COALESCE(p->>'transferenciaDatos', '')), '');
    v_corr  text := NULLIF(lower(btrim(COALESCE(p->>'correoPagos', ''))), '');
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF v_modo NOT IN ('apagado', 'simulado', 'culqi') THEN
        RAISE EXCEPTION 'COBRO_CONFIG: Elige el modo: apagado, simulado o Culqi.';
    END IF;
    IF v_modo = 'simulado' AND p_produccion THEN
        RAISE EXCEPTION 'COBRO_CONFIG: En producción no se puede usar el modo simulado: nadie puede «pagar de mentira».';
    END IF;
    SELECT array_agg(x::int ORDER BY x::int) INTO v_reint
      FROM (SELECT DISTINCT jsonb_array_elements_text(COALESCE(p->'reintentoDias', '[]'::jsonb)) x) q;
    v_reint := COALESCE(v_reint, ARRAY[]::integer[]);
    IF cardinality(v_reint) > 5 OR EXISTS (SELECT 1 FROM unnest(v_reint) d WHERE d < 1 OR d > 30) THEN
        RAISE EXCEPTION 'COBRO_CONFIG: Hasta 5 reintentos, cada uno entre el día 1 y el 30.';
    END IF;
    IF v_gr IS NULL OR v_gr NOT BETWEEN 0 AND 30 THEN
        RAISE EXCEPTION 'COBRO_CONFIG: Los días de gracia van de 0 a 30.';
    END IF;
    IF v_av IS NULL OR v_av NOT BETWEEN 0 AND 15 THEN
        RAISE EXCEPTION 'COBRO_CONFIG: El aviso antes del cobro va de 0 a 15 días.';
    END IF;
    IF COALESCE(p->>'alVencer', '') NOT IN ('no_emite', 'solo_aviso') THEN
        RAISE EXCEPTION 'COBRO_CONFIG: Elige qué pasa al terminar la gracia.';
    END IF;
    IF COALESCE((p->>'aceptaTransferencia')::boolean, false) AND v_banco IS NULL THEN
        RAISE EXCEPTION 'COBRO_CONFIG: Para aceptar transferencias escribe los datos de tu cuenta bancaria.';
    END IF;
    IF v_corr IS NOT NULL AND v_corr !~ '^[^@\s]+@[^@\s]+\.[^@\s]+$' THEN
        RAISE EXCEPTION 'COBRO_CONFIG: El correo de pagos no es válido.';
    END IF;

    UPDATE cobros_ajustes SET
        modo = v_modo, reintento_dias = v_reint, gracia_dias = v_gr, aviso_dias = v_av, al_vencer = p->>'alVencer',
        acepta_tarjeta = COALESCE((p->>'aceptaTarjeta')::boolean, true),
        acepta_transferencia = COALESCE((p->>'aceptaTransferencia')::boolean, false),
        acepta_yape = COALESCE((p->>'aceptaYape')::boolean, false),
        transferencia_datos = left(v_banco, 600), correo_pagos = v_corr
     WHERE id = 1;
END $$;

DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY['mi_empresa_fijar(uuid)', 'mi_empresa()',
                             'cobros_fijar_comprobante(text, text, boolean, text)',
                             'cobros_guardar_config(jsonb, boolean)']
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
    END LOOP;
END $$;

INSERT INTO permisos (codigo, area, descripcion) VALUES
    ('plataforma.mi_empresa', 'plataforma', 'Mi empresa: los datos, el certificado, el usuario SOL y las series de la empresa de Reyban que cobra los planes')
ON CONFLICT (codigo) DO NOTHING;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('075_mi_empresa')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '075: Mi empresa (la de Reyban, que cobra los planes y emite sus comprobantes). Nada se borró.';
END $$;