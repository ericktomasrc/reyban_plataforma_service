-- =============================================================================
-- 017 — LA EMPRESA PROPIA (fase 2: el asistente de configuración)
--
-- Diseño: cartera-diseno.md (fase 2) y el lienzo «Fase 2 — Asistente de
-- configuración».
--
-- Hasta hoy una empresa solo la creaba el super administrador. Desde la fase 2,
-- quien se registró la agrega solo, desde el asistente: «Agregar mi primera
-- empresa». La plataforma crea el emisor en el servicio (con la clave de alta,
-- igual que el alta del super administrador) y aquí se guarda todo de una vez.
--
-- POR QUÉ FUNCIONES Y NO EL C# DIRECTO: quien se registró no tiene empresa en
-- el contexto, así que el aislamiento no le deja insertar una empresa, ni
-- darse un rol, ni activarse un módulo (`p_empresa_modulo` solo deja al super
-- administrador). Estas tres funciones son la única puerta, y cada una
-- comprueba quién llama ANTES de hacer nada:
--
--   revisar_empresa_propia(ruc)   ¿puede agregar una? (y ¿el RUC está libre?)
--   crear_empresa_propia(...)     empresa + credencial + él como Administrador
--   activar_facturacion_propia()  el módulo, cuando el servicio dice «listo»
--
-- LO QUE NO ABREN: un usuario de una empresa del super administrador no puede
-- activarse módulos (solo las cuentas de registro), y un módulo que el super
-- administrador desactivó no se reactiva desde aquí.
--
-- Por ahora UNA empresa por usuario: la cartera (varias empresas y el selector)
-- es la fase 3.
--
-- Todo en una transacción.
-- =============================================================================

BEGIN;


-- =============================================================================
-- 1. EL CORREO DEL TITULAR
--
-- El del dueño de la empresa. En la fase 4 es la llave para recuperarla
-- («Recupérala») si la gestiona otra persona. Hoy solo se guarda.
-- =============================================================================

ALTER TABLE empresas ADD COLUMN correo_titular text;

ALTER TABLE empresas ADD CONSTRAINT ck_empresas_correo_titular CHECK (
    correo_titular IS NULL
    OR (length(correo_titular) BETWEEN 3 AND 254
        AND correo_titular ~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$'));


-- =============================================================================
-- 2. ¿PUEDE AGREGAR UNA EMPRESA?
--
-- Devuelve un código, no lanza: la pantalla lo traduce.
--
--   ok · sin_usuario · super_admin · ya_tiene_empresa · sin_cuenta ·
--   cuenta_no_activa · tope · ruc_ocupado
--
-- El RUC se mira en TODA la plataforma (por eso SECURITY DEFINER): solo
-- devuelve «ocupado», nunca de quién es.
-- =============================================================================

CREATE OR REPLACE FUNCTION revisar_empresa_propia(p_ruc text)
RETURNS text
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = public AS $$
DECLARE
    v_usuario  uuid := ctx_usuario();
    v_empresa  uuid;
    v_cuenta   uuid;
    v_super    boolean;
    v_activo   boolean;
    v_estado   text;
    v_tope     integer;
    v_hoy      integer;
BEGIN
    SELECT u.empresa_id, u.cuenta_id, u.es_super_admin, u.activo
      INTO v_empresa, v_cuenta, v_super, v_activo
      FROM usuarios u
     WHERE u.id = v_usuario;

    IF NOT FOUND OR NOT v_activo THEN RETURN 'sin_usuario'; END IF;
    IF v_super                   THEN RETURN 'super_admin'; END IF;
    IF v_empresa IS NOT NULL     THEN RETURN 'ya_tiene_empresa'; END IF;
    IF v_cuenta IS NULL          THEN RETURN 'sin_cuenta'; END IF;

    SELECT c.estado, c.tope_empresas_dia INTO v_estado, v_tope
      FROM cuentas c WHERE c.id = v_cuenta;

    IF v_estado IS DISTINCT FROM 'activa' THEN RETURN 'cuenta_no_activa'; END IF;

    -- El tope de la cuenta: empresas nuevas en las últimas 24 horas.
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

COMMENT ON FUNCTION revisar_empresa_propia IS
    'SECURITY DEFINER: mira si quien llama (ctx_usuario) puede agregar su empresa y si el RUC esta libre en toda la plataforma. Solo devuelve un codigo.';


-- =============================================================================
-- 3. CREAR LA EMPRESA PROPIA
--
-- La llama la plataforma DESPUÉS de que el servicio creó el emisor (con su
-- clave fac_ y su webhook), con los dos secretos ya cifrados. En una sola
-- pasada:
--
--   · la empresa, en la cuenta de quien llama, con el RUC y la razón social;
--   · él pasa a ser de esa empresa, con el rol Administrador;
--   · la credencial del servicio, marcada como creada por el alta.
--
-- Vuelve a revisar todo con el usuario bloqueado (FOR UPDATE): dos pestañas
-- a la vez no crean dos empresas. Devuelve 'ok' o el código de la revisión.
-- =============================================================================

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
    v_res     text;
BEGIN
    -- El candado primero; la revisión, con él puesto.
    SELECT u.cuenta_id INTO v_cuenta FROM usuarios u WHERE u.id = v_usuario FOR UPDATE;

    v_res := revisar_empresa_propia(p_ruc);
    IF v_res <> 'ok' THEN RETURN v_res; END IF;

    INSERT INTO empresas (id, cuenta_id, nombre, ruc, razon_social, nombre_comercial,
                          ambiente, datos_oficiales_en, correo_titular)
    VALUES (p_id, v_cuenta, btrim(p_nombre), btrim(p_ruc), btrim(p_razon_social),
            NULLIF(btrim(p_nombre_comercial), ''), COALESCE(p_ambiente, 'beta'), now(),
            NULLIF(btrim(p_correo_titular), ''));

    UPDATE usuarios SET empresa_id = p_id WHERE id = v_usuario;

    INSERT INTO usuario_rol (usuario_id, rol_id)
    VALUES (v_usuario, '11111111-1111-1111-1111-111111111111')
    ON CONFLICT (usuario_id, rol_id) DO UPDATE SET activo = true;

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

COMMENT ON FUNCTION crear_empresa_propia IS
    'SECURITY DEFINER: quien se registro no tiene empresa en el contexto. Crea su empresa, lo hace Administrador y guarda la credencial del servicio. Revisa todo antes.';


-- =============================================================================
-- 4. ACTIVAR FACTURACIÓN DESDE EL ASISTENTE
--
-- La plataforma ya preguntó al servicio (listo.paraEmitir) antes de llamar.
-- Aquí se comprueba quién: un usuario de la empresa del contexto, con el
-- permiso de configurar la facturación, y de una cuenta de REGISTRO.
--
--   ok · ya_activo · sin_empresa · sin_permiso · no_es_autoservicio ·
--   desactivado_por_admin
-- =============================================================================

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
         WHERE u.id = v_usuario AND u.empresa_id = v_empresa AND u.activo)
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

COMMENT ON FUNCTION activar_facturacion_propia IS
    'SECURITY DEFINER: p_empresa_modulo solo deja escribir al super administrador. Activa Facturacion para la empresa del contexto si quien llama la administra y su cuenta es de registro. No reactiva un modulo desactivado.';


GRANT EXECUTE ON FUNCTION revisar_empresa_propia(text) TO plataforma_app;
GRANT EXECUTE ON FUNCTION crear_empresa_propia(uuid, text, text, text, text, text, text, text, bytea, text, bytea, text) TO plataforma_app;
GRANT EXECUTE ON FUNCTION activar_facturacion_propia() TO plataforma_app;


INSERT INTO migraciones_aplicadas (nombre) VALUES ('017_empresa_propia');

COMMIT;