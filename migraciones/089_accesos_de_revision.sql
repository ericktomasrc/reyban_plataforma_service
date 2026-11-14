-- =============================================================================
-- 089 — ACCESOS DE REVISIÓN (e240, 11 nov 2026)
--
-- Para que alguien de fuera (Culqi, un banco, un socio) pruebe la plataforma
-- como un cliente, sin correo y sin segundo factor:
--
--   · El super administrador crea el acceso: usuario, para quién, tipo de
--     cuenta (Negocio o Profesional), cuántos días y si empieza en prueba
--     gratis o con la prueba terminada. La contraseña la genera la API y se
--     muestra una sola vez (aquí solo llega su hash).
--   · Es una cuenta de verdad (la cuenta y su dueño), marcada en
--     `cuentas.revision_hasta`. Su «correo» es <usuario>@revision.invalid: el
--     dominio .invalid no existe en internet (RFC 2606), así que nunca sale un
--     correo hacia allí (la API además no los envía).
--   · El nombre del titular empieza con «REVISIÓN ·»: se reconoce en todas
--     las listas del super administrador.
--   · Vence sola, se apaga en un clic, se le cambia la contraseña y se le
--     termina o se le devuelve la prueba gratis.
--   · Los pagos de un acceso de revisión en modo «simulado» (el que se usa
--     cuando la plataforma ya cobra de verdad) no generan comprobante de Reyban.
--
-- Lo que un acceso de revisión NO puede hacer (invitar gente, cambiar clave o
-- correo, subir certificado, traspasos…) lo corta la API.
--
-- Se puede correr dos veces. Nada se borra.
-- =============================================================================
BEGIN;

ALTER TABLE cuentas
    ADD COLUMN IF NOT EXISTS revision_hasta      timestamptz,
    ADD COLUMN IF NOT EXISTS revision_para       text,
    ADD COLUMN IF NOT EXISTS revision_apagado_en timestamptz;

COMMENT ON COLUMN cuentas.revision_hasta IS
    '089: no nulo = acceso de revisión (Culqi, un banco…). Hasta cuándo puede entrar.';
COMMENT ON COLUMN cuentas.revision_para IS '089: para quién es el acceso (solo para reconocerlo).';
COMMENT ON COLUMN cuentas.revision_apagado_en IS '089: cuándo lo apagó el super administrador.';

DO $$ BEGIN
    ALTER TABLE cuentas ADD CONSTRAINT ck_cuentas_revision CHECK (
        (revision_hasta IS NULL AND revision_para IS NULL AND revision_apagado_en IS NULL)
        OR (revision_hasta IS NOT NULL AND length(btrim(COALESCE(revision_para, ''))) BETWEEN 2 AND 80));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

CREATE INDEX IF NOT EXISTS ix_cuentas_revision ON cuentas (revision_hasta) WHERE revision_hasta IS NOT NULL;


-- --- ¿Es un acceso de revisión? (para el ingreso, /api/yo y los candados) --
-- Nulo si no lo es. Solo de uno mismo (o el super administrador).
CREATE OR REPLACE FUNCTION revision_de_usuario(p_usuario uuid) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT jsonb_build_object(
               'para',   c.revision_para,
               'hasta',  c.revision_hasta,
               'activa', c.revision_apagado_en IS NULL AND c.revision_hasta > now())
      FROM usuarios u JOIN cuentas c ON c.id = u.cuenta_id
     WHERE u.id = p_usuario
       AND c.revision_hasta IS NOT NULL
       AND (p_usuario = ctx_usuario() OR ctx_es_super());
$$;


-- --- Crear --------------------------------------------------------------------
CREATE OR REPLACE FUNCTION revision_crear(p_usuario text, p_para text, p_tipo text, p_dias integer,
                                          p_en_prueba boolean, p_clave_hash text) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_usuario text := lower(btrim(COALESCE(p_usuario, '')));
    v_para    text := btrim(COALESCE(p_para, ''));
    v_cuenta  uuid;
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'REVISION: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF v_usuario !~ '^[a-z0-9][a-z0-9._-]{2,29}$' THEN
        RAISE EXCEPTION 'REVISION: El usuario va en minúsculas, de 3 a 30 letras o números (puede llevar . _ -), sin espacios ni tildes.';
    END IF;
    IF length(v_para) NOT BETWEEN 2 AND 80 THEN
        RAISE EXCEPTION 'REVISION: Escribe para quién es (por ejemplo, Culqi).';
    END IF;
    IF p_tipo NOT IN ('negocio', 'profesional') THEN
        RAISE EXCEPTION 'REVISION: Elige Negocio o Profesional.';
    END IF;
    IF p_dias IS NULL OR p_dias NOT BETWEEN 1 AND 60 THEN
        RAISE EXCEPTION 'REVISION: Los días van de 1 a 60.';
    END IF;
    IF COALESCE(p_clave_hash, '') = '' THEN
        RAISE EXCEPTION 'REVISION: Falta la contraseña.';
    END IF;
    IF EXISTS (SELECT 1 FROM usuarios
                WHERE lower(alias) = v_usuario OR lower(correo) = v_usuario || '@revision.invalid') THEN
        RAISE EXCEPTION 'REVISION: Ese usuario ya existe. Elige otro.';
    END IF;

    INSERT INTO cuentas (tipo, titular_tipo, titular_documento, titular_nombre, estado, confirmada_en,
                         origen, terminos_aceptados_en, prueba_hasta,
                         revision_hasta, revision_para)
    VALUES (p_tipo, 'natural', '00000000', left('REVISIÓN · ' || v_para, 200), 'activa', now(),
            'registro', now(),
            CASE WHEN p_en_prueba THEN NULL ELSE now() - interval '1 minute' END,   -- nulo: la prueba de siempre
            now() + make_interval(days => p_dias), v_para)
    RETURNING id INTO v_cuenta;

    INSERT INTO usuarios (cuenta_id, correo, nombre, alias, clave_hash, clave_cambiada_en, clave_cambio_forzado,
                          nivel_cuenta, tipo_documento, numero_documento, nombres, apellido_paterno,
                          apellido_materno, ficha_confirmada_en)
    VALUES (v_cuenta, v_usuario || '@revision.invalid', left('Revisión ' || v_para, 120), v_usuario,
            p_clave_hash, now(), false,
            'dueno', '1', '00000000', 'Revisión', left(v_para, 60), 'Reyban', now());

    RETURN v_cuenta;
END $$;


-- --- La lista del super administrador ------------------------------------------
CREATE OR REPLACE FUNCTION revision_lista() RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'REVISION: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    RETURN COALESCE((
        SELECT jsonb_agg(x ORDER BY x.creado DESC) FROM (
            SELECT c.id,
                   c.revision_para AS para,
                   u.alias AS usuario,
                   c.tipo,
                   c.revision_hasta AS hasta,
                   c.creado_en AS creado,
                   CASE WHEN c.revision_apagado_en IS NOT NULL THEN 'apagado'
                        WHEN c.revision_hasta <= now() THEN 'vencido'
                        ELSE 'activo' END AS estado,
                   c.prueba_hasta AS "pruebaHasta",
                   u.ultimo_ingreso_en AS "ultimoIngreso",
                   (SELECT count(*) FROM eventos_seguridad v
                     WHERE v.usuario_id = u.id AND v.tipo = 'ingreso' AND v.exito) AS ingresos,
                   (SELECT COALESCE(jsonb_agg(jsonb_build_object('nombre', e.nombre, 'ruc', e.ruc, 'plan', e.plan)
                                              ORDER BY e.creado_en), '[]'::jsonb)
                      FROM empresas e WHERE e.cuenta_id = c.id) AS empresas,
                   (SELECT count(*) FROM cobros k WHERE k.cuenta_id = c.id AND k.estado = 'pagado') AS pagos,
                   (SELECT count(*) FROM cobros k WHERE k.cuenta_id = c.id AND k.estado = 'rechazado') AS rechazos
              FROM cuentas c
              JOIN usuarios u ON u.cuenta_id = c.id AND u.nivel_cuenta = 'dueno'
             WHERE c.revision_hasta IS NOT NULL) x), '[]'::jsonb);
END $$;


-- --- Apagar, alargar, nueva contraseña, prueba ------------------------------------
CREATE OR REPLACE FUNCTION revision_cambiar(p_cuenta uuid, p_accion text, p_dias integer DEFAULT NULL,
                                            p_clave_hash text DEFAULT NULL) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_dias int;
BEGIN
    IF NOT ctx_es_super() THEN
        RAISE EXCEPTION 'REVISION: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege';
    END IF;
    PERFORM 1 FROM cuentas WHERE id = p_cuenta AND revision_hasta IS NOT NULL FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'REVISION: Ese acceso no existe.'; END IF;

    IF p_accion = 'apagar' THEN
        UPDATE cuentas SET revision_apagado_en = now(), revision_hasta = LEAST(revision_hasta, now())
         WHERE id = p_cuenta;
        UPDATE sesiones s SET revocada_en = now(), motivo_revocacion = 'acceso de revision apagado'
          FROM usuarios u WHERE u.id = s.usuario_id AND u.cuenta_id = p_cuenta AND s.revocada_en IS NULL;

    ELSIF p_accion = 'alargar' THEN
        IF p_dias IS NULL OR p_dias NOT BETWEEN 1 AND 60 THEN
            RAISE EXCEPTION 'REVISION: Los días van de 1 a 60.';
        END IF;
        UPDATE cuentas SET revision_hasta = now() + make_interval(days => p_dias), revision_apagado_en = NULL
         WHERE id = p_cuenta;

    ELSIF p_accion = 'clave' THEN
        IF COALESCE(p_clave_hash, '') = '' THEN RAISE EXCEPTION 'REVISION: Falta la contraseña.'; END IF;
        UPDATE usuarios SET clave_hash = p_clave_hash, clave_cambiada_en = now(), clave_cambio_forzado = false,
                            intentos_fallidos = 0, bloqueado_hasta = NULL
         WHERE cuenta_id = p_cuenta AND nivel_cuenta = 'dueno';
        UPDATE sesiones s SET revocada_en = now(), motivo_revocacion = 'acceso de revision: clave nueva'
          FROM usuarios u WHERE u.id = s.usuario_id AND u.cuenta_id = p_cuenta AND s.revocada_en IS NULL;

    ELSIF p_accion = 'terminar_prueba' THEN
        UPDATE cuentas  SET prueba_hasta = now() - interval '1 minute' WHERE id = p_cuenta;
        UPDATE empresas SET prueba_hasta = now() - interval '1 minute'
         WHERE cuenta_id = p_cuenta AND prueba_hasta > now();

    ELSIF p_accion = 'dar_prueba' THEN
        v_dias := COALESCE((SELECT dias_prueba FROM plataforma_ajustes WHERE id = 1), 30);
        UPDATE cuentas  SET prueba_hasta = now() + make_interval(days => v_dias) WHERE id = p_cuenta;
        UPDATE empresas SET prueba_hasta = now() + make_interval(days => v_dias), prueba_motivo = COALESCE(prueba_motivo, 'cuenta')
         WHERE cuenta_id = p_cuenta;

    ELSE
        RAISE EXCEPTION 'REVISION: Acción desconocida.';
    END IF;
END $$;


-- --- Los pagos «simulados» de un acceso de revisión no llevan comprobante -------
-- (con la plataforma cobrando de verdad, un acceso de revisión paga en modo
-- simulado: Reyban no debe emitirle una factura real por un pago que no existió).
CREATE OR REPLACE FUNCTION cobros_comprobantes_pendientes() RETURNS SETOF uuid
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NOT ctx_es_super() THEN RAISE EXCEPTION 'COBRO_NO_PUEDE: Solo la plataforma.' USING ERRCODE = 'insufficient_privilege'; END IF;
    RETURN QUERY
    SELECT x.id FROM cobros x, cobros_ajustes k
     WHERE k.id = 1 AND k.emisora_empresa IS NOT NULL
       AND x.estado = 'pagado' AND x.modo IN ('real', 'manual', 'prueba', 'simulado')
       AND x.comprobante_estado IN ('pendiente', 'error')
       AND x.comprobante_intentos < 10
       AND (x.comprobante_intento_en IS NULL
            OR x.comprobante_intento_en < now() - make_interval(mins => (power(2, LEAST(x.comprobante_intentos, 8)))::int))
       -- 089
       AND NOT (x.modo = 'simulado' AND EXISTS (SELECT 1 FROM cuentas c
                                                 WHERE c.id = x.cuenta_id AND c.revision_hasta IS NOT NULL))
     ORDER BY x.pagado_en
     LIMIT 50;
END $$;


DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY['revision_de_usuario(uuid)',
                             'revision_crear(text, text, text, integer, boolean, text)',
                             'revision_lista()',
                             'revision_cambiar(uuid, text, integer, text)',
                             'cobros_comprobantes_pendientes()'] LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO plataforma_app', f);
    END LOOP;
END $$;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('089_accesos_de_revision')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;