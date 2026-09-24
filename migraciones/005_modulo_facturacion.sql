-- =============================================================================
-- 005 — MÓDULO DE FACTURACIÓN
--
-- LA LÍNEA QUE NO SE CRUZA:
--
--     La plataforma NO sabe de SUNAT.
--
-- No calcula IGV, no arma XML, no conoce los catálogos, no decide si una
-- operación es gratuita. Todo eso vive en el servicio de facturación y ya
-- está probado contra el ambiente beta, documento por documento.
--
-- Lo que hay aquí es: qué escribió el usuario, qué se le mandó al servicio, y
-- qué contestó. Nada más.
--
-- LOS TOTALES DE ESTAS TABLAS SON UNA COPIA INFORMATIVA. La cifra que se
-- firmó y viajó a SUNAT es la que devolvió el servicio. Si algún día las dos
-- difieren, gana el servicio y esta fila está mal. Se guardan igualmente
-- porque un listado de comprobantes no puede hacer una llamada HTTP por cada
-- fila de la pantalla.
--
-- POR QUÉ NO HAY TABLA DE SERIES NI DE CORRELATIVOS: porque los asigna el
-- servicio, con candado, y duplicarlos aquí sería crear una segunda fuente de
-- verdad para el dato que más caro cuesta equivocar. Un correlativo duplicado
-- es un problema tributario, no un error de programa.
-- =============================================================================


-- =============================================================================
-- CLIENTES  (los receptores de los comprobantes)
-- =============================================================================

CREATE TABLE clientes (
    id             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id     uuid        NOT NULL REFERENCES empresas (id),

    -- Catálogo 06 de SUNAT: 1 DNI, 6 RUC, 4 carnet de extranjería,
    -- 7 pasaporte, 0 sin documento. Se guarda el código tal cual para que
    -- viaje sin traducción.
    tipo_documento char(1)     NOT NULL,
    numero_documento text      NOT NULL,

    razon_social   text        NOT NULL,
    nombre_comercial text,
    direccion      text,
    ubigeo         char(6),
    correo         text,
    telefono       text,

    creado_en      timestamptz NOT NULL DEFAULT now(),
    creado_por     uuid,
    modificado_en  timestamptz,
    modificado_por uuid,
    activo         boolean     NOT NULL DEFAULT true,
    version        integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_clientes_tipo_doc CHECK (tipo_documento IN ('0','1','4','6','7','A','B','C','D','E','F','G'))
);

-- Único dentro de cada empresa, no globalmente: dos clientes tuyos pueden
-- venderle al mismo RUC y cada uno tiene su propia ficha, su propio correo
-- de contacto y su propio historial.
CREATE UNIQUE INDEX ux_clientes_documento
    ON clientes (empresa_id, tipo_documento, numero_documento) WHERE activo;

CREATE INDEX ix_clientes_busqueda
    ON clientes (empresa_id, lower(razon_social)) WHERE activo;


-- =============================================================================
-- PRODUCTOS
-- =============================================================================

CREATE TABLE productos (
    id                uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id        uuid          NOT NULL REFERENCES empresas (id),

    codigo            text          NOT NULL,
    descripcion       text          NOT NULL,

    -- Catálogo 03 de SUNAT (NIU, ZZ, KGM, ...). Va sin traducir al servicio.
    unidad_medida     text          NOT NULL DEFAULT 'NIU',

    -- Catálogo 07: 10 gravado, 20 exonerado, 30 inafecto, 40 exportación...
    tipo_afectacion   char(2)       NOT NULL DEFAULT '10',

    -- Diez decimales porque SUNAT los admite en el valor unitario, y redondear
    -- aquí produce diferencias de céntimos que luego no cuadran con el total.
    precio_unitario   numeric(18,10) NOT NULL DEFAULT 0,
    incluye_igv       boolean        NOT NULL DEFAULT true,

    -- Código del producto en el catálogo de SUNAT (opcional, catálogo 25).
    codigo_sunat      text,

    creado_en         timestamptz   NOT NULL DEFAULT now(),
    creado_por        uuid,
    modificado_en     timestamptz,
    modificado_por    uuid,
    activo            boolean       NOT NULL DEFAULT true,
    version           integer       NOT NULL DEFAULT 1,

    CONSTRAINT ck_productos_precio CHECK (precio_unitario >= 0)
);

CREATE UNIQUE INDEX ux_productos_codigo
    ON productos (empresa_id, lower(codigo)) WHERE activo;

CREATE INDEX ix_productos_busqueda
    ON productos (empresa_id, lower(descripcion)) WHERE activo;


-- =============================================================================
-- DOCUMENTOS
--
-- El registro local de cada comprobante. Nace como borrador, se envía, y a
-- partir de ahí su estado lo marca el servicio.
--
-- `comprobante_id` es el identificador que devolvió el servicio. Es el puente
-- entre los dos sistemas: con él se piden el XML, el CDR y el PDF, que NO se
-- copian aquí. Guardarlos sería tener dos originales del mismo documento
-- legal, y el original es el del servicio.
-- =============================================================================

CREATE TABLE documentos (
    id                 uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id         uuid          NOT NULL REFERENCES empresas (id),

    -- Catálogo 01: 01 factura, 03 boleta, 07 nota de crédito, 08 nota de
    -- débito, 04 liquidación de compra, 20 retención, 40 percepción...
    tipo               char(2)       NOT NULL,
    serie              text,
    correlativo        integer,

    cliente_id         uuid          NOT NULL REFERENCES clientes (id),

    fecha_emision      date          NOT NULL,
    fecha_vencimiento  date,
    moneda             char(3)       NOT NULL DEFAULT 'PEN',

    -- Para notas de crédito y débito: el documento que corrigen.
    documento_ref_id   uuid          REFERENCES documentos (id),
    motivo_nota        text,
    tipo_nota          char(2),

    -- --- Lo que devuelve el servicio ---------------------------------------
    comprobante_id     uuid,
    idempotency_key    text,

    estado             text          NOT NULL DEFAULT 'borrador' CHECK (estado IN (
                            'borrador',      -- solo existe aquí
                            'encolado',      -- aceptado por el servicio, esperando a SUNAT
                            'aceptado',
                            'observado',     -- aceptado con observaciones
                            'rechazado',     -- SUNAT lo rechazó: no se reintenta
                            'anulado',       -- baja o anulación comunicada
                            'error'          -- no se pudo ni entregar al servicio
                       )),

    codigo_sunat       text,
    mensaje_sunat      text,
    observaciones      text[],

    -- --- Totales (copia informativa, ver la cabecera del archivo) -----------
    total_gravado      numeric(18,2) NOT NULL DEFAULT 0,
    total_exonerado    numeric(18,2) NOT NULL DEFAULT 0,
    total_inafecto     numeric(18,2) NOT NULL DEFAULT 0,
    total_gratuito     numeric(18,2) NOT NULL DEFAULT 0,
    total_exportacion  numeric(18,2) NOT NULL DEFAULT 0,
    total_descuentos   numeric(18,2) NOT NULL DEFAULT 0,
    total_igv          numeric(18,2) NOT NULL DEFAULT 0,
    total_isc          numeric(18,2) NOT NULL DEFAULT 0,
    total_otros        numeric(18,2) NOT NULL DEFAULT 0,
    importe_total      numeric(18,2) NOT NULL DEFAULT 0,

    -- Lo que el usuario escribió y no es de SUNAT: orden de compra, vendedor,
    -- centro de costo. Nunca viaja al XML; si hace falta que aparezca, va en
    -- la plantilla del PDF.
    datos_extra        jsonb         NOT NULL DEFAULT '{}'::jsonb,

    creado_en          timestamptz   NOT NULL DEFAULT now(),
    creado_por         uuid,
    modificado_en      timestamptz,
    modificado_por     uuid,
    activo             boolean       NOT NULL DEFAULT true,
    version            integer       NOT NULL DEFAULT 1,

    -- Una nota necesita saber a qué documento se refiere y por qué.
    CONSTRAINT ck_documentos_nota CHECK (
        (tipo NOT IN ('07','08')) OR
        (documento_ref_id IS NOT NULL AND tipo_nota IS NOT NULL)
    )
);

-- La serie y el correlativo los pone el servicio, pero una vez puestos no se
-- pueden repetir ni aquí. Es la segunda red bajo el problema irreversible
-- número dos del documento maestro.
CREATE UNIQUE INDEX ux_documentos_numeracion
    ON documentos (empresa_id, tipo, serie, correlativo)
    WHERE serie IS NOT NULL AND correlativo IS NOT NULL;

CREATE UNIQUE INDEX ux_documentos_comprobante
    ON documentos (comprobante_id) WHERE comprobante_id IS NOT NULL;

-- La clave de idempotencia es lo que impide que un doble clic o un reintento
-- del navegador emitan dos veces la misma factura.
CREATE UNIQUE INDEX ux_documentos_idempotencia
    ON documentos (empresa_id, idempotency_key) WHERE idempotency_key IS NOT NULL;

CREATE INDEX ix_documentos_listado
    ON documentos (empresa_id, fecha_emision DESC, creado_en DESC);
CREATE INDEX ix_documentos_cliente ON documentos (cliente_id, fecha_emision DESC);
CREATE INDEX ix_documentos_estado  ON documentos (empresa_id, estado)
    WHERE estado IN ('borrador', 'encolado', 'error');
CREATE INDEX ix_documentos_extra   ON documentos USING gin (datos_extra);


-- =============================================================================
-- LÍNEAS DEL DOCUMENTO
--
-- Se copian los datos del producto en vez de solo referenciarlo. Es
-- deliberado: si dentro de un año alguien corrige la descripción o el precio
-- del producto, la factura emitida no puede cambiar. Lo que se facturó es lo
-- que decía el papel ese día.
-- =============================================================================

CREATE TABLE documento_lineas (
    id                uuid           PRIMARY KEY DEFAULT gen_random_uuid(),
    documento_id      uuid           NOT NULL REFERENCES documentos (id) ON DELETE CASCADE,
    empresa_id        uuid           NOT NULL REFERENCES empresas (id),

    numero            integer        NOT NULL,

    producto_id       uuid           REFERENCES productos (id),
    codigo            text,
    descripcion       text           NOT NULL,
    unidad_medida     text           NOT NULL DEFAULT 'NIU',

    cantidad          numeric(18,10) NOT NULL,
    valor_unitario    numeric(18,10) NOT NULL,
    precio_unitario   numeric(18,10) NOT NULL,
    descuento         numeric(18,2)  NOT NULL DEFAULT 0,

    tipo_afectacion   char(2)        NOT NULL DEFAULT '10',
    igv               numeric(18,2)  NOT NULL DEFAULT 0,
    isc               numeric(18,2)  NOT NULL DEFAULT 0,
    otros_tributos    numeric(18,2)  NOT NULL DEFAULT 0,

    valor_venta       numeric(18,2)  NOT NULL DEFAULT 0,
    importe_total     numeric(18,2)  NOT NULL DEFAULT 0,

    creado_en         timestamptz    NOT NULL DEFAULT now(),
    creado_por        uuid,
    modificado_en     timestamptz,
    modificado_por    uuid,
    activo            boolean        NOT NULL DEFAULT true,
    version           integer        NOT NULL DEFAULT 1,

    CONSTRAINT ck_lineas_cantidad CHECK (cantidad > 0)
);

CREATE UNIQUE INDEX ux_documento_lineas_numero
    ON documento_lineas (documento_id, numero);
CREATE INDEX ix_documento_lineas_doc ON documento_lineas (documento_id);


-- =============================================================================
-- HISTORIAL DEL DOCUMENTO
--
-- La bitácora general ya registra cada UPDATE de la tabla `documentos`. ¿Por
-- qué entonces este historial?
--
-- Porque son dos preguntas distintas:
--
--   la bitácora     «qué columnas cambiaron en esta fila y quién las tocó»
--   este historial  «por qué este comprobante está rechazado, y cuándo dejó
--                    de estar encolado»
--
-- La primera es forense: se lee cuando algo pasó y hay que reconstruirlo. La
-- segunda se muestra en pantalla, al cliente, en la ficha del documento. Una
-- no sustituye a la otra, y una fila de bitácora con dos JSONB dentro no se
-- le puede enseñar a nadie.
--
-- SOLO-INSERCIÓN.
-- =============================================================================

CREATE TABLE documento_historial (
    id              bigserial   PRIMARY KEY,
    documento_id    uuid        NOT NULL REFERENCES documentos (id),
    empresa_id      uuid        NOT NULL REFERENCES empresas (id),

    ocurrido_en     timestamptz NOT NULL DEFAULT now(),

    estado_anterior text,
    estado_nuevo    text        NOT NULL,

    -- De dónde vino el cambio. `webhook` es el caso normal en producción:
    -- lo avisa el servicio cuando SUNAT contesta.
    origen          text        NOT NULL CHECK (origen IN (
                                    'usuario', 'webhook', 'consulta', 'sistema'
                                )),
    usuario_id      uuid,

    codigo_sunat    text,
    mensaje         text,
    detalle         jsonb
);

CREATE INDEX ix_documento_historial_doc
    ON documento_historial (documento_id, ocurrido_en DESC);

CREATE TRIGGER documento_historial_inmutable
    BEFORE UPDATE OR DELETE ON documento_historial
    FOR EACH ROW EXECUTE FUNCTION fn_solo_insercion();


-- --- El historial se escribe solo --------------------------------------------
--
-- Un disparador, no una llamada del código. El estado de un documento cambia
-- desde tres sitios distintos —la pantalla, el webhook, la consulta manual— y
-- mañana habrá un cuarto. Si cada uno tuviera que acordarse de escribir el
-- historial, alguno se olvidaría.
--
-- Es la misma decisión que ya se tomó en el servicio para los eventos de
-- webhook, y por el mismo motivo.

CREATE OR REPLACE FUNCTION fn_historial_documento() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        INSERT INTO documento_historial
            (documento_id, empresa_id, estado_anterior, estado_nuevo,
             origen, usuario_id, codigo_sunat, mensaje)
        VALUES
            (NEW.id, NEW.empresa_id, NULL, NEW.estado,
             CASE WHEN ctx_usuario() IS NULL THEN 'sistema' ELSE 'usuario' END,
             ctx_usuario(), NEW.codigo_sunat, NEW.mensaje_sunat);

    ELSIF NEW.estado IS DISTINCT FROM OLD.estado THEN
        INSERT INTO documento_historial
            (documento_id, empresa_id, estado_anterior, estado_nuevo,
             origen, usuario_id, codigo_sunat, mensaje)
        VALUES
            (NEW.id, NEW.empresa_id, OLD.estado, NEW.estado,
             CASE WHEN ctx_usuario() IS NULL THEN 'webhook' ELSE 'usuario' END,
             ctx_usuario(), NEW.codigo_sunat, NEW.mensaje_sunat);
    END IF;

    RETURN NULL;
END $$;

CREATE TRIGGER historial_documentos
    AFTER INSERT OR UPDATE ON documentos
    FOR EACH ROW EXECUTE FUNCTION fn_historial_documento();


-- =============================================================================
-- LLAMADAS AL SERVICIO DE FACTURACIÓN
--
-- Cada petición HTTP que la plataforma le hace al servicio, con lo que mandó
-- y lo que le contestaron.
--
-- POR QUÉ GUARDAR ESTO, si el servicio ya tiene su propia bitácora: porque el
-- día que un cliente diga «yo emití esa factura y no salió», la discusión es
-- entre dos sistemas. Sin este registro, la plataforma no puede demostrar si
-- llegó a pedirlo o si se quedó en el camino.
--
-- LO QUE NUNCA ENTRA AQUÍ: la clave de API. El código debe quitarla de la
-- cabecera antes de guardar. Una tabla de depuración con las llaves de todos
-- los clientes dentro es peor que no tener tabla.
--
-- SOLO-INSERCIÓN, y conviene purgarla: a los 90 días el cuerpo de las
-- peticiones viejas ya no le sirve a nadie y ocupa lo que más pesa.
-- =============================================================================

CREATE TABLE llamadas_facturacion (
    id             bigserial   PRIMARY KEY,
    empresa_id     uuid        NOT NULL REFERENCES empresas (id),
    documento_id   uuid        REFERENCES documentos (id),

    ocurrido_en    timestamptz NOT NULL DEFAULT now(),

    metodo         text        NOT NULL,
    ruta           text        NOT NULL,
    idempotency_key text,

    codigo_http    integer,
    duracion_ms    integer,

    peticion       jsonb,
    respuesta      jsonb,
    error          text,

    usuario_id     uuid
);

CREATE INDEX ix_llamadas_empresa ON llamadas_facturacion (empresa_id, ocurrido_en DESC);
CREATE INDEX ix_llamadas_doc     ON llamadas_facturacion (documento_id, ocurrido_en DESC);
CREATE INDEX ix_llamadas_fallos  ON llamadas_facturacion (ocurrido_en DESC)
    WHERE codigo_http IS NULL OR codigo_http >= 400;

CREATE TRIGGER llamadas_facturacion_inmutable
    BEFORE UPDATE OR DELETE ON llamadas_facturacion
    FOR EACH ROW EXECUTE FUNCTION fn_solo_insercion();


-- =============================================================================
-- WEBHOOKS RECIBIDOS
--
-- El servicio avisa por webhook cuando SUNAT contesta. Se guarda cada aviso
-- tal cual llegó, ANTES de procesarlo y aunque la firma no valide.
--
-- Guardar también los que fallan la firma es lo que permite distinguir «el
-- servicio no avisó» de «avisó y lo rechazamos», que son dos problemas
-- completamente distintos y se investigan en sitios distintos.
--
-- `evento_id` con índice único es la defensa contra el reenvío: si el
-- servicio reintenta el mismo aviso, el segundo choca contra el índice y no
-- se procesa dos veces.
-- =============================================================================

CREATE TABLE webhooks_recibidos (
    id             bigserial   PRIMARY KEY,
    recibido_en    timestamptz NOT NULL DEFAULT now(),

    empresa_id     uuid        REFERENCES empresas (id),
    evento_id      text,
    tipo_evento    text,

    firma_valida   boolean     NOT NULL,
    ip             inet,

    cuerpo         jsonb       NOT NULL,

    procesado_en   timestamptz,
    error_proceso  text,
    documento_id   uuid        REFERENCES documentos (id)
);

CREATE UNIQUE INDEX ux_webhooks_evento
    ON webhooks_recibidos (evento_id) WHERE evento_id IS NOT NULL;
CREATE INDEX ix_webhooks_pendientes
    ON webhooks_recibidos (recibido_en) WHERE procesado_en IS NULL;
CREATE INDEX ix_webhooks_invalidos
    ON webhooks_recibidos (recibido_en DESC) WHERE NOT firma_valida;

-- Aquí sí se permite UPDATE, pero solo para marcar el resultado del proceso.
-- El cuerpo recibido no se toca nunca.
CREATE OR REPLACE FUNCTION fn_webhook_cuerpo_inmutable() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.cuerpo IS DISTINCT FROM OLD.cuerpo
       OR NEW.firma_valida IS DISTINCT FROM OLD.firma_valida
       OR NEW.recibido_en  IS DISTINCT FROM OLD.recibido_en THEN
        RAISE EXCEPTION 'El aviso recibido no se puede modificar, solo marcar como procesado.';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER webhooks_cuerpo_inmutable
    BEFORE UPDATE ON webhooks_recibidos
    FOR EACH ROW EXECUTE FUNCTION fn_webhook_cuerpo_inmutable();

CREATE TRIGGER webhooks_sin_borrado
    BEFORE DELETE ON webhooks_recibidos
    FOR EACH ROW EXECUTE FUNCTION fn_solo_insercion();


-- =============================================================================
-- AISLAMIENTO Y VIGILANCIA
-- =============================================================================

ALTER TABLE clientes             ENABLE ROW LEVEL SECURITY;
ALTER TABLE productos            ENABLE ROW LEVEL SECURITY;
ALTER TABLE documentos           ENABLE ROW LEVEL SECURITY;
ALTER TABLE documento_lineas     ENABLE ROW LEVEL SECURITY;
ALTER TABLE documento_historial  ENABLE ROW LEVEL SECURITY;
ALTER TABLE llamadas_facturacion ENABLE ROW LEVEL SECURITY;
ALTER TABLE webhooks_recibidos   ENABLE ROW LEVEL SECURITY;

ALTER TABLE clientes             FORCE ROW LEVEL SECURITY;
ALTER TABLE productos            FORCE ROW LEVEL SECURITY;
ALTER TABLE documentos           FORCE ROW LEVEL SECURITY;
ALTER TABLE documento_lineas     FORCE ROW LEVEL SECURITY;
ALTER TABLE documento_historial  FORCE ROW LEVEL SECURITY;
ALTER TABLE llamadas_facturacion FORCE ROW LEVEL SECURITY;

-- Los webhooks llegan SIN sesión: el que llama es el servicio, no una
-- persona. Por eso esta tabla queda fuera del filtro por empresa; su defensa
-- es la firma HMAC, que se comprueba antes de hacer nada con el contenido.
ALTER TABLE webhooks_recibidos   FORCE ROW LEVEL SECURITY;

CREATE POLICY p_clientes ON clientes
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());

CREATE POLICY p_productos ON productos
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());

CREATE POLICY p_documentos ON documentos
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());

CREATE POLICY p_documento_lineas ON documento_lineas
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa());

CREATE POLICY p_documento_historial ON documento_historial
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (true);

CREATE POLICY p_llamadas_facturacion ON llamadas_facturacion
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (true);

CREATE POLICY p_webhooks_recibidos ON webhooks_recibidos
    USING (true) WITH CHECK (true);

SELECT vigilar_tabla('clientes');
SELECT vigilar_tabla('productos');
SELECT vigilar_tabla('documentos');
SELECT vigilar_tabla('documento_lineas');
SELECT vigilar_tabla_inmutable('documento_historial');
SELECT vigilar_tabla_inmutable('llamadas_facturacion');

-- El cuerpo de los webhooks ya se guarda entero en su propia tabla inmutable.
-- Copiarlo otra vez a la bitácora sería duplicar el dato más voluminoso del
-- sistema para no aprender nada nuevo.
CREATE TRIGGER auditar_webhooks_recibidos
    AFTER INSERT OR UPDATE ON webhooks_recibidos
    FOR EACH ROW EXECUTE FUNCTION fn_auditar('cuerpo');


GRANT SELECT, INSERT, UPDATE ON clientes, productos, documentos, documento_lineas
    TO plataforma_app;
GRANT SELECT, INSERT ON documento_historial, llamadas_facturacion TO plataforma_app;
GRANT SELECT, INSERT, UPDATE ON webhooks_recibidos TO plataforma_app;
GRANT USAGE ON SEQUENCE documento_historial_id_seq,
                        llamadas_facturacion_id_seq,
                        webhooks_recibidos_id_seq TO plataforma_app;

INSERT INTO migraciones_aplicadas (nombre) VALUES ('005_modulo_facturacion');
