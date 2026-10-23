-- =============================================================================
-- 045 — EL PADRÓN DE LOCALES ANEXOS DE SUNAT (23 oct 2026, para Sucursales)
--
-- El segundo archivo público de SUNAT, padron_reducido_local_anexo.zip: por
-- cada RUC, la dirección de cada uno de sus locales anexos (tiendas,
-- almacenes, oficinas). Con él, «Nueva sucursal» ofrece los locales que la
-- empresa tiene declarados en su ficha RUC y llena la dirección sola.
--
-- NO TRAE EL CÓDIGO DE ESTABLECIMIENTO (0001, 0002…): ese lo escribe quien
-- crea la sucursal, de su ficha RUC. Tampoco la razón social.
--
-- Funciona IGUAL que el padrón de RUC (042), y comparte su vigilancia:
--
--   · dos tablas que se turnan (referencia.anexos_a / _b), UNLOGGED, fuera de
--     la bitácora: se carga la que no se usa y solo si salió entera pasa a
--     usarse. Una carga que falla no toca la que está en uso.
--   · cada carga queda en padron_cargas, ahora con la columna `padron`
--     ('ruc' o 'anexos'), con el motivo en palabras si falló.
--   · se baja sola cada madrugada, después del de RUC.
--
-- Lo que cambia de lo de antes: las funciones del padrón de RUC miran solo
-- sus cargas (una carga de anexos no le bloquea ni le cuenta intentos), y
-- padron_estado() trae además el bloque «anexos».
--
-- No se borra nada de nadie. Se puede correr dos veces.
-- =============================================================================

BEGIN;

-- --- Las dos tablas ------------------------------------------------------------

CREATE UNLOGGED TABLE IF NOT EXISTS referencia.anexos_a (
    ruc       text NOT NULL,
    ubigeo    text,
    direccion text
);
CREATE UNLOGGED TABLE IF NOT EXISTS referencia.anexos_b (LIKE referencia.anexos_a);

ALTER TABLE referencia.anexos_a OWNER TO plataforma_puertas;
ALTER TABLE referencia.anexos_b OWNER TO plataforma_puertas;

-- La aplicación solo ESCRIBE (la carga, con COPY). Leer, por anexos_consultar.
REVOKE ALL ON referencia.anexos_a, referencia.anexos_b FROM plataforma_app;
GRANT INSERT ON referencia.anexos_a, referencia.anexos_b TO plataforma_app;

-- --- Qué padrón es cada carga, y cuál de anexos está en uso ---------------------

ALTER TABLE padron_cargas ADD COLUMN IF NOT EXISTS padron text NOT NULL DEFAULT 'ruc';
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_padron_cargas_padron') THEN
        ALTER TABLE padron_cargas ADD CONSTRAINT ck_padron_cargas_padron CHECK (padron IN ('ruc', 'anexos'));
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS ix_padron_cargas_padron ON padron_cargas (padron, iniciada_en DESC);

ALTER TABLE consultas_config ADD COLUMN IF NOT EXISTS anexos_tabla    text;
ALTER TABLE consultas_config ADD COLUMN IF NOT EXISTS anexos_carga_id uuid;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_consultas_config_anexos') THEN
        ALTER TABLE consultas_config ADD CONSTRAINT ck_consultas_config_anexos
            CHECK (anexos_tabla IS NULL OR anexos_tabla IN ('anexos_a', 'anexos_b'));
    END IF;
END $$;


-- =============================================================================
-- EL PADRÓN DE RUC: ahora solo mira sus propias cargas
-- =============================================================================

CREATE OR REPLACE FUNCTION padron_preparar_carga(p_origen text, p_usuario uuid, p_archivo text, p_bytes bigint)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_activa text;
    v_tabla  text;
    v_id     uuid;
BEGIN
    UPDATE padron_cargas
       SET estado = 'error', terminada_en = now(),
           motivo = 'Se interrumpió a la mitad (la plataforma se reinició o se cortó). No se cambió el padrón en uso.'
     WHERE padron = 'ruc' AND estado = 'en_curso' AND iniciada_en < now() - interval '4 hours';

    IF EXISTS (SELECT 1 FROM padron_cargas WHERE padron = 'ruc' AND estado = 'en_curso') THEN
        RETURN json_build_object('error', 'en_curso')::text;
    END IF;

    SELECT padron_tabla INTO v_activa FROM consultas_config WHERE id = 1 FOR UPDATE;
    v_tabla := CASE WHEN v_activa = 'padron_ruc_a' THEN 'padron_ruc_b' ELSE 'padron_ruc_a' END;

    EXECUTE format('TRUNCATE referencia.%I', v_tabla);
    EXECUTE format('DROP INDEX IF EXISTS referencia.%I', 'ix_' || v_tabla || '_ruc');

    INSERT INTO padron_cargas (padron, origen, tabla, archivo, bytes, usuario_id)
    VALUES ('ruc', p_origen, v_tabla, p_archivo, p_bytes, p_usuario)
    RETURNING id INTO v_id;

    RETURN json_build_object('id', v_id, 'tabla', v_tabla)::text;
END $$;
ALTER FUNCTION padron_preparar_carga(text, uuid, text, bigint) OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION padron_preparar_carga(text, uuid, text, bigint) TO plataforma_app;


-- Anotar un fallo de antes de empezar, diciendo de qué padrón.
CREATE OR REPLACE FUNCTION padron_anotar_fallo(p_origen text, p_usuario uuid, p_archivo text, p_motivo text, p_padron text)
RETURNS void
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
    INSERT INTO padron_cargas (padron, origen, estado, tabla, terminada_en, archivo, motivo, usuario_id)
    VALUES (CASE WHEN p_padron = 'anexos' THEN 'anexos' ELSE 'ruc' END,
            p_origen, 'error', '-', now(), p_archivo, left(coalesce(p_motivo, 'Error sin detalle.'), 1000), p_usuario);
$$;
ALTER FUNCTION padron_anotar_fallo(text, uuid, text, text, text) OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION padron_anotar_fallo(text, uuid, text, text, text) TO plataforma_app;


-- Las automáticas de hoy de UN padrón.
CREATE OR REPLACE FUNCTION padron_automaticas_hoy(p_padron text)
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT json_build_object(
               'intentos', count(*),
               'ok', coalesce(bool_or(estado = 'ok'), false),
               'enCurso', coalesce(bool_or(estado = 'en_curso'), false),
               'ultimaEn', max(iniciada_en))::text
      FROM padron_cargas
     WHERE origen = 'automatica'
       AND padron = p_padron
       AND (iniciada_en AT TIME ZONE 'America/Lima')::date = (now() AT TIME ZONE 'America/Lima')::date;
$$;
ALTER FUNCTION padron_automaticas_hoy(text) OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION padron_automaticas_hoy(text) TO plataforma_app;

-- La de antes (sin argumento) sigue siendo la del padrón de RUC.
CREATE OR REPLACE FUNCTION padron_automaticas_hoy()
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT padron_automaticas_hoy('ruc');
$$;
ALTER FUNCTION padron_automaticas_hoy() OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION padron_automaticas_hoy() TO plataforma_app;


-- =============================================================================
-- EL PADRÓN DE ANEXOS: preparar y terminar (fallar: padron_fallo_carga, que
-- ya sirve para los dos porque trabaja por id)
-- =============================================================================

CREATE OR REPLACE FUNCTION anexos_preparar_carga(p_origen text, p_usuario uuid, p_archivo text, p_bytes bigint)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_activa text;
    v_tabla  text;
    v_id     uuid;
BEGIN
    UPDATE padron_cargas
       SET estado = 'error', terminada_en = now(),
           motivo = 'Se interrumpió a la mitad (la plataforma se reinició o se cortó). No se cambió el padrón en uso.'
     WHERE padron = 'anexos' AND estado = 'en_curso' AND iniciada_en < now() - interval '4 hours';

    IF EXISTS (SELECT 1 FROM padron_cargas WHERE padron = 'anexos' AND estado = 'en_curso') THEN
        RETURN json_build_object('error', 'en_curso')::text;
    END IF;

    SELECT anexos_tabla INTO v_activa FROM consultas_config WHERE id = 1 FOR UPDATE;
    v_tabla := CASE WHEN v_activa = 'anexos_a' THEN 'anexos_b' ELSE 'anexos_a' END;

    EXECUTE format('TRUNCATE referencia.%I', v_tabla);
    EXECUTE format('DROP INDEX IF EXISTS referencia.%I', 'ix_' || v_tabla || '_ruc');

    INSERT INTO padron_cargas (padron, origen, tabla, archivo, bytes, usuario_id)
    VALUES ('anexos', p_origen, v_tabla, p_archivo, p_bytes, p_usuario)
    RETURNING id INTO v_id;

    RETURN json_build_object('id', v_id, 'tabla', v_tabla)::text;
END $$;
ALTER FUNCTION anexos_preparar_carga(text, uuid, text, bigint) OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION anexos_preparar_carga(text, uuid, text, bigint) TO plataforma_app;


CREATE OR REPLACE FUNCTION anexos_terminar_carga(p_id uuid, p_descartadas bigint, p_minimo bigint)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_c      padron_cargas%ROWTYPE;
    v_filas  bigint;
    v_motivo text;
BEGIN
    SELECT * INTO v_c FROM padron_cargas WHERE id = p_id FOR UPDATE;
    IF NOT FOUND OR v_c.estado <> 'en_curso' OR v_c.padron <> 'anexos' THEN
        RETURN 'Esa carga ya no está en curso.';
    END IF;

    EXECUTE format('SELECT count(*) FROM referencia.%I', v_c.tabla) INTO v_filas;

    IF v_filas < p_minimo THEN
        v_motivo := format('El archivo trae solo %s locales y se esperan al menos %s: parece incompleto o no es el '
                           'padrón de locales anexos. No se cambió el padrón en uso.', v_filas, p_minimo);
        UPDATE padron_cargas SET estado = 'error', terminada_en = now(), filas = v_filas,
               descartadas = p_descartadas, motivo = v_motivo WHERE id = p_id;
        EXECUTE format('TRUNCATE referencia.%I', v_c.tabla);
        RETURN v_motivo;
    END IF;

    EXECUTE format('CREATE INDEX %I ON referencia.%I (ruc)', 'ix_' || v_c.tabla || '_ruc', v_c.tabla);
    EXECUTE format('ANALYZE referencia.%I', v_c.tabla);

    UPDATE consultas_config SET anexos_tabla = v_c.tabla, anexos_carga_id = p_id WHERE id = 1;
    UPDATE padron_cargas SET estado = 'ok', terminada_en = now(), filas = v_filas,
           descartadas = p_descartadas, motivo = NULL WHERE id = p_id;
    RETURN 'ok';
END $$;
ALTER FUNCTION anexos_terminar_carga(uuid, bigint, bigint) OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION anexos_terminar_carga(uuid, bigint, bigint) TO plataforma_app;


-- Los locales anexos de un RUC, del padrón en uso:
--   {"cargado": true|false, "locales": [{direccion, ubigeo, distrito, provincia, departamento}]}
-- «cargado» false = todavía no hay padrón de anexos (o quedó vacío tras una caída).
CREATE OR REPLACE FUNCTION anexos_consultar(p_ruc text)
RETURNS text
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_tabla   text;
    v_locales json;
    v_hay     boolean;
BEGIN
    IF p_ruc !~ '^[0-9]{11}$' THEN
        RETURN json_build_object('cargado', false, 'locales', '[]'::json)::text;
    END IF;

    SELECT anexos_tabla INTO v_tabla FROM consultas_config WHERE id = 1;
    IF v_tabla IS NULL THEN
        RETURN json_build_object('cargado', false, 'locales', '[]'::json)::text;
    END IF;

    EXECUTE format($q$
        SELECT coalesce(json_agg(json_build_object(
                   'direccion', x.direccion, 'ubigeo', x.ubigeo, 'distrito', u.distrito,
                   'provincia', u.provincia, 'departamento', u.departamento)
                 ORDER BY x.direccion), '[]'::json)
          FROM (SELECT DISTINCT a.direccion, a.ubigeo FROM referencia.%I a WHERE a.ruc = $1 LIMIT 200) x
          LEFT JOIN referencia.ubigeos u ON u.ubigeo = x.ubigeo $q$, v_tabla)
    INTO v_locales USING p_ruc;

    EXECUTE format('SELECT EXISTS (SELECT 1 FROM referencia.%I)', v_tabla) INTO v_hay;

    RETURN json_build_object('cargado', v_hay, 'locales', v_locales)::text;
END $$;
ALTER FUNCTION anexos_consultar(text) OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION anexos_consultar(text) TO plataforma_app;


-- El estado para las pantallas: lo de siempre (padrón de RUC) y el bloque «anexos».
CREATE OR REPLACE FUNCTION padron_estado()
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT json_build_object(
        'modo', cfg.modo,
        'enUso', (SELECT json_build_object('filas', c.filas, 'cargadoEn', c.terminada_en,
                                          'origen', c.origen, 'archivo', c.archivo)
                    FROM padron_cargas c WHERE c.id = cfg.padron_carga_id),
        'enCurso', EXISTS (SELECT 1 FROM padron_cargas WHERE padron = 'ruc' AND estado = 'en_curso'
                            AND iniciada_en > now() - interval '4 hours'),
        'cargas', coalesce((
            SELECT json_agg(json_build_object(
                       'id', x.id, 'origen', x.origen, 'estado', x.estado,
                       'iniciadaEn', x.iniciada_en, 'terminadaEn', x.terminada_en,
                       'archivo', x.archivo, 'bytes', x.bytes, 'filas', x.filas,
                       'descartadas', x.descartadas, 'motivo', x.motivo,
                       'usuario', (SELECT u.nombre FROM usuarios u WHERE u.id = x.usuario_id))
                     ORDER BY x.iniciada_en DESC)
              FROM (SELECT * FROM padron_cargas WHERE padron = 'ruc' ORDER BY iniciada_en DESC LIMIT 15) x), '[]'::json),
        'anexos', json_build_object(
            'enUso', (SELECT json_build_object('filas', c.filas, 'cargadoEn', c.terminada_en,
                                              'origen', c.origen, 'archivo', c.archivo)
                        FROM padron_cargas c WHERE c.id = cfg.anexos_carga_id),
            'enCurso', EXISTS (SELECT 1 FROM padron_cargas WHERE padron = 'anexos' AND estado = 'en_curso'
                                AND iniciada_en > now() - interval '4 hours'),
            'cargas', coalesce((
                SELECT json_agg(json_build_object(
                           'id', x.id, 'origen', x.origen, 'estado', x.estado,
                           'iniciadaEn', x.iniciada_en, 'terminadaEn', x.terminada_en,
                           'archivo', x.archivo, 'bytes', x.bytes, 'filas', x.filas,
                           'descartadas', x.descartadas, 'motivo', x.motivo,
                           'usuario', (SELECT u.nombre FROM usuarios u WHERE u.id = x.usuario_id))
                         ORDER BY x.iniciada_en DESC)
                  FROM (SELECT * FROM padron_cargas WHERE padron = 'anexos' ORDER BY iniciada_en DESC LIMIT 15) x), '[]'::json))
    )::text
      FROM consultas_config cfg WHERE cfg.id = 1;
$$;
ALTER FUNCTION padron_estado() OWNER TO plataforma_puertas;
GRANT EXECUTE ON FUNCTION padron_estado() TO plataforma_app;


INSERT INTO migraciones_aplicadas (nombre) VALUES ('045_padron_anexos')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN RAISE NOTICE '045: padrón de locales anexos de SUNAT (para Sucursales). Nada se borró.'; END $$;