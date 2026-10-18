-- =============================================================================
-- 029 — CONSULTAS DE RUC, DNI Y TIPO DE CAMBIO (Decolecta): EL CONTADOR DEL MES
--
-- La plataforma consulta RUC, DNI y el tipo de cambio de SUNAT en un servicio
-- externo (Decolecta) que da un número de consultas gratis al mes. Aquí se
-- cuentan, para avisar a los super administradores ANTES de que se acaben (al
-- 80 %) y cuando se acaban. Cada aviso sale UNA vez por mes.
--
-- Nadie lee ni escribe la tabla directamente: solo por las funciones.
-- No se borra nada. Se puede correr dos veces.
-- =============================================================================

BEGIN;

CREATE TABLE IF NOT EXISTS consultas_externas (
    id             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    proveedor      text        NOT NULL,
    -- El primer día del mes, por fecha de Lima.
    mes            date        NOT NULL,
    usadas         integer     NOT NULL DEFAULT 0,
    limite         integer     NOT NULL,
    ultima_en      timestamptz,
    agotada_en     timestamptz,
    aviso_80_en    timestamptz,
    aviso_100_en   timestamptz,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ux_consultas_externas UNIQUE (proveedor, mes)
);

COMMENT ON TABLE consultas_externas IS
    'Consultas del mes a servicios externos (RUC, DNI, tipo de cambio). Solo por funciones.';

ALTER TABLE consultas_externas ENABLE ROW LEVEL SECURITY;
ALTER TABLE consultas_externas FORCE  ROW LEVEL SECURITY;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditar_consultas_externas') THEN
        PERFORM vigilar_tabla('consultas_externas');
    END IF;
END $$;


-- Anota UNA consulta (o que el servicio dijo «se acabaron») y dice si toca
-- avisar. Solo devuelve los correos de los super administradores cuando hay
-- aviso que mandar, y cada aviso sale una sola vez por mes.
CREATE OR REPLACE FUNCTION anotar_consulta_externa(p_proveedor text, p_limite integer, p_agotada boolean)
RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_mes   date := date_trunc('month', (now() AT TIME ZONE 'America/Lima'))::date;
    v_fila  consultas_externas%ROWTYPE;
    v_aviso text := 'ok';
BEGIN
    IF ctx_usuario() IS NULL THEN
        RETURN json_build_object('aviso', 'sin_sesion')::text;
    END IF;

    INSERT INTO consultas_externas (proveedor, mes, limite)
    VALUES (p_proveedor, v_mes, greatest(p_limite, 1))
    ON CONFLICT (proveedor, mes) DO NOTHING;

    SELECT * INTO v_fila FROM consultas_externas
     WHERE proveedor = p_proveedor AND mes = v_mes
     FOR UPDATE;

    IF p_agotada THEN
        UPDATE consultas_externas
           SET agotada_en = coalesce(agotada_en, now()),
               aviso_100_en = coalesce(aviso_100_en, now()),
               limite = greatest(p_limite, 1),
               ultima_en = now()
         WHERE id = v_fila.id;
        IF v_fila.aviso_100_en IS NULL THEN v_aviso := 'aviso_100'; END IF;
    ELSE
        UPDATE consultas_externas
           SET usadas = usadas + 1,
               limite = greatest(p_limite, 1),
               ultima_en = now()
         WHERE id = v_fila.id
        RETURNING * INTO v_fila;

        IF v_fila.usadas >= v_fila.limite AND v_fila.aviso_100_en IS NULL THEN
            UPDATE consultas_externas SET aviso_100_en = now() WHERE id = v_fila.id;
            v_aviso := 'aviso_100';
        ELSIF v_fila.usadas * 100 >= v_fila.limite * 80 AND v_fila.aviso_80_en IS NULL THEN
            UPDATE consultas_externas SET aviso_80_en = now() WHERE id = v_fila.id;
            v_aviso := 'aviso_80';
        END IF;
    END IF;

    SELECT * INTO v_fila FROM consultas_externas WHERE id = v_fila.id;

    RETURN json_build_object(
        'aviso',  v_aviso,
        'usadas', v_fila.usadas,
        'limite', v_fila.limite,
        'correos', CASE WHEN v_aviso = 'ok' THEN '[]'::json ELSE coalesce((
            SELECT json_agg(DISTINCT lower(btrim(u.correo)))
              FROM usuarios u
             WHERE u.es_super_admin AND u.activo AND u.correo IS NOT NULL), '[]'::json) END
    )::text;
END $$;

ALTER FUNCTION anotar_consulta_externa(text, integer, boolean) OWNER TO plataforma_puertas;


-- Lo que ve el super administrador en su inicio: el mes en curso.
CREATE OR REPLACE FUNCTION estado_consultas_externas(p_proveedor text)
RETURNS text
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_mes  date := date_trunc('month', (now() AT TIME ZONE 'America/Lima'))::date;
    v_fila consultas_externas%ROWTYPE;
BEGIN
    IF NOT ctx_es_super() THEN RETURN NULL; END IF;

    SELECT * INTO v_fila FROM consultas_externas WHERE proveedor = p_proveedor AND mes = v_mes;

    RETURN json_build_object(
        'mes',       v_mes,
        'usadas',    coalesce(v_fila.usadas, 0),
        'limite',    v_fila.limite,
        'agotadaEn', v_fila.agotada_en,
        'ultimaEn',  v_fila.ultima_en
    )::text;
END $$;

ALTER FUNCTION estado_consultas_externas(text) OWNER TO plataforma_puertas;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('029_consultas_externas')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;