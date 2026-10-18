-- =============================================================================
-- 025 — LOS CORREOS DEL CERTIFICADO POR VENCER (paso 6.3)
--
-- La revisión diaria (013) ya sabe hasta cuándo vale el certificado de cada
-- empresa, y el Inicio y las listas ya lo enseñan. Faltaba el correo: quien no
-- entra a la plataforma se entera el día que no puede emitir.
--
-- CUÁNDO: a los 30, 15, 7 y 1 días, el día que vence (0) y una vez vencido
-- (-1). Una sola vez cada uno POR CERTIFICADO: la fila guarda la fecha de
-- vencimiento, así que al subir el certificado nuevo la cuenta empieza sola.
--
-- SIN AVALANCHAS: si la API estuvo apagada, solo sale el aviso que toca hoy
-- (el más cercano), no todos los atrasados. Y un certificado vencido hace más
-- de 30 días no avisa: esa empresa ya se enteró.
--
-- A QUIÉN (lo decide `destinatarios_aviso_certificado`):
--   · el correo del titular de la empresa;
--   · los administradores de la empresa (rol Administrador, usuarios de esa
--     empresa);
--   · si está en una cartera: el dueño de la cuenta y los colaboradores
--     administradores que la pueden abrir;
--   · los super administradores.
--   Sin repetir correos.
--
-- No entran las empresas desactivadas, suspendidas o en pausa, ni las de una
-- cuenta suspendida.
--
-- Todo por funciones SECURITY DEFINER: el aviso no tiene sesión, y la tabla
-- no la lee nadie más. No se borra nada.
-- =============================================================================

BEGIN;

-- =============================================================================
-- 1. LO QUE YA SALIÓ
-- =============================================================================

CREATE TABLE avisos_certificado (
    id             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id     uuid        NOT NULL REFERENCES empresas (id),

    -- El certificado del que se avisó: su fecha de vencimiento.
    valido_hasta   timestamptz NOT NULL,

    -- 30 · 15 · 7 · 1 · 0 (vence hoy) · -1 (ya venció).
    umbral         smallint    NOT NULL CHECK (umbral IN (30, 15, 7, 1, 0, -1)),

    -- Los días que quedaban cuando salió (el texto del correo).
    dias           integer     NOT NULL,

    -- A quiénes se escribió y a quiénes no se pudo.
    destinatarios  text[]      NOT NULL,
    fallidos       text[]      NOT NULL DEFAULT '{}',

    enviado_en     timestamptz NOT NULL DEFAULT now(),

    -- Los de siempre, para la bitácora.
    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ux_avisos_certificado UNIQUE (empresa_id, valido_hasta, umbral)
);

CREATE INDEX ix_avisos_certificado_empresa ON avisos_certificado (empresa_id, enviado_en DESC);

COMMENT ON TABLE avisos_certificado IS
    'Correos del certificado por vencer (paso 6.3). Uno por empresa, certificado y umbral. Solo por funciones.';

-- Sin políticas: la aplicación no la lee ni la escribe directamente.
ALTER TABLE avisos_certificado ENABLE ROW LEVEL SECURITY;
ALTER TABLE avisos_certificado FORCE  ROW LEVEL SECURITY;

SELECT vigilar_tabla('avisos_certificado');


-- =============================================================================
-- 2. QUÉ AVISOS TOCAN AHORA
--
-- dias: POR FECHAS DE LIMA, no por horas, porque el correo enseña la fecha:
-- «vence el 20 de octubre, dentro de 6 días» tiene que cuadrar con el
-- calendario. 0 es «vence hoy»; si ya pasó la hora, aunque sea hoy, cuenta como
-- vencido (-1 o menos).
-- umbral: el más cercano que ya se alcanzó.
-- =============================================================================

CREATE OR REPLACE FUNCTION certificados_por_avisar()
RETURNS TABLE (empresa_id uuid, valido_hasta timestamptz, umbral smallint, dias integer)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    WITH vigentes AS (
        SELECT c.empresa_id,
               c.certificado_valido_hasta AS valido_hasta,
               CASE WHEN c.certificado_valido_hasta <= now()
                    THEN least(-1, (c.certificado_valido_hasta AT TIME ZONE 'America/Lima')::date
                                 - (now() AT TIME ZONE 'America/Lima')::date)
                    ELSE (c.certificado_valido_hasta AT TIME ZONE 'America/Lima')::date
                       - (now() AT TIME ZONE 'America/Lima')::date
               END AS dias
          FROM credenciales_facturacion c
          JOIN empresas e ON e.id = c.empresa_id
          LEFT JOIN cuentas cu ON cu.id = e.cuenta_id
         WHERE c.activo
           AND c.certificado_valido_hasta IS NOT NULL
           AND e.activo
           AND e.suspendida_en IS NULL
           AND e.pausada_en IS NULL
           AND COALESCE(cu.estado <> 'suspendida', true)
    ),
    con_umbral AS (
        SELECT v.*,
               (CASE WHEN v.dias < 0   THEN -1
                     WHEN v.dias = 0   THEN 0
                     WHEN v.dias = 1   THEN 1
                     WHEN v.dias <= 7  THEN 7
                     WHEN v.dias <= 15 THEN 15
                     ELSE 30 END)::smallint AS umbral
          FROM vigentes v
         WHERE v.dias <= 30
           AND v.dias >= -30
    )
    SELECT u.empresa_id, u.valido_hasta, u.umbral, u.dias
      FROM con_umbral u
     WHERE NOT EXISTS (SELECT 1 FROM avisos_certificado a
                        WHERE a.empresa_id   = u.empresa_id
                          AND a.valido_hasta = u.valido_hasta
                          AND a.umbral       = u.umbral)
     ORDER BY u.dias, u.empresa_id;
$$;


-- =============================================================================
-- 3. A QUIÉN
-- =============================================================================

CREATE OR REPLACE FUNCTION destinatarios_aviso_certificado(p_empresa uuid)
RETURNS SETOF text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    SELECT min(btrim(x.correo))
      FROM (
            -- El titular.
            SELECT e.correo_titular AS correo
              FROM empresas e
             WHERE e.id = p_empresa

            UNION ALL

            -- Los administradores de la empresa (los de una sola empresa).
            SELECT u.correo
              FROM usuarios u
              JOIN usuario_rol ur ON ur.usuario_id = u.id AND ur.activo
             WHERE u.empresa_id = p_empresa
               AND u.nivel_cuenta IS NULL
               AND u.activo
               AND ur.rol_id = '11111111-1111-1111-1111-111111111111'

            UNION ALL

            -- El dueño de la cuenta y los colaboradores administradores que la abren.
            SELECT u.correo
              FROM usuarios u
             WHERE u.nivel_cuenta IN ('dueno', 'colaborador_admin')
               AND puede_abrir(u.id, p_empresa)

            UNION ALL

            -- Los super administradores.
            SELECT u.correo
              FROM usuarios u
             WHERE u.es_super_admin
               AND u.activo
           ) x
     WHERE x.correo IS NOT NULL
       AND btrim(x.correo) <> ''
     GROUP BY lower(btrim(x.correo))
     ORDER BY lower(btrim(x.correo));
$$;


-- =============================================================================
-- 4. TOMAR UN AVISO
--
-- Vuelve a comprobar que toca y lo anota. La API lo llama dentro de una
-- transacción, escribe los correos y solo confirma si salió alguno: si no
-- salió ninguno, deshace y se reintenta en la vuelta siguiente (una hora).
--
-- Dos instancias a la vez: la segunda espera a la primera en el índice único
-- y, cuando esta confirma, no inserta nada y recibe cero filas.
-- =============================================================================

CREATE OR REPLACE FUNCTION tomar_aviso_certificado(
    p_empresa uuid, p_valido_hasta timestamptz, p_umbral smallint)
RETURNS TABLE (aviso_id uuid, nombre text, ruc text, valido_hasta_lima date, dias integer, correos text[])
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE
    v_dias    integer;
    v_correos text[];
    v_id      uuid;
BEGIN
    SELECT p.dias INTO v_dias
      FROM certificados_por_avisar() p
     WHERE p.empresa_id = p_empresa AND p.valido_hasta = p_valido_hasta AND p.umbral = p_umbral;

    IF NOT FOUND THEN RETURN; END IF;

    v_correos := ARRAY(SELECT d FROM destinatarios_aviso_certificado(p_empresa) d);
    IF cardinality(v_correos) = 0 THEN RETURN; END IF;

    INSERT INTO avisos_certificado (empresa_id, valido_hasta, umbral, dias, destinatarios)
    VALUES (p_empresa, p_valido_hasta, p_umbral, v_dias, v_correos)
    ON CONFLICT ON CONSTRAINT ux_avisos_certificado DO NOTHING
    RETURNING id INTO v_id;

    IF v_id IS NULL THEN RETURN; END IF;

    RETURN QUERY
        SELECT v_id, e.nombre, e.ruc, (p_valido_hasta AT TIME ZONE 'America/Lima')::date, v_dias, v_correos
          FROM empresas e
         WHERE e.id = p_empresa;
END $$;


-- Lo que no salió, para saberlo después.
CREATE OR REPLACE FUNCTION anotar_fallidos_aviso_certificado(p_aviso uuid, p_fallidos text[])
RETURNS void
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    UPDATE avisos_certificado SET fallidos = COALESCE(p_fallidos, '{}') WHERE id = p_aviso;
$$;


REVOKE ALL ON FUNCTION certificados_por_avisar()                                    FROM PUBLIC;
REVOKE ALL ON FUNCTION destinatarios_aviso_certificado(uuid)                        FROM PUBLIC;
REVOKE ALL ON FUNCTION tomar_aviso_certificado(uuid, timestamptz, smallint)         FROM PUBLIC;
REVOKE ALL ON FUNCTION anotar_fallidos_aviso_certificado(uuid, text[])              FROM PUBLIC;

GRANT EXECUTE ON FUNCTION certificados_por_avisar()                                 TO plataforma_app;
GRANT EXECUTE ON FUNCTION tomar_aviso_certificado(uuid, timestamptz, smallint)      TO plataforma_app;
GRANT EXECUTE ON FUNCTION anotar_fallidos_aviso_certificado(uuid, text[])           TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('025_avisos_certificado');

COMMIT;