-- =============================================================================
-- 018 — LA CARTERA (fase 3)
--
-- Diseño: cartera-diseno.md §5 y el lienzo «Fase 3 — Cartera».
--
-- Hasta la 017, un usuario era de UNA empresa (usuarios.empresa_id). Desde aquí
-- las personas de una CUENTA (el dueño y sus colaboradores) pueden trabajar en
-- VARIAS empresas de esa cuenta, una a la vez:
--
--   usuarios.nivel_cuenta   dueno · colaborador_admin · colaborador. Nulo =
--                           usuario de una sola empresa (la cajera), como hoy.
--   usuarios.ve_todas       el colaborador ve todas las empresas de la cuenta,
--                           también las que se agreguen después.
--   usuario_empresa         las empresas que ve un colaborador sin «ve_todas».
--   usuarios.empresa_id     para los de la cuenta, LA EMPRESA ABIERTA. Cambiarla
--                           es abrir_empresa(), que comprueba el acceso.
--
-- LO QUE NO CAMBIA: el aislamiento. ctx_empresa() sigue siendo UNA empresa, la
-- abierta, y todas las políticas de las tablas de negocio siguen igual. Lo
-- nuevo es quién puede abrir cuál, y eso lo deciden la base y estas funciones:
--
--   puede_abrir(usuario, empresa)   la regla, en un solo sitio
--   resolver_sesion                 si ya no puede abrirla (le quitaron el
--                                   acceso, la pusieron en pausa), la sesión
--                                   sigue viva pero SIN empresa
--   abrir_empresa(id)               el selector
--   mis_empresas()                  la bandeja
--   colaboradores(), invitar_colaborador, editar_colaborador, quitar_colaborador
--   pausar_empresa, reanudar_empresa  «Quitar de mi cartera»
--   convertir_a_profesional()
--
-- LOS DE LA CUENTA NO SALEN EN «USUARIOS» DE UNA EMPRESA: `p_usuarios` deja de
-- enseñar, por empresa, a quien tiene nivel de cuenta. Si no, el administrador
-- de un cliente vería al contador entre sus usuarios —y podría desactivarlo—
-- solo porque tiene su empresa abierta.
--
-- NADA SE BORRA: quitar un colaborador lo desactiva; quitar una empresa de la
-- cartera la pone en pausa.
--
-- Todo en una transacción.
-- =============================================================================

BEGIN;


-- =============================================================================
-- 1. COLUMNAS Y TABLA
-- =============================================================================

ALTER TABLE usuarios ADD COLUMN nivel_cuenta text;
ALTER TABLE usuarios ADD COLUMN ve_todas boolean NOT NULL DEFAULT false;

ALTER TABLE usuarios ADD CONSTRAINT ck_usuarios_nivel_cuenta CHECK (
    nivel_cuenta IS NULL
    OR (nivel_cuenta IN ('dueno', 'colaborador_admin', 'colaborador') AND cuenta_id IS NOT NULL));

-- Un solo dueño activo por cuenta.
CREATE UNIQUE INDEX ux_usuarios_dueno_cuenta
    ON usuarios (cuenta_id) WHERE nivel_cuenta = 'dueno' AND activo;

-- El dueño de cada cuenta que se registró: el primer usuario que tuvo.
UPDATE usuarios u
   SET nivel_cuenta = 'dueno'
  FROM (SELECT DISTINCT ON (u2.cuenta_id) u2.id
          FROM usuarios u2
          JOIN cuentas c ON c.id = u2.cuenta_id
         WHERE c.origen = 'registro' AND NOT u2.es_super_admin
         ORDER BY u2.cuenta_id, u2.creado_en, u2.id) primero
 WHERE u.id = primero.id;

ALTER TABLE empresas ADD COLUMN pausada_en   timestamptz;
ALTER TABLE empresas ADD COLUMN motivo_pausa text;
ALTER TABLE empresas ADD COLUMN pausada_por  uuid;

ALTER TABLE empresas ADD CONSTRAINT ck_empresas_pausa CHECK (
    pausada_en IS NULL OR length(btrim(motivo_pausa)) BETWEEN 10 AND 500);


CREATE TABLE usuario_empresa (
    id             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    usuario_id     uuid        NOT NULL REFERENCES usuarios (id),
    empresa_id     uuid        NOT NULL REFERENCES empresas (id),

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1
);

CREATE UNIQUE INDEX ux_usuario_empresa ON usuario_empresa (usuario_id, empresa_id);
CREATE INDEX ix_usuario_empresa_empresa ON usuario_empresa (empresa_id);

COMMENT ON TABLE usuario_empresa IS
    'Las empresas de la cuenta que ve un colaborador sin ve_todas. Se escribe solo por las funciones de la cartera.';

ALTER TABLE usuario_empresa ENABLE ROW LEVEL SECURITY;
ALTER TABLE usuario_empresa FORCE  ROW LEVEL SECURITY;

CREATE POLICY p_usuario_empresa ON usuario_empresa
    USING (ctx_es_super() OR usuario_id = ctx_usuario())
    WITH CHECK (ctx_es_super());

SELECT vigilar_tabla('usuario_empresa');

GRANT SELECT, INSERT, UPDATE ON usuario_empresa TO plataforma_app;


-- =============================================================================
-- 2. LOS DE LA CUENTA NO SON «USUARIOS» DE LA EMPRESA ABIERTA
-- =============================================================================

DROP POLICY p_usuarios ON usuarios;

CREATE POLICY p_usuarios ON usuarios
    USING (ctx_es_super()
           OR id = ctx_usuario()
           OR (empresa_id = ctx_empresa() AND nivel_cuenta IS NULL))
    WITH CHECK (ctx_es_super()
           OR id = ctx_usuario()
           OR (empresa_id = ctx_empresa() AND nivel_cuenta IS NULL));


-- Los roles, igual: los de la gente de la cuenta no son de la empresa abierta
-- (se escriben solo por funciones). Cada uno ve los suyos, abra la empresa
-- que abra: sin esto, sus permisos dependerían de qué empresa tiene abierta.
DROP POLICY p_usuario_rol ON usuario_rol;

CREATE POLICY p_usuario_rol ON usuario_rol
    USING (ctx_es_super()
           OR usuario_rol.usuario_id = ctx_usuario()
           OR EXISTS (SELECT 1 FROM usuarios u WHERE u.id = usuario_rol.usuario_id
                         AND u.empresa_id = ctx_empresa() AND u.nivel_cuenta IS NULL))
    WITH CHECK (ctx_es_super()
           OR EXISTS (SELECT 1 FROM usuarios u WHERE u.id = usuario_rol.usuario_id
                         AND u.empresa_id = ctx_empresa() AND u.nivel_cuenta IS NULL));


-- =============================================================================
-- 3. LA REGLA: ¿PUEDE ABRIR ESTA EMPRESA?
-- =============================================================================

CREATE OR REPLACE FUNCTION puede_abrir(p_usuario uuid, p_empresa uuid)
RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT EXISTS (
        SELECT 1
          FROM usuarios u
          JOIN empresas e ON e.cuenta_id = u.cuenta_id
         WHERE u.id = p_usuario
           AND e.id = p_empresa
           AND u.activo
           AND u.nivel_cuenta IS NOT NULL
           AND e.activo
           AND e.pausada_en IS NULL
           AND e.suspendida_en IS NULL
           AND (u.nivel_cuenta = 'dueno'
                OR u.ve_todas
                OR EXISTS (SELECT 1 FROM usuario_empresa ue
                            WHERE ue.usuario_id = u.id AND ue.empresa_id = e.id AND ue.activo)));
$$;


-- =============================================================================
-- 4. INGRESO Y SESIÓN
--
-- Mismas firmas (CREATE OR REPLACE). Para los de la cuenta, la empresa abierta
-- que ya no pueden abrir se cae (NULL) en vez de echarlos: entran a su bandeja.
-- Para un usuario de una sola empresa, una empresa en pausa cuenta como no
-- operativa, igual que una suspendida.
-- =============================================================================

CREATE OR REPLACE FUNCTION buscar_usuario_para_ingreso(p_correo text)
RETURNS TABLE (
    id                   uuid,
    empresa_id           uuid,
    nombre               text,
    clave_hash           text,
    activo               boolean,
    es_super_admin       boolean,
    intentos_fallidos    integer,
    bloqueado_hasta      timestamptz,
    dosfa_activo         boolean,
    clave_cambio_forzado boolean,
    empresa_operativa    boolean
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT u.id,
           CASE WHEN u.nivel_cuenta IS NOT NULL AND NOT puede_abrir(u.id, u.empresa_id)
                THEN NULL ELSE u.empresa_id END,
           u.nombre,
           u.clave_hash,
           u.activo,
           u.es_super_admin,
           u.intentos_fallidos,
           u.bloqueado_hasta,
           u.dosfa_activo,
           u.clave_cambio_forzado,
           (u.nivel_cuenta IS NOT NULL
            OR COALESCE(e.activo AND e.suspendida_en IS NULL AND e.pausada_en IS NULL, true))
           AND COALESCE(c.estado <> 'suspendida', true)
      FROM usuarios u
      LEFT JOIN empresas e ON e.id = u.empresa_id
      LEFT JOIN cuentas  c ON c.id = u.cuenta_id
     WHERE lower(u.correo) = lower(p_correo);
$$;


CREATE OR REPLACE FUNCTION resolver_sesion(p_token_hash bytea)
RETURNS TABLE (
    sesion_id            uuid,
    usuario_id           uuid,
    empresa_id           uuid,
    es_super_admin       boolean,
    dosfa_superado       boolean,
    clave_cambio_forzado boolean,
    expira_en            timestamptz,
    revocada_en          timestamptz,
    usuario_activo       boolean,
    empresa_operativa    boolean,
    suplantador_id       uuid,
    suplantador_nombre   text,
    suplantado_nombre    text,
    suplantacion_inicio  timestamptz
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT s.id,
           ef.id,
           CASE WHEN ef.nivel_cuenta IS NOT NULL AND NOT puede_abrir(ef.id, ef.empresa_id)
                THEN NULL ELSE ef.empresa_id END,
           CASE WHEN sup.id IS NOT NULL THEN false ELSE dueno.es_super_admin END,
           s.dosfa_superado,
           CASE WHEN sup.id IS NOT NULL THEN false ELSE dueno.clave_cambio_forzado END,
           s.expira_en,
           s.revocada_en,
           dueno.activo AND COALESCE(sup.activo, true),

           (ef.nivel_cuenta IS NOT NULL
            OR COALESCE(e.activo AND e.suspendida_en IS NULL AND e.pausada_en IS NULL, true))
           AND COALESCE(c.estado <> 'suspendida', true),

           CASE WHEN sup.id IS NOT NULL THEN dueno.id END,
           CASE WHEN sup.id IS NOT NULL THEN dueno.nombre END,
           sup.nombre,
           s.suplantacion_inicio

      FROM sesiones s
      JOIN usuarios dueno    ON dueno.id = s.usuario_id
      LEFT JOIN usuarios sup ON sup.id  = s.suplantando_usuario_id
      -- La identidad efectiva: la suplantada si la hay.
      JOIN LATERAL (SELECT COALESCE(sup.id, dueno.id)                     AS id,
                           COALESCE(sup.empresa_id, dueno.empresa_id)     AS empresa_id,
                           CASE WHEN sup.id IS NOT NULL THEN sup.nivel_cuenta ELSE dueno.nivel_cuenta END AS nivel_cuenta,
                           CASE WHEN sup.id IS NOT NULL THEN sup.cuenta_id ELSE dueno.cuenta_id END     AS cuenta_id
                   ) ef ON true
      LEFT JOIN empresas e ON e.id = ef.empresa_id
      LEFT JOIN cuentas  c ON c.id = ef.cuenta_id
     WHERE s.token_hash = p_token_hash;
$$;


-- =============================================================================
-- 5. AGREGAR EMPRESAS: LAS REGLAS DE LA CARTERA
--
-- Cambia sobre la 017:
--   · Cuenta Negocio: una empresa (activa o en pausa) y ya.
--   · Cuenta Profesional: el dueño y el colaborador administrador agregan las
--     que quieran (con el tope del día); el colaborador, no.
--   · Ya no hace falta estar sin empresa abierta: la nueva queda abierta.
-- Códigos nuevos: sin_permiso. «sin_cuenta» = usuario de una sola empresa.
-- =============================================================================

CREATE OR REPLACE FUNCTION revisar_empresa_propia(p_ruc text)
RETURNS text
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_usuario  uuid := ctx_usuario();
    v_cuenta   uuid;
    v_nivel    text;
    v_super    boolean;
    v_activo   boolean;
    v_estado   text;
    v_tipo     text;
    v_tope     integer;
    v_hoy      integer;
BEGIN
    SELECT u.cuenta_id, u.nivel_cuenta, u.es_super_admin, u.activo
      INTO v_cuenta, v_nivel, v_super, v_activo
      FROM usuarios u
     WHERE u.id = v_usuario;

    IF NOT FOUND OR NOT v_activo THEN RETURN 'sin_usuario'; END IF;
    IF v_super                   THEN RETURN 'super_admin'; END IF;
    IF v_nivel IS NULL           THEN RETURN 'sin_cuenta'; END IF;
    IF v_nivel = 'colaborador'   THEN RETURN 'sin_permiso'; END IF;

    SELECT c.estado, c.tipo, c.tope_empresas_dia INTO v_estado, v_tipo, v_tope
      FROM cuentas c WHERE c.id = v_cuenta;

    IF v_estado IS DISTINCT FROM 'activa' THEN RETURN 'cuenta_no_activa'; END IF;

    IF v_tipo = 'negocio' AND EXISTS (
        SELECT 1 FROM empresas e WHERE e.cuenta_id = v_cuenta AND e.activo)
    THEN
        RETURN 'ya_tiene_empresa';
    END IF;

    SELECT count(*) INTO v_hoy
      FROM empresas e
     WHERE e.cuenta_id = v_cuenta
       AND e.creado_en > now() - interval '1 day';

    IF v_hoy >= v_tope THEN RETURN 'tope'; END IF;

    IF p_ruc IS NOT NULL AND EXISTS (
        SELECT 1 FROM empresas e WHERE e.ruc = btrim(p_ruc) AND e.activo)
    THEN
        RETURN 'ruc_ocupado';
    END IF;

    RETURN 'ok';
END $$;


-- Los roles que da cada nivel, en un solo sitio. Administrador para el dueño y
-- el colaborador administrador; Facturador (emitir y consultar) para el
-- colaborador. El rol que no toca se desactiva (no se borra).
CREATE OR REPLACE FUNCTION ajustar_roles_de_nivel(p_usuario uuid)
RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_nivel text;
    v_rol   uuid;
BEGIN
    SELECT nivel_cuenta INTO v_nivel FROM usuarios WHERE id = p_usuario;
    IF v_nivel IS NULL THEN RETURN; END IF;

    v_rol := CASE WHEN v_nivel = 'colaborador'
                  THEN '22222222-2222-2222-2222-222222222222'::uuid
                  ELSE '11111111-1111-1111-1111-111111111111'::uuid END;

    INSERT INTO usuario_rol (usuario_id, rol_id) VALUES (p_usuario, v_rol)
    ON CONFLICT (usuario_id, rol_id) DO UPDATE SET activo = true;

    UPDATE usuario_rol SET activo = false
     WHERE usuario_id = p_usuario
       AND rol_id IN ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222')
       AND rol_id <> v_rol
       AND activo;
END $$;


CREATE OR REPLACE FUNCTION crear_empresa_propia(
    p_id              uuid,
    p_nombre          text,
    p_ruc             text,
    p_razon_social    text,
    p_nombre_comercial text,
    p_ambiente        text,
    p_correo_titular  text,
    p_base_url        text,
    p_clave_cifrada   bytea,
    p_pista           text,
    p_whsec_cifrado   bytea,
    p_webhook_url     text
) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_usuario uuid := ctx_usuario();
    v_cuenta  uuid;
    v_nivel   text;
    v_todas   boolean;
    v_res     text;
BEGIN
    -- El candado de la CUENTA: dos pestañas o dos colaboradores a la vez no
    -- se saltan el tope ni la regla de una empresa de la Cuenta Negocio.
    SELECT u.cuenta_id, u.nivel_cuenta, u.ve_todas INTO v_cuenta, v_nivel, v_todas
      FROM usuarios u WHERE u.id = v_usuario;

    IF v_cuenta IS NOT NULL THEN
        PERFORM 1 FROM cuentas WHERE id = v_cuenta FOR UPDATE;
    END IF;

    v_res := revisar_empresa_propia(p_ruc);
    IF v_res <> 'ok' THEN RETURN v_res; END IF;

    INSERT INTO empresas (id, cuenta_id, nombre, ruc, razon_social, nombre_comercial,
                          ambiente, datos_oficiales_en, correo_titular)
    VALUES (p_id, v_cuenta, btrim(p_nombre), btrim(p_ruc), btrim(p_razon_social),
            NULLIF(btrim(p_nombre_comercial), ''), COALESCE(p_ambiente, 'beta'), now(),
            NULLIF(btrim(p_correo_titular), ''));

    -- Quien la agrega la ve siempre, aunque no vea todas.
    IF v_nivel <> 'dueno' AND NOT v_todas THEN
        INSERT INTO usuario_empresa (usuario_id, empresa_id) VALUES (v_usuario, p_id)
        ON CONFLICT (usuario_id, empresa_id) DO UPDATE SET activo = true;
    END IF;

    -- Y queda abierta.
    UPDATE usuarios SET empresa_id = p_id WHERE id = v_usuario;

    PERFORM ajustar_roles_de_nivel(v_usuario);

    INSERT INTO credenciales_facturacion
           (empresa_id, base_url, clave_cifrada, pista,
            webhook_secreto_cifrado, webhook_url_servicio, webhook_revisado_en,
            ultima_llamada_en, creada_por_alta)
    VALUES (p_id, p_base_url, p_clave_cifrada, p_pista,
            p_whsec_cifrado, p_webhook_url, now(),
            now(), true);

    PERFORM registrar_evento('otro'::text, true, NULL, p_id, NULL,
        jsonb_build_object('accion', 'empresa_propia', 'ruc', p_ruc, 'nombre', p_nombre));

    RETURN 'ok';
END $$;


-- Activar Facturación desde el asistente (017): ahora también vale para quien
-- tiene la empresa abierta por la cartera (puede_abrir), no solo por empresa_id.
CREATE OR REPLACE FUNCTION activar_facturacion_propia()
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_usuario uuid := ctx_usuario();
    v_empresa uuid := ctx_empresa();
    v_activo  boolean;
BEGIN
    IF v_usuario IS NULL OR v_empresa IS NULL OR NOT EXISTS (
        SELECT 1 FROM usuarios u
         WHERE u.id = v_usuario AND u.activo
           AND (u.empresa_id = v_empresa OR puede_abrir(u.id, v_empresa)))
    THEN
        RETURN 'sin_empresa';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM permisos_efectivos p
         WHERE p.usuario_id = v_usuario
           AND p.permiso_codigo = 'facturacion.credenciales_gestionar')
    THEN
        RETURN 'sin_permiso';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM empresas e JOIN cuentas c ON c.id = e.cuenta_id
         WHERE e.id = v_empresa AND c.origen = 'registro')
    THEN
        RETURN 'no_es_autoservicio';
    END IF;

    SELECT m.activo INTO v_activo
      FROM empresa_modulo m
     WHERE m.empresa_id = v_empresa AND m.modulo_codigo = 'facturacion';

    IF FOUND THEN
        RETURN CASE WHEN v_activo THEN 'ya_activo' ELSE 'desactivado_por_admin' END;
    END IF;

    INSERT INTO empresa_modulo (empresa_id, modulo_codigo, contratado_en)
    VALUES (v_empresa, 'facturacion', now());

    PERFORM registrar_evento('otro'::text, true, NULL, v_empresa, NULL,
        jsonb_build_object('accion', 'facturacion_activada_por_asistente'));

    RETURN 'ok';
END $$;


-- =============================================================================
-- 6. EL SELECTOR Y LA BANDEJA
-- =============================================================================

-- ok · sin_acceso · pausada
CREATE OR REPLACE FUNCTION abrir_empresa(p_empresa uuid)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_usuario uuid := ctx_usuario();
BEGIN
    IF NOT puede_abrir(v_usuario, p_empresa) THEN
        IF EXISTS (SELECT 1 FROM empresas e JOIN usuarios u ON u.cuenta_id = e.cuenta_id
                    WHERE u.id = v_usuario AND e.id = p_empresa AND e.pausada_en IS NOT NULL
                      AND u.nivel_cuenta IS NOT NULL) THEN
            RETURN 'pausada';
        END IF;
        RETURN 'sin_acceso';
    END IF;

    UPDATE usuarios SET empresa_id = p_empresa WHERE id = v_usuario;
    RETURN 'ok';
END $$;


-- La bandeja: las empresas de la cuenta que ve quien llama, con lo que hace
-- falta para pintar su estado. Las en pausa salen también (para reanudarlas),
-- aunque no se puedan abrir.
CREATE OR REPLACE FUNCTION mis_empresas()
RETURNS TABLE (
    id                 uuid,
    nombre             text,
    ruc                text,
    razon_social       text,
    ambiente           text,
    abierta            boolean,
    pausada_en         timestamptz,
    motivo_pausa       text,
    facturacion_activa boolean,
    lista_para_emitir  boolean,
    certificado_hasta  timestamptz,
    rechazos_7_dias    integer,
    ultimo_documento   timestamptz,
    la_ven             text[]
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    WITH yo AS (
        SELECT u.id, u.cuenta_id, u.nivel_cuenta, u.ve_todas, u.empresa_id
          FROM usuarios u
         WHERE u.id = ctx_usuario() AND u.activo AND u.nivel_cuenta IS NOT NULL
    )
    SELECT e.id, e.nombre, e.ruc, e.razon_social, e.ambiente,
           e.id = yo.empresa_id,
           e.pausada_en, e.motivo_pausa,
           EXISTS (SELECT 1 FROM empresa_modulo m
                    WHERE m.empresa_id = e.id AND m.modulo_codigo = 'facturacion' AND m.activo),
           (cf.emisor_estado #>> '{listo,paraEmitir}')::boolean,
           cf.certificado_valido_hasta,
           (SELECT count(*)::int FROM documentos d
             WHERE d.empresa_id = e.id AND d.estado = 'rechazado'
               AND d.creado_en > now() - interval '7 days'),
           (SELECT max(d.creado_en) FROM documentos d
             WHERE d.empresa_id = e.id AND d.estado <> 'borrador'),
           ARRAY(SELECT o.nombre FROM usuarios o
                  WHERE o.cuenta_id = e.cuenta_id AND o.activo
                    AND o.nivel_cuenta IN ('colaborador_admin', 'colaborador')
                    AND (o.ve_todas OR EXISTS (SELECT 1 FROM usuario_empresa ue
                                                WHERE ue.usuario_id = o.id AND ue.empresa_id = e.id
                                                  AND ue.activo))
                  ORDER BY o.nombre)
      FROM yo
      JOIN empresas e ON e.cuenta_id = yo.cuenta_id AND e.activo
      LEFT JOIN credenciales_facturacion cf ON cf.empresa_id = e.id AND cf.activo
     WHERE yo.nivel_cuenta = 'dueno' OR yo.ve_todas
        OR EXISTS (SELECT 1 FROM usuario_empresa ue
                    WHERE ue.usuario_id = yo.id AND ue.empresa_id = e.id AND ue.activo)
     ORDER BY e.pausada_en IS NOT NULL, e.nombre;
$$;


-- =============================================================================
-- 7. COLABORADORES
-- =============================================================================

CREATE OR REPLACE FUNCTION colaboradores()
RETURNS TABLE (
    id          uuid,
    nombre      text,
    correo      text,
    alias       text,
    nivel       text,
    ve_todas    boolean,
    empresas    uuid[],
    activo      boolean,
    pendiente   boolean
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
    SELECT o.id, o.nombre, o.correo, o.alias, o.nivel_cuenta, o.ve_todas,
           ARRAY(SELECT ue.empresa_id FROM usuario_empresa ue
                  WHERE ue.usuario_id = o.id AND ue.activo),
           o.activo,
           o.ultimo_ingreso_en IS NULL
      FROM usuarios yo
      JOIN usuarios o ON o.cuenta_id = yo.cuenta_id AND o.nivel_cuenta IS NOT NULL
     WHERE yo.id = ctx_usuario() AND yo.activo
       AND yo.nivel_cuenta IN ('dueno', 'colaborador_admin')
     ORDER BY o.nivel_cuenta = 'dueno' DESC, o.activo DESC, o.nombre;
$$;


-- Quien invita: el dueño (a los dos niveles) o el colaborador administrador
-- (solo colaboradores). Las empresas tienen que ser de la cuenta.
-- ok · sin_permiso · correo_ocupado · nivel · empresa_ajena · cuenta_no_activa
CREATE OR REPLACE FUNCTION invitar_colaborador(
    p_correo     text,
    p_nombre     text,
    p_nivel      text,
    p_ve_todas   boolean,
    p_empresas   uuid[],
    p_token_hash bytea,
    p_expira_en  timestamptz
) RETURNS TABLE (resultado text, usuario_id uuid)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_yo      uuid := ctx_usuario();
    v_cuenta  uuid;
    v_mio     text;
    v_tipo    text;
    v_estado  text;
    v_nuevo   uuid;
BEGIN
    SELECT u.cuenta_id, u.nivel_cuenta INTO v_cuenta, v_mio
      FROM usuarios u WHERE u.id = v_yo AND u.activo;

    SELECT c.tipo, c.estado INTO v_tipo, v_estado FROM cuentas c WHERE c.id = v_cuenta;

    IF v_mio IS NULL OR v_mio = 'colaborador' OR v_tipo <> 'profesional' THEN
        RETURN QUERY SELECT 'sin_permiso'::text, NULL::uuid; RETURN;
    END IF;
    IF v_estado <> 'activa' THEN
        RETURN QUERY SELECT 'cuenta_no_activa'::text, NULL::uuid; RETURN;
    END IF;
    IF p_nivel NOT IN ('colaborador_admin', 'colaborador')
       OR (p_nivel = 'colaborador_admin' AND v_mio <> 'dueno') THEN
        RETURN QUERY SELECT 'nivel'::text, NULL::uuid; RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM unnest(COALESCE(p_empresas, '{}')) x(id)
                WHERE NOT EXISTS (SELECT 1 FROM empresas e WHERE e.id = x.id AND e.cuenta_id = v_cuenta)) THEN
        RETURN QUERY SELECT 'empresa_ajena'::text, NULL::uuid; RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM usuarios WHERE lower(correo) = lower(btrim(p_correo))) THEN
        RETURN QUERY SELECT 'correo_ocupado'::text, NULL::uuid; RETURN;
    END IF;

    INSERT INTO usuarios (cuenta_id, correo, nombre, clave_hash, nivel_cuenta, ve_todas)
    VALUES (v_cuenta, btrim(p_correo), btrim(p_nombre),
            'sin-clave$' || encode(gen_random_bytes(16), 'hex'),
            p_nivel, COALESCE(p_ve_todas, false))
    RETURNING id INTO v_nuevo;

    IF NOT COALESCE(p_ve_todas, false) THEN
        INSERT INTO usuario_empresa (usuario_id, empresa_id)
        SELECT v_nuevo, x.id FROM unnest(COALESCE(p_empresas, '{}')) x(id)
        ON CONFLICT DO NOTHING;
    END IF;

    PERFORM ajustar_roles_de_nivel(v_nuevo);

    INSERT INTO tokens_un_uso (usuario_id, proposito, token_hash, expira_en, creado_por)
    VALUES (v_nuevo, 'invitacion', p_token_hash, p_expira_en, v_yo);

    PERFORM registrar_evento('otro'::text, true, v_nuevo, NULL, btrim(p_correo),
        jsonb_build_object('accion', 'colaborador_invitado', 'nivel', p_nivel));

    RETURN QUERY SELECT 'ok'::text, v_nuevo;
END $$;


-- Cambiar nivel y empresas. El dueño a cualquiera; el colaborador
-- administrador solo a colaboradores, y sin subirlos de nivel. Nadie se
-- edita a sí mismo ni al dueño.
-- ok · sin_permiso · no_existe · nivel · empresa_ajena
CREATE OR REPLACE FUNCTION editar_colaborador(
    p_usuario  uuid,
    p_nivel    text,
    p_ve_todas boolean,
    p_empresas uuid[]
) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_yo     uuid := ctx_usuario();
    v_cuenta uuid;
    v_mio    text;
    v_suyo   text;
BEGIN
    SELECT u.cuenta_id, u.nivel_cuenta INTO v_cuenta, v_mio FROM usuarios u WHERE u.id = v_yo AND u.activo;
    SELECT u.nivel_cuenta INTO v_suyo FROM usuarios u WHERE u.id = p_usuario AND u.cuenta_id = v_cuenta;

    IF v_suyo IS NULL THEN RETURN 'no_existe'; END IF;
    IF v_mio IS NULL OR v_mio = 'colaborador' OR p_usuario = v_yo OR v_suyo = 'dueno' THEN
        RETURN 'sin_permiso';
    END IF;
    IF p_nivel NOT IN ('colaborador_admin', 'colaborador')
       OR (v_mio <> 'dueno' AND (v_suyo <> 'colaborador' OR p_nivel <> 'colaborador')) THEN
        RETURN 'nivel';
    END IF;
    IF EXISTS (SELECT 1 FROM unnest(COALESCE(p_empresas, '{}')) x(id)
                WHERE NOT EXISTS (SELECT 1 FROM empresas e WHERE e.id = x.id AND e.cuenta_id = v_cuenta)) THEN
        RETURN 'empresa_ajena';
    END IF;

    UPDATE usuarios SET nivel_cuenta = p_nivel, ve_todas = COALESCE(p_ve_todas, false)
     WHERE id = p_usuario;

    UPDATE usuario_empresa SET activo = false
     WHERE usuario_id = p_usuario AND activo
       AND (COALESCE(p_ve_todas, false) OR NOT (empresa_id = ANY (COALESCE(p_empresas, '{}'))));

    IF NOT COALESCE(p_ve_todas, false) THEN
        INSERT INTO usuario_empresa (usuario_id, empresa_id)
        SELECT p_usuario, x.id FROM unnest(COALESCE(p_empresas, '{}')) x(id)
        ON CONFLICT (usuario_id, empresa_id) DO UPDATE SET activo = true;
    END IF;

    PERFORM ajustar_roles_de_nivel(p_usuario);

    PERFORM registrar_evento('otro'::text, true, p_usuario, NULL, NULL,
        jsonb_build_object('accion', 'colaborador_editado', 'nivel', p_nivel));

    RETURN 'ok';
END $$;


-- Solo el dueño quita colaboradores. Se desactiva (no se borra) y se le
-- cierran las sesiones. ok · sin_permiso · no_existe
CREATE OR REPLACE FUNCTION quitar_colaborador(p_usuario uuid)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_yo     uuid := ctx_usuario();
    v_cuenta uuid;
    v_mio    text;
BEGIN
    SELECT u.cuenta_id, u.nivel_cuenta INTO v_cuenta, v_mio FROM usuarios u WHERE u.id = v_yo AND u.activo;

    IF v_mio IS DISTINCT FROM 'dueno' OR p_usuario = v_yo THEN RETURN 'sin_permiso'; END IF;

    IF NOT EXISTS (SELECT 1 FROM usuarios u
                    WHERE u.id = p_usuario AND u.cuenta_id = v_cuenta
                      AND u.nivel_cuenta IN ('colaborador_admin', 'colaborador')) THEN
        RETURN 'no_existe';
    END IF;

    UPDATE usuarios SET activo = false WHERE id = p_usuario;

    UPDATE sesiones SET revocada_en = now(), motivo_revocacion = 'colaborador quitado de la cuenta'
     WHERE sesiones.usuario_id = p_usuario AND revocada_en IS NULL;

    UPDATE tokens_un_uso SET anulado_en = now()
     WHERE tokens_un_uso.usuario_id = p_usuario AND usado_en IS NULL AND anulado_en IS NULL;

    PERFORM registrar_evento('otro'::text, true, p_usuario, NULL, NULL,
        jsonb_build_object('accion', 'colaborador_quitado'));

    RETURN 'ok';
END $$;


-- =============================================================================
-- 8. «QUITAR DE MI CARTERA» = EN PAUSA, Y REANUDAR
--
-- Solo el dueño. La empresa no emite (nadie la puede abrir; sus usuarios de
-- empresa no entran) y no se borra nada. Devuelve a quién avisar: el correo
-- del titular, para que la plataforma le escriba.
-- =============================================================================

CREATE OR REPLACE FUNCTION pausar_empresa(p_empresa uuid, p_motivo text, p_ruc text)
RETURNS TABLE (resultado text, correo_titular text, nombre text)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_yo     uuid := ctx_usuario();
    v_cuenta uuid;
    v_mio    text;
    v_e      record;
BEGIN
    SELECT u.cuenta_id, u.nivel_cuenta INTO v_cuenta, v_mio FROM usuarios u WHERE u.id = v_yo AND u.activo;

    IF v_mio IS DISTINCT FROM 'dueno' THEN
        RETURN QUERY SELECT 'sin_permiso'::text, NULL::text, NULL::text; RETURN;
    END IF;

    SELECT e.id, e.ruc, e.nombre, e.correo_titular, e.pausada_en INTO v_e
      FROM empresas e WHERE e.id = p_empresa AND e.cuenta_id = v_cuenta AND e.activo
       FOR UPDATE;

    IF NOT FOUND THEN RETURN QUERY SELECT 'no_existe'::text, NULL::text, NULL::text; RETURN; END IF;
    IF v_e.pausada_en IS NOT NULL THEN RETURN QUERY SELECT 'ya_pausada'::text, NULL::text, NULL::text; RETURN; END IF;
    IF v_e.ruc IS DISTINCT FROM btrim(p_ruc) THEN RETURN QUERY SELECT 'ruc'::text, NULL::text, NULL::text; RETURN; END IF;
    IF length(btrim(COALESCE(p_motivo, ''))) NOT BETWEEN 10 AND 500 THEN
        RETURN QUERY SELECT 'motivo'::text, NULL::text, NULL::text; RETURN;
    END IF;

    UPDATE empresas
       SET pausada_en = now(), motivo_pausa = btrim(p_motivo), pausada_por = v_yo
     WHERE id = p_empresa;

    -- Quien la tenía abierta, se queda en su bandeja.
    UPDATE usuarios SET empresa_id = NULL
     WHERE empresa_id = p_empresa AND nivel_cuenta IS NOT NULL;

    PERFORM registrar_evento('otro'::text, true, NULL, p_empresa, NULL,
        jsonb_build_object('accion', 'empresa_en_pausa', 'motivo', btrim(p_motivo)));

    RETURN QUERY SELECT 'ok'::text, v_e.correo_titular, v_e.nombre;
END $$;


CREATE OR REPLACE FUNCTION reanudar_empresa(p_empresa uuid)
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_yo     uuid := ctx_usuario();
    v_cuenta uuid;
    v_mio    text;
BEGIN
    SELECT u.cuenta_id, u.nivel_cuenta INTO v_cuenta, v_mio FROM usuarios u WHERE u.id = v_yo AND u.activo;

    IF v_mio IS DISTINCT FROM 'dueno' THEN RETURN 'sin_permiso'; END IF;

    UPDATE empresas SET pausada_en = NULL, motivo_pausa = NULL, pausada_por = NULL
     WHERE id = p_empresa AND cuenta_id = v_cuenta AND activo AND pausada_en IS NOT NULL;

    IF NOT FOUND THEN RETURN 'no_existe'; END IF;

    PERFORM registrar_evento('otro'::text, true, NULL, p_empresa, NULL,
        jsonb_build_object('accion', 'empresa_reanudada'));

    RETURN 'ok';
END $$;


-- =============================================================================
-- 9. CUENTA NEGOCIO → CUENTA PROFESIONAL
-- =============================================================================

-- ok · sin_permiso · ya_es
CREATE OR REPLACE FUNCTION convertir_a_profesional()
RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_yo     uuid := ctx_usuario();
    v_cuenta uuid;
    v_mio    text;
    v_tipo   text;
BEGIN
    SELECT u.cuenta_id, u.nivel_cuenta INTO v_cuenta, v_mio FROM usuarios u WHERE u.id = v_yo AND u.activo;
    IF v_mio IS DISTINCT FROM 'dueno' THEN RETURN 'sin_permiso'; END IF;

    SELECT tipo INTO v_tipo FROM cuentas WHERE id = v_cuenta FOR UPDATE;
    IF v_tipo = 'profesional' THEN RETURN 'ya_es'; END IF;

    UPDATE cuentas SET tipo = 'profesional' WHERE id = v_cuenta;

    PERFORM registrar_evento('otro'::text, true, v_yo, NULL, NULL,
        jsonb_build_object('accion', 'cuenta_convertida_a_profesional'));

    RETURN 'ok';
END $$;


GRANT EXECUTE ON FUNCTION puede_abrir(uuid, uuid)                TO plataforma_app;
GRANT EXECUTE ON FUNCTION abrir_empresa(uuid)                    TO plataforma_app;
GRANT EXECUTE ON FUNCTION mis_empresas()                         TO plataforma_app;
GRANT EXECUTE ON FUNCTION colaboradores()                        TO plataforma_app;
GRANT EXECUTE ON FUNCTION invitar_colaborador(text, text, text, boolean, uuid[], bytea, timestamptz) TO plataforma_app;
GRANT EXECUTE ON FUNCTION editar_colaborador(uuid, text, boolean, uuid[]) TO plataforma_app;
GRANT EXECUTE ON FUNCTION quitar_colaborador(uuid)               TO plataforma_app;
GRANT EXECUTE ON FUNCTION pausar_empresa(uuid, text, text)       TO plataforma_app;
GRANT EXECUTE ON FUNCTION reanudar_empresa(uuid)                 TO plataforma_app;
GRANT EXECUTE ON FUNCTION convertir_a_profesional()              TO plataforma_app;

-- ajustar_roles_de_nivel es interna: solo la llaman las otras funciones.
REVOKE ALL ON FUNCTION ajustar_roles_de_nivel(uuid) FROM PUBLIC;


-- =============================================================================
-- COMPROBACIONES
-- =============================================================================

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM tablas_sin_vigilar) THEN
        RAISE EXCEPTION 'Quedó una tabla sin vigilar.';
    END IF;

    IF EXISTS (SELECT 1 FROM cuentas c WHERE c.origen = 'registro'
                  AND EXISTS (SELECT 1 FROM usuarios u WHERE u.cuenta_id = c.id)
                  AND NOT EXISTS (SELECT 1 FROM usuarios u
                                   WHERE u.cuenta_id = c.id AND u.nivel_cuenta = 'dueno')) THEN
        RAISE EXCEPTION 'Hay una cuenta de registro sin dueño.';
    END IF;
END $$;


INSERT INTO migraciones_aplicadas (nombre) VALUES ('018_cartera');

COMMIT;