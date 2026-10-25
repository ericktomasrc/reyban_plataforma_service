-- =============================================================================
-- 052 — INVENTARIO POR SUCURSAL (e160, 24 oct 2026, diseño aprobado el 24 oct:
--       lienzo «Reyban — Inventario por sucursal»)
--
-- Cuánto hay de cada producto en cada sucursal (la sucursal es su almacén):
--
--   físico      lo que hay en el almacén ahora
--   reservado   lo facturado «con entrega después», que espera su guía
--   disponible  físico − reservado: lo que se puede vender
--   en tránsito lo que salió de otra sucursal con guía 04 y aún no llega
--
-- Lo que trae:
--
--   · inventario_config     por empresa: encendido o no, el modo (venta directa
--                           o con despacho), «permitir vender sin stock»
--                           (apagado) y el aviso de mínimos.
--   · inv_sucursales        por sucursal: cuándo se cargó el stock inicial y
--                           desde cuándo SE CONTROLA (bloquea). Antes de
--                           «Empezar a controlar» todo se anota y solo avisa.
--   · inv_stock             el saldo por producto y sucursal (y su mínimo).
--   · inv_movimientos       EL KARDEX: cada entrada y salida con el documento
--                           que la causó. Solo inserción: nada se edita ni se
--                           borra; un error se corrige con otro movimiento.
--   · inv_reservas          lo reservado por cada factura «se entrega después»
--                           y cuánto ya despacharon sus guías.
--   · inv_aplicado          si un comprobante o una guía ya movió el stock
--                           (para que mover y revertir sea idempotente).
--   · inv_operaciones (+ _lineas)
--                           ingresos por compra, ajustes, stock inicial y
--                           conteos físicos. Numeradas por empresa (IN-0001,
--                           AJ-0001, SI-0001, CF-0001).
--   · inv_traslados (+ _lineas)
--                           cada guía 04 entre sucursales: en tránsito hasta
--                           el «Recibí» del destino, con lo que faltó y por qué.
--   · proveedores, producto_categorias
--                           las maestras nuevas.
--   · productos.controla_stock (marcado en los bienes, nunca en servicios ZZ)
--     y productos.categoria_id.
--   · documentos.entrega_despues / inv_retorno / inv_sucursal_id
--     guias.inv_origen_id / inv_destino_id
--   · permisos inventario.*
--
-- QUIÉN MUEVE EL STOCK: la base, no la API. Un comprobante cambia de estado
-- desde la pantalla, el aviso del servicio y la consulta manual; si cada sitio
-- tuviera que acordarse de mover el stock, alguno se olvidaría (como con el
-- historial de documentos). Las reglas, en inv_sync_documento / inv_sync_guia.
--
-- PREPARADO PARA DESPUÉS (Inventario ampliado, D1–D6): los movimientos tienen
-- variante_id, lote y serie (vacíos hoy) y el costo unitario de cada compra
-- ya se guarda para el inventario valorizado.
--
-- Nada se borra. Se puede correr dos veces.
-- =============================================================================

BEGIN;

-- --- Maestras: categorías y proveedores ---------------------------------------------

CREATE TABLE IF NOT EXISTS producto_categorias (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id      uuid        NOT NULL REFERENCES empresas (id),
    nombre          text        NOT NULL,

    creado_en       timestamptz NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean     NOT NULL DEFAULT true,
    version         integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_producto_categorias_nombre CHECK (length(btrim(nombre)) BETWEEN 1 AND 60)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_producto_categorias_nombre
    ON producto_categorias (empresa_id, lower(btrim(nombre))) WHERE activo;

CREATE TABLE IF NOT EXISTS proveedores (
    id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id        uuid        NOT NULL REFERENCES empresas (id),
    tipo_documento    char(1)     NOT NULL DEFAULT '6',
    numero_documento  text        NOT NULL,
    razon_social      text        NOT NULL,
    direccion         text,
    correo            text,
    telefono          text,

    creado_en         timestamptz NOT NULL DEFAULT now(),
    creado_por        uuid,
    modificado_en     timestamptz,
    modificado_por    uuid,
    activo            boolean     NOT NULL DEFAULT true,
    version           integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_proveedores_tipo   CHECK (tipo_documento IN ('6', '1', '4', '7', '0')),
    CONSTRAINT ck_proveedores_numero CHECK (
        (tipo_documento = '6' AND numero_documento ~ '^(10|15|17|20)[0-9]{9}$') OR
        (tipo_documento = '1' AND numero_documento ~ '^[0-9]{8}$') OR
        (tipo_documento NOT IN ('6', '1') AND numero_documento ~ '^[A-Z0-9-]{1,15}$')),
    CONSTRAINT ck_proveedores_nombre CHECK (length(btrim(razon_social)) BETWEEN 1 AND 200)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_proveedores_documento ON proveedores (empresa_id, tipo_documento, numero_documento);

-- --- productos: controla stock y categoría ---------------------------------------------

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'productos' AND column_name = 'controla_stock') THEN
        ALTER TABLE productos ADD COLUMN controla_stock boolean NOT NULL DEFAULT true;
        -- Los servicios no tienen stock. Solo la primera vez: después, lo que
        -- cada empresa desmarque se respeta.
        UPDATE productos SET controla_stock = false WHERE unidad_medida = 'ZZ';
    END IF;
END $$;

ALTER TABLE productos ADD COLUMN IF NOT EXISTS categoria_id uuid REFERENCES producto_categorias (id);

COMMENT ON COLUMN productos.controla_stock IS '052: se cuenta en el inventario. Marcado de fábrica en los bienes; un servicio (ZZ) nunca.';
COMMENT ON COLUMN productos.categoria_id IS '052: categoría para filtrar stock, productos y reportes (opcional).';

-- Un servicio nunca controla stock, lo escriba quien lo escriba.
CREATE OR REPLACE FUNCTION fn_producto_servicio_sin_stock() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.unidad_medida = 'ZZ' THEN NEW.controla_stock := false; END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS producto_servicio_sin_stock ON productos;
CREATE TRIGGER producto_servicio_sin_stock
    BEFORE INSERT OR UPDATE OF unidad_medida, controla_stock ON productos
    FOR EACH ROW EXECUTE FUNCTION fn_producto_servicio_sin_stock();

-- --- documentos y guías: lo que el inventario necesita saber -----------------------------

ALTER TABLE documentos ADD COLUMN IF NOT EXISTS entrega_despues boolean;
ALTER TABLE documentos ADD COLUMN IF NOT EXISTS inv_retorno     text;
ALTER TABLE documentos ADD COLUMN IF NOT EXISTS inv_sucursal_id uuid REFERENCES sucursales (id);

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_documentos_inv_retorno') THEN
        ALTER TABLE documentos ADD CONSTRAINT ck_documentos_inv_retorno
            CHECK (inv_retorno IS NULL OR inv_retorno IN ('volvio', 'danada', 'no_volvio'));
    END IF;
END $$;

COMMENT ON COLUMN documentos.entrega_despues IS '052: «se entrega después (con guía)»: la factura reserva y la guía saca. Null = el modo de la empresa.';
COMMENT ON COLUMN documentos.inv_retorno IS '052: en una nota de crédito que devuelve productos, si la mercadería volvió al almacén (null = sí).';
COMMENT ON COLUMN documentos.inv_sucursal_id IS '052: a qué sucursal volvió la mercadería de la nota (null = la del comprobante).';

ALTER TABLE guias ADD COLUMN IF NOT EXISTS inv_origen_id  uuid REFERENCES sucursales (id);
ALTER TABLE guias ADD COLUMN IF NOT EXISTS inv_destino_id uuid REFERENCES sucursales (id);

COMMENT ON COLUMN guias.inv_origen_id IS '052: de qué sucursal sale la mercadería (null = la de la serie).';
COMMENT ON COLUMN guias.inv_destino_id IS '052: en un traslado entre sucursales (motivo 04), a cuál llega.';

-- --- Configuración ---------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS inventario_config (
    id                  uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id          uuid        NOT NULL REFERENCES empresas (id),
    activo_inventario   boolean     NOT NULL DEFAULT false,
    modo                text        NOT NULL DEFAULT 'directa',
    permitir_sin_stock  boolean     NOT NULL DEFAULT false,
    avisar_minimo       boolean     NOT NULL DEFAULT true,
    activado_en         timestamptz,

    creado_en           timestamptz NOT NULL DEFAULT now(),
    creado_por          uuid,
    modificado_en       timestamptz,
    modificado_por      uuid,
    activo              boolean     NOT NULL DEFAULT true,
    version             integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_inventario_config_modo CHECK (modo IN ('directa', 'despacho'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_inventario_config_empresa ON inventario_config (empresa_id);

COMMENT ON TABLE inventario_config IS '052: el inventario de cada empresa. Sin fila o apagado, la facturación funciona como siempre.';

CREATE TABLE IF NOT EXISTS inv_sucursales (
    id                uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id        uuid        NOT NULL REFERENCES empresas (id),
    sucursal_id       uuid        NOT NULL REFERENCES sucursales (id),
    stock_inicial_en  timestamptz,
    controla_desde    timestamptz,

    creado_en         timestamptz NOT NULL DEFAULT now(),
    creado_por        uuid,
    modificado_en     timestamptz,
    modificado_por    uuid,
    activo            boolean     NOT NULL DEFAULT true,
    version           integer     NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_inv_sucursales ON inv_sucursales (sucursal_id);

COMMENT ON COLUMN inv_sucursales.controla_desde IS '052: desde cuándo se bloquea lo que no hay («Empezar a controlar»). Null = solo avisa.';

-- --- El saldo ------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS inv_stock (
    id              uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id      uuid          NOT NULL REFERENCES empresas (id),
    producto_id     uuid          NOT NULL REFERENCES productos (id),
    sucursal_id     uuid          NOT NULL REFERENCES sucursales (id),
    fisico          numeric(18,4) NOT NULL DEFAULT 0,
    reservado       numeric(18,4) NOT NULL DEFAULT 0,
    minimo          numeric(18,4),
    actualizado_en  timestamptz   NOT NULL DEFAULT now(),

    CONSTRAINT ck_inv_stock_reservado CHECK (reservado >= 0),
    CONSTRAINT ck_inv_stock_minimo    CHECK (minimo IS NULL OR minimo >= 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_inv_stock ON inv_stock (producto_id, sucursal_id);
CREATE INDEX IF NOT EXISTS ix_inv_stock_empresa ON inv_stock (empresa_id, sucursal_id);

COMMENT ON TABLE inv_stock IS '052: el saldo de cada producto en cada sucursal. Lo escriben solo las funciones inv_*; su historia es inv_movimientos.';

-- --- El kardex -----------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS inv_movimientos (
    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    orden            bigint        GENERATED ALWAYS AS IDENTITY,
    empresa_id       uuid          NOT NULL REFERENCES empresas (id),
    producto_id      uuid          NOT NULL REFERENCES productos (id),
    sucursal_id      uuid          NOT NULL REFERENCES sucursales (id),
    tipo             text          NOT NULL,

    -- Lo que cambia el físico (+ entra, − sale) y el reservado.
    cantidad         numeric(18,4) NOT NULL DEFAULT 0,
    reservado        numeric(18,4) NOT NULL DEFAULT 0,
    saldo_fisico     numeric(18,4) NOT NULL,
    saldo_reservado  numeric(18,4) NOT NULL,

    -- Qué lo causó: un comprobante, una guía, una operación (ingreso, ajuste,
    -- stock inicial, conteo) o la recepción de un traslado.
    fuente           text          NOT NULL,
    fuente_id        uuid          NOT NULL,
    documento_id     uuid          REFERENCES documentos (id),
    guia_id          uuid          REFERENCES guias (id),
    operacion_id     uuid,
    motivo           text,
    nota             text,

    -- Para el inventario valorizado: lo que costó (solo compras, opcional).
    costo_unitario   numeric(18,6),
    moneda           char(3),

    -- Para el inventario ampliado (D3–D5): hoy siempre vacíos.
    variante_id      uuid,
    lote             text,
    serie            text,

    usuario_id       uuid,
    creado_en        timestamptz   NOT NULL DEFAULT now(),

    CONSTRAINT ck_inv_movimientos_tipo CHECK (tipo IN (
        'stock_inicial', 'compra', 'compra_anulada', 'ajuste', 'ajuste_anulado', 'conteo',
        'venta', 'venta_revertida', 'reserva', 'reserva_liberada', 'reserva_revertida',
        'despacho', 'despacho_revertido', 'guia_salida', 'guia_revertida',
        'devolucion', 'devolucion_revertida',
        'traslado_salida', 'traslado_revertido', 'traslado_entrada', 'traslado_faltante', 'traslado_retorno')),
    CONSTRAINT ck_inv_movimientos_fuente CHECK (fuente IN ('documento', 'guia', 'operacion', 'traslado'))
);
CREATE INDEX IF NOT EXISTS ix_inv_mov_kardex  ON inv_movimientos (producto_id, sucursal_id, orden DESC);
CREATE INDEX IF NOT EXISTS ix_inv_mov_fuente  ON inv_movimientos (fuente_id);
CREATE INDEX IF NOT EXISTS ix_inv_mov_empresa ON inv_movimientos (empresa_id, creado_en DESC);

COMMENT ON TABLE inv_movimientos IS '052: el kardex. Solo inserción: un error se corrige con otro movimiento.';

CREATE TABLE IF NOT EXISTS inv_reservas (
    id            uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id    uuid          NOT NULL REFERENCES empresas (id),
    documento_id  uuid          NOT NULL REFERENCES documentos (id),
    producto_id   uuid          NOT NULL REFERENCES productos (id),
    sucursal_id   uuid          NOT NULL REFERENCES sucursales (id),
    reservada     numeric(18,4) NOT NULL DEFAULT 0,
    despachada    numeric(18,4) NOT NULL DEFAULT 0,
    liberada      numeric(18,4) NOT NULL DEFAULT 0,
    creado_en     timestamptz   NOT NULL DEFAULT now(),
    actualizado_en timestamptz  NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_inv_reservas ON inv_reservas (documento_id, producto_id, sucursal_id);

COMMENT ON TABLE inv_reservas IS '052: lo reservado por una factura «se entrega después»; pendiente = reservada − despachada − liberada.';

CREATE TABLE IF NOT EXISTS inv_aplicado (
    fuente_id       uuid        PRIMARY KEY,
    fuente          text        NOT NULL,
    empresa_id      uuid        NOT NULL REFERENCES empresas (id),
    modo            text        NOT NULL,
    sucursal_id     uuid        NOT NULL REFERENCES sucursales (id),
    aplicado        boolean     NOT NULL DEFAULT false,
    creado_en       timestamptz NOT NULL DEFAULT now(),
    actualizado_en  timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT ck_inv_aplicado_fuente CHECK (fuente IN ('documento', 'guia')),
    CONSTRAINT ck_inv_aplicado_modo   CHECK (modo IN ('salida', 'reserva', 'nota', 'despacho', 'traslado', 'ninguno'))
);

COMMENT ON TABLE inv_aplicado IS '052: si un comprobante o una guía ya movió el stock. Mover y revertir son idempotentes.';

-- --- Operaciones: ingresos, ajustes, stock inicial, conteos ---------------------------------

CREATE TABLE IF NOT EXISTS inv_operaciones (
    id                   uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id           uuid        NOT NULL REFERENCES empresas (id),
    tipo                 text        NOT NULL,
    numero               integer     NOT NULL DEFAULT 0,
    sucursal_id          uuid        NOT NULL REFERENCES sucursales (id),
    estado               text        NOT NULL DEFAULT 'registrada',
    fecha                date        NOT NULL DEFAULT current_date,

    -- Ingreso por compra
    proveedor_id         uuid        REFERENCES proveedores (id),
    documento_proveedor  text,
    moneda               char(3),

    -- Ajuste
    motivo               text,
    nota                 text,

    aplicada_en          timestamptz,
    anulada_en           timestamptz,
    anulada_motivo       text,

    creado_en            timestamptz NOT NULL DEFAULT now(),
    creado_por           uuid,
    modificado_en        timestamptz,
    modificado_por       uuid,
    activo               boolean     NOT NULL DEFAULT true,
    version              integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_inv_operaciones_tipo   CHECK (tipo IN ('ingreso', 'ajuste', 'stock_inicial', 'conteo')),
    CONSTRAINT ck_inv_operaciones_estado CHECK (estado IN ('registrada', 'anulada', 'abierto', 'cerrado')),
    CONSTRAINT ck_inv_operaciones_motivo CHECK (motivo IS NULL OR motivo IN (
        'merma', 'vencido', 'robo', 'consumo_interno', 'muestra', 'sobrante', 'correccion', 'otro')),
    CONSTRAINT ck_inv_operaciones_moneda CHECK (moneda IS NULL OR moneda IN ('PEN', 'USD', 'EUR')),
    CONSTRAINT ck_inv_operaciones_anulada CHECK ((estado = 'anulada') = (anulada_en IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_inv_operaciones_numero ON inv_operaciones (empresa_id, tipo, numero);
CREATE INDEX IF NOT EXISTS ix_inv_operaciones_lista ON inv_operaciones (empresa_id, tipo, creado_en DESC);

CREATE TABLE IF NOT EXISTS inv_operacion_lineas (
    id              uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id      uuid          NOT NULL REFERENCES empresas (id),
    operacion_id    uuid          NOT NULL REFERENCES inv_operaciones (id),
    producto_id     uuid          NOT NULL REFERENCES productos (id),
    -- Ingreso: lo que llegó (> 0). Ajuste: + entra / − sale. Stock inicial: lo
    -- que hay (≥ 0). Conteo: no se usa (va en contado).
    cantidad        numeric(18,4) NOT NULL DEFAULT 0,
    costo_unitario  numeric(18,6),
    sistema         numeric(18,4),
    contado         numeric(18,4),

    creado_en       timestamptz   NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean       NOT NULL DEFAULT true,
    version         integer       NOT NULL DEFAULT 1,

    CONSTRAINT ck_inv_operacion_lineas_costo   CHECK (costo_unitario IS NULL OR costo_unitario >= 0),
    CONSTRAINT ck_inv_operacion_lineas_contado CHECK (contado IS NULL OR contado >= 0)
);
CREATE INDEX IF NOT EXISTS ix_inv_operacion_lineas ON inv_operacion_lineas (operacion_id);

-- El número: el siguiente de su tipo en la empresa, con candado (dos ajustes a
-- la vez no se llevan el mismo).
CREATE OR REPLACE FUNCTION fn_inv_numerar() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    IF NEW.numero IS NULL OR NEW.numero = 0 THEN
        PERFORM pg_advisory_xact_lock(hashtext('inv_operaciones:' || NEW.empresa_id || ':' || NEW.tipo));
        SELECT COALESCE(max(numero), 0) + 1 INTO NEW.numero
          FROM inv_operaciones WHERE empresa_id = NEW.empresa_id AND tipo = NEW.tipo;
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS inv_numerar ON inv_operaciones;
CREATE TRIGGER inv_numerar BEFORE INSERT ON inv_operaciones
    FOR EACH ROW EXECUTE FUNCTION fn_inv_numerar();

-- --- Traslados entre sucursales -------------------------------------------------------------

CREATE TABLE IF NOT EXISTS inv_traslados (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id      uuid        NOT NULL REFERENCES empresas (id),
    guia_id         uuid        NOT NULL REFERENCES guias (id),
    origen_id       uuid        NOT NULL REFERENCES sucursales (id),
    destino_id      uuid        NOT NULL REFERENCES sucursales (id),
    estado          text        NOT NULL DEFAULT 'en_transito',
    salio_en        timestamptz NOT NULL DEFAULT now(),
    recibido_en     timestamptz,
    recibido_por    uuid,
    nota            text,

    creado_en       timestamptz NOT NULL DEFAULT now(),
    creado_por      uuid,
    modificado_en   timestamptz,
    modificado_por  uuid,
    activo          boolean     NOT NULL DEFAULT true,
    version         integer     NOT NULL DEFAULT 1,

    CONSTRAINT ck_inv_traslados_estado CHECK (estado IN ('en_transito', 'recibido', 'con_diferencia', 'anulado'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_inv_traslados_guia ON inv_traslados (guia_id);
CREATE INDEX IF NOT EXISTS ix_inv_traslados_destino ON inv_traslados (empresa_id, destino_id, estado);

CREATE TABLE IF NOT EXISTS inv_traslado_lineas (
    id                 uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id         uuid          NOT NULL REFERENCES empresas (id),
    traslado_id        uuid          NOT NULL REFERENCES inv_traslados (id),
    producto_id        uuid          NOT NULL REFERENCES productos (id),
    enviado            numeric(18,4) NOT NULL,
    recibido           numeric(18,4),
    motivo_diferencia  text,

    creado_en          timestamptz   NOT NULL DEFAULT now(),
    creado_por         uuid,
    modificado_en      timestamptz,
    modificado_por     uuid,
    activo             boolean       NOT NULL DEFAULT true,
    version            integer       NOT NULL DEFAULT 1,

    CONSTRAINT ck_inv_traslado_lineas_motivo CHECK (motivo_diferencia IS NULL OR motivo_diferencia IN ('merma', 'perdida', 'enviado_menos'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_inv_traslado_lineas ON inv_traslado_lineas (traslado_id, producto_id);


-- =============================================================================
-- LAS REGLAS
-- =============================================================================

-- La sucursal 0000 de la empresa (la de los documentos sin sucursal).
CREATE OR REPLACE FUNCTION inv_principal(p_empresa uuid) RETURNS uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT id FROM sucursales WHERE empresa_id = p_empresa AND codigo = '0000' ORDER BY creado_en LIMIT 1;
$$;

-- ¿Se bloquea lo que no hay en esta sucursal? Encendido, controlando y sin
-- «permitir vender sin stock».
CREATE OR REPLACE FUNCTION inv_bloquea(p_empresa uuid, p_sucursal uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE((
        SELECT c.activo_inventario AND NOT c.permitir_sin_stock
           AND EXISTS (SELECT 1 FROM inv_sucursales s WHERE s.sucursal_id = p_sucursal AND s.controla_desde IS NOT NULL)
          FROM inventario_config c WHERE c.empresa_id = p_empresa), false);
$$;

-- ¿Está encendido el inventario de la empresa?
CREATE OR REPLACE FUNCTION inv_activo(p_empresa uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path TO 'public' AS $$
    SELECT COALESCE((SELECT activo_inventario FROM inventario_config WHERE empresa_id = p_empresa), false);
$$;


-- -----------------------------------------------------------------------------
-- MOVER: el único sitio que cambia un saldo. Escribe el saldo y el kardex.
--
-- Con p_estricto y si lo disponible baja de cero, NO MUEVE y lanza
-- «INV_SIN_STOCK|producto|sucursal|disponible antes|pedido» (la API lo
-- traduce a palabras).
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION inv_mover(
    p_empresa uuid, p_producto uuid, p_sucursal uuid, p_tipo text,
    p_cantidad numeric, p_reservado numeric,
    p_fuente text, p_fuente_id uuid, p_estricto boolean,
    p_documento uuid DEFAULT NULL, p_guia uuid DEFAULT NULL, p_operacion uuid DEFAULT NULL,
    p_motivo text DEFAULT NULL, p_nota text DEFAULT NULL,
    p_costo numeric DEFAULT NULL, p_moneda text DEFAULT NULL)
RETURNS numeric
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    v_fisico    numeric;
    v_reservado numeric;
BEGIN
    IF COALESCE(p_cantidad, 0) = 0 AND COALESCE(p_reservado, 0) = 0 THEN
        RETURN NULL;
    END IF;

    INSERT INTO inv_stock AS s (empresa_id, producto_id, sucursal_id, fisico, reservado)
    VALUES (p_empresa, p_producto, p_sucursal, COALESCE(p_cantidad, 0), GREATEST(COALESCE(p_reservado, 0), 0))
    ON CONFLICT (producto_id, sucursal_id) DO UPDATE
        SET fisico = s.fisico + COALESCE(p_cantidad, 0),
            reservado = GREATEST(s.reservado + COALESCE(p_reservado, 0), 0),
            actualizado_en = now()
    RETURNING s.fisico, s.reservado INTO v_fisico, v_reservado;

    -- Lo disponible bajó y quedó en negativo: no se puede.
    IF p_estricto AND (COALESCE(p_cantidad, 0) - COALESCE(p_reservado, 0)) < 0 AND v_fisico - v_reservado < 0 THEN
        RAISE EXCEPTION 'INV_SIN_STOCK|%|%|%|%', p_producto, p_sucursal,
            (v_fisico - v_reservado) - (COALESCE(p_cantidad, 0) - COALESCE(p_reservado, 0)),
            -(COALESCE(p_cantidad, 0) - COALESCE(p_reservado, 0))
            USING ERRCODE = 'P0001';
    END IF;

    INSERT INTO inv_movimientos (
        empresa_id, producto_id, sucursal_id, tipo, cantidad, reservado, saldo_fisico, saldo_reservado,
        fuente, fuente_id, documento_id, guia_id, operacion_id, motivo, nota, costo_unitario, moneda, usuario_id)
    VALUES (
        p_empresa, p_producto, p_sucursal, p_tipo, COALESCE(p_cantidad, 0), COALESCE(p_reservado, 0), v_fisico, v_reservado,
        p_fuente, p_fuente_id, p_documento, p_guia, p_operacion, p_motivo, p_nota, p_costo, p_moneda, ctx_usuario());

    RETURN v_fisico;
END $$;


-- -----------------------------------------------------------------------------
-- UN COMPROBANTE (factura, boleta o nota de crédito) Y EL STOCK.
--
--   Factura / boleta, venta directa   sale del físico al emitir (encolado) y
--                                     vuelve si SUNAT la rechaza, si no llegó
--                                     al servicio (error) o con la baja.
--   Factura «se entrega después»      reserva; la guía saca. Rechazo / baja:
--                                     se libera lo que falta despachar (lo que
--                                     ya salió con guía no vuelve solo).
--   Nota de crédito 01/02/06/07       al ser aceptada: primero libera lo que
--                                     seguía reservado del original; el resto
--                                     entra (si volvió) o entra y sale como
--                                     merma (si volvió dañada).
--
-- Bloquea (si la sucursal controla y no se permite vender sin stock) SOLO al
-- emitir: el aviso del servicio nunca falla por stock.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION inv_sync_documento(p_doc uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    d          documentos%ROWTYPE;
    a          inv_aplicado%ROWTYPE;
    v_cfg      inventario_config%ROWTYPE;
    v_quiere   boolean;
    v_modo     text;
    v_suc      uuid;
    v_estricto boolean;
    r          record;
    v_pend     numeric;
    v_toma     numeric;
    v_resto    numeric;
BEGIN
    SELECT * INTO d FROM documentos WHERE id = p_doc;
    IF NOT FOUND OR d.tipo NOT IN ('01', '03', '07') THEN RETURN; END IF;

    IF d.tipo = '07' THEN
        v_quiere := d.activo AND d.estado IN ('aceptado', 'observado') AND d.tipo_nota IN ('01', '02', '06', '07');
    ELSE
        v_quiere := d.activo AND d.estado IN ('encolado', 'aceptado', 'observado');
    END IF;

    SELECT * INTO a FROM inv_aplicado WHERE fuente_id = p_doc FOR UPDATE;

    IF NOT FOUND THEN
        IF NOT v_quiere THEN RETURN; END IF;
        SELECT * INTO v_cfg FROM inventario_config WHERE empresa_id = d.empresa_id;
        IF NOT FOUND OR NOT v_cfg.activo_inventario THEN RETURN; END IF;

        v_suc := COALESCE(d.sucursal_id, inv_principal(d.empresa_id));
        IF v_suc IS NULL THEN RETURN; END IF;
        IF d.tipo = '07' THEN
            v_modo := 'nota';
            v_suc := COALESCE(d.inv_sucursal_id, v_suc);
        ELSIF COALESCE(d.entrega_despues, v_cfg.modo = 'despacho') THEN
            v_modo := 'reserva';
        ELSE
            v_modo := 'salida';
        END IF;

        INSERT INTO inv_aplicado (fuente_id, fuente, empresa_id, modo, sucursal_id, aplicado)
        VALUES (p_doc, 'documento', d.empresa_id, v_modo, v_suc, false)
        RETURNING * INTO a;
    END IF;

    IF v_quiere = a.aplicado THEN RETURN; END IF;

    IF v_quiere THEN
        -- Si el inventario se apagó entretanto, no se mueve nada nuevo.
        IF NOT inv_activo(d.empresa_id) THEN RETURN; END IF;
        v_estricto := d.estado = 'encolado' AND inv_bloquea(d.empresa_id, a.sucursal_id);

        FOR r IN
            SELECT l.producto_id, sum(l.cantidad) AS q
              FROM documento_lineas l
              JOIN productos p ON p.id = l.producto_id
             WHERE l.documento_id = d.id AND l.activo AND p.controla_stock AND l.cantidad > 0
             GROUP BY l.producto_id
             ORDER BY l.producto_id     -- siempre el mismo orden: sin cruces de candados
        LOOP
            IF a.modo = 'salida' THEN
                PERFORM inv_mover(d.empresa_id, r.producto_id, a.sucursal_id, 'venta', -r.q, 0,
                                  'documento', d.id, v_estricto, d.id);

            ELSIF a.modo = 'reserva' THEN
                -- Si se vuelve a aplicar (raro: llegó «aceptado» después del
                -- rechazo), no se reserva lo que ya despacharon sus guías.
                SELECT COALESCE(sum(despachada), 0) INTO v_pend
                  FROM inv_reservas WHERE documento_id = d.id AND producto_id = r.producto_id;
                v_toma := GREATEST(r.q - v_pend, 0);
                INSERT INTO inv_reservas AS x (empresa_id, documento_id, producto_id, sucursal_id, reservada)
                VALUES (d.empresa_id, d.id, r.producto_id, a.sucursal_id, v_toma)
                ON CONFLICT (documento_id, producto_id, sucursal_id) DO UPDATE
                    SET reservada = x.reservada + v_toma, actualizado_en = now();
                PERFORM inv_mover(d.empresa_id, r.producto_id, a.sucursal_id, 'reserva', 0, v_toma,
                                  'documento', d.id, v_estricto, d.id);

            ELSE   -- nota de crédito
                v_toma := 0;
                FOR v_pend, v_suc IN
                    SELECT x.reservada - x.despachada - x.liberada, x.sucursal_id
                      FROM inv_reservas x
                     WHERE x.documento_id = d.documento_ref_id AND x.producto_id = r.producto_id
                       AND x.reservada - x.despachada - x.liberada > 0
                LOOP
                    v_resto := LEAST(r.q - v_toma, v_pend);
                    EXIT WHEN v_resto <= 0;
                    UPDATE inv_reservas SET liberada = liberada + v_resto, actualizado_en = now()
                     WHERE documento_id = d.documento_ref_id AND producto_id = r.producto_id AND sucursal_id = v_suc;
                    PERFORM inv_mover(d.empresa_id, r.producto_id, v_suc, 'reserva_liberada', 0, -v_resto,
                                      'documento', d.id, false, d.documento_ref_id, NULL, NULL, NULL, 'nota de crédito');
                    v_toma := v_toma + v_resto;
                END LOOP;

                v_resto := r.q - v_toma;
                IF v_resto > 0 AND COALESCE(d.inv_retorno, 'volvio') IN ('volvio', 'danada') THEN
                    PERFORM inv_mover(d.empresa_id, r.producto_id, a.sucursal_id, 'devolucion', v_resto, 0,
                                      'documento', d.id, false, d.id);
                    IF d.inv_retorno = 'danada' THEN
                        PERFORM inv_mover(d.empresa_id, r.producto_id, a.sucursal_id, 'ajuste', -v_resto, 0,
                                          'documento', d.id, false, d.id, NULL, NULL, 'merma', 'volvió dañada');
                    END IF;
                END IF;
            END IF;
        END LOOP;

        UPDATE inv_aplicado SET aplicado = true, actualizado_en = now() WHERE fuente_id = p_doc;

    ELSE
        -- REVERTIR.
        IF a.modo = 'reserva' THEN
            -- Se libera lo que faltaba despachar.
            FOR r IN SELECT * FROM inv_reservas WHERE documento_id = d.id ORDER BY producto_id LOOP
                v_pend := r.reservada - r.despachada - r.liberada;
                IF v_pend > 0 THEN
                    UPDATE inv_reservas SET liberada = liberada + v_pend, actualizado_en = now() WHERE id = r.id;
                    PERFORM inv_mover(d.empresa_id, r.producto_id, r.sucursal_id, 'reserva_revertida', 0, -v_pend,
                                      'documento', d.id, false, d.id, NULL, NULL, NULL, d.estado);
                END IF;
            END LOOP;
        ELSE
            -- Venta directa o nota: lo contrario de lo que movió.
            FOR r IN
                SELECT m.producto_id, m.sucursal_id, m.documento_id,
                       sum(m.cantidad) AS c, sum(m.reservado) AS rv
                  FROM inv_movimientos m
                 WHERE m.fuente = 'documento' AND m.fuente_id = d.id
                 GROUP BY m.producto_id, m.sucursal_id, m.documento_id
                HAVING sum(m.cantidad) <> 0 OR sum(m.reservado) <> 0
                 ORDER BY m.producto_id
            LOOP
                -- Lo liberado del original vuelve a estar reservado.
                IF r.rv <> 0 AND r.documento_id IS DISTINCT FROM d.id THEN
                    UPDATE inv_reservas SET liberada = liberada + r.rv, actualizado_en = now()
                     WHERE documento_id = r.documento_id AND producto_id = r.producto_id AND sucursal_id = r.sucursal_id;
                END IF;
                PERFORM inv_mover(d.empresa_id, r.producto_id, r.sucursal_id,
                                  CASE WHEN a.modo = 'nota' THEN 'devolucion_revertida' ELSE 'venta_revertida' END,
                                  -r.c, -r.rv, 'documento', d.id, false, r.documento_id, NULL, NULL, NULL, d.estado);
            END LOOP;
        END IF;

        UPDATE inv_aplicado SET aplicado = false, actualizado_en = now() WHERE fuente_id = p_doc;
    END IF;
END $$;


-- -----------------------------------------------------------------------------
-- UNA GUÍA DEL REMITENTE (09) Y EL STOCK. Se mueve al emitirla (encolado) y
-- se revierte si SUNAT la rechaza, si no llegó al servicio o si se registra
-- su anulación en SOL.
--
--   De una factura «se entrega después»  despacho: sale del físico y consume
--                                         la reserva.
--   De una factura de venta directa       nada (ya salió con la factura).
--   Traslado entre sucursales (04)        sale del origen y queda en tránsito
--                                         hasta el «Recibí» (ya recibido, la
--                                         anulación no lo deshace).
--   Compra (02) o importación (08)        nada: entra por «Ingreso por compra».
--   Cualquier otro motivo                 sale del físico.
--
-- La 31 (transportista) nunca: la mercadería no es de la empresa.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION inv_sync_guia(p_guia uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    g          guias%ROWTYPE;
    a          inv_aplicado%ROWTYPE;
    ad         inv_aplicado%ROWTYPE;
    v_quiere   boolean;
    v_modo     text;
    v_suc      uuid;
    v_estricto boolean;
    v_tras     uuid;
    v_estado_t text;
    r          record;
    v_pend     numeric;
    v_toma     numeric;
BEGIN
    SELECT * INTO g FROM guias WHERE id = p_guia;
    IF NOT FOUND OR g.tipo <> '09' THEN RETURN; END IF;

    v_quiere := g.activo AND g.estado IN ('encolado', 'aceptado', 'observado');

    SELECT * INTO a FROM inv_aplicado WHERE fuente_id = p_guia FOR UPDATE;
    IF NOT FOUND THEN
        IF NOT v_quiere OR NOT inv_activo(g.empresa_id) THEN RETURN; END IF;

        v_suc := COALESCE(g.inv_origen_id, g.sucursal_id, inv_principal(g.empresa_id));
        IF g.motivo IN ('02', '08') THEN
            v_modo := 'ninguno';
        ELSIF g.documento_id IS NOT NULL THEN
            SELECT * INTO ad FROM inv_aplicado WHERE fuente_id = g.documento_id;
            IF FOUND AND ad.modo = 'reserva' THEN
                v_modo := 'despacho';
                v_suc := ad.sucursal_id;
            ELSIF FOUND AND ad.modo = 'salida' THEN
                v_modo := 'ninguno';
            ELSE
                v_modo := 'salida';     -- facturada antes de llevar inventario: sale ahora
            END IF;
        ELSIF g.motivo = '04' AND g.inv_destino_id IS NOT NULL AND g.inv_destino_id <> v_suc THEN
            v_modo := 'traslado';
        ELSE
            v_modo := 'salida';
        END IF;
        IF v_suc IS NULL THEN RETURN; END IF;

        INSERT INTO inv_aplicado (fuente_id, fuente, empresa_id, modo, sucursal_id, aplicado)
        VALUES (p_guia, 'guia', g.empresa_id, v_modo, v_suc, false)
        RETURNING * INTO a;
    END IF;

    IF v_quiere = a.aplicado OR a.modo = 'ninguno' THEN RETURN; END IF;

    IF v_quiere THEN
        IF NOT inv_activo(g.empresa_id) THEN RETURN; END IF;
        v_estricto := g.estado = 'encolado' AND inv_bloquea(g.empresa_id, a.sucursal_id);

        IF a.modo = 'traslado' THEN
            SELECT id, estado INTO v_tras, v_estado_t FROM inv_traslados WHERE guia_id = g.id;
            IF v_tras IS NULL THEN
                INSERT INTO inv_traslados (empresa_id, guia_id, origen_id, destino_id)
                VALUES (g.empresa_id, g.id, a.sucursal_id, g.inv_destino_id)
                RETURNING id INTO v_tras;
            ELSE
                UPDATE inv_traslados SET estado = 'en_transito', salio_en = now() WHERE id = v_tras;
            END IF;
        END IF;

        FOR r IN
            SELECT b.producto_id, sum(b.cantidad) AS q
              FROM guia_bienes b
              JOIN productos p ON p.id = b.producto_id
             WHERE b.guia_id = g.id AND b.activo AND p.controla_stock
             GROUP BY b.producto_id
             ORDER BY b.producto_id
        LOOP
            IF a.modo = 'despacho' THEN
                SELECT COALESCE(sum(reservada - despachada - liberada), 0) INTO v_pend
                  FROM inv_reservas WHERE documento_id = g.documento_id AND producto_id = r.producto_id
                   AND sucursal_id = a.sucursal_id;
                v_toma := LEAST(r.q, GREATEST(v_pend, 0));
                IF v_toma > 0 THEN
                    UPDATE inv_reservas SET despachada = despachada + v_toma, actualizado_en = now()
                     WHERE documento_id = g.documento_id AND producto_id = r.producto_id AND sucursal_id = a.sucursal_id;
                END IF;
                PERFORM inv_mover(g.empresa_id, r.producto_id, a.sucursal_id, 'despacho', -r.q, -v_toma,
                                  'guia', g.id, v_estricto, g.documento_id, g.id);

            ELSIF a.modo = 'traslado' THEN
                INSERT INTO inv_traslado_lineas AS x (empresa_id, traslado_id, producto_id, enviado)
                VALUES (g.empresa_id, v_tras, r.producto_id, r.q)
                ON CONFLICT (traslado_id, producto_id) DO UPDATE SET enviado = r.q;
                PERFORM inv_mover(g.empresa_id, r.producto_id, a.sucursal_id, 'traslado_salida', -r.q, 0,
                                  'guia', g.id, v_estricto, NULL, g.id);

            ELSE
                PERFORM inv_mover(g.empresa_id, r.producto_id, a.sucursal_id, 'guia_salida', -r.q, 0,
                                  'guia', g.id, v_estricto, NULL, g.id);
            END IF;
        END LOOP;

        UPDATE inv_aplicado SET aplicado = true, actualizado_en = now() WHERE fuente_id = p_guia;

    ELSE
        IF a.modo = 'traslado' THEN
            SELECT id, estado INTO v_tras, v_estado_t FROM inv_traslados WHERE guia_id = g.id;
            -- Ya recibida en destino: la mercadería está allá.
            IF v_estado_t IN ('recibido', 'con_diferencia') THEN RETURN; END IF;
            UPDATE inv_traslados SET estado = 'anulado' WHERE id = v_tras;
        END IF;

        FOR r IN
            SELECT m.producto_id, m.sucursal_id, m.documento_id, sum(m.cantidad) AS c, sum(m.reservado) AS rv
              FROM inv_movimientos m
             WHERE m.fuente = 'guia' AND m.fuente_id = g.id
             GROUP BY m.producto_id, m.sucursal_id, m.documento_id
            HAVING sum(m.cantidad) <> 0 OR sum(m.reservado) <> 0
             ORDER BY m.producto_id
        LOOP
            IF r.rv <> 0 AND r.documento_id IS NOT NULL THEN
                UPDATE inv_reservas SET despachada = despachada + r.rv, actualizado_en = now()
                 WHERE documento_id = r.documento_id AND producto_id = r.producto_id AND sucursal_id = r.sucursal_id;
            END IF;
            PERFORM inv_mover(g.empresa_id, r.producto_id, r.sucursal_id,
                              CASE a.modo WHEN 'despacho' THEN 'despacho_revertido'
                                          WHEN 'traslado' THEN 'traslado_revertido'
                                          ELSE 'guia_revertida' END,
                              -r.c, -r.rv, 'guia', g.id, false, r.documento_id, g.id, NULL, NULL, g.estado);
        END LOOP;

        UPDATE inv_aplicado SET aplicado = false, actualizado_en = now() WHERE fuente_id = p_guia;
    END IF;
END $$;


-- Los disparadores. El del comprobante es inmediato (sus líneas ya existen
-- cuando cambia de estado, y así el «no alcanza» llega al guardar). El de la
-- guía va AL FINAL DE LA TRANSACCIÓN: la guía nace ya «encolado» y sus bienes
-- se insertan después, en la misma transacción.
CREATE OR REPLACE FUNCTION fn_inv_documento() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    PERFORM inv_sync_documento(NEW.id);
    RETURN NULL;
END $$;

CREATE OR REPLACE FUNCTION fn_inv_guia() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path TO 'public' AS $$
BEGIN
    PERFORM inv_sync_guia(NEW.id);
    RETURN NULL;
END $$;

DROP TRIGGER IF EXISTS inv_documento ON documentos;
CREATE TRIGGER inv_documento
    AFTER INSERT OR UPDATE OF estado, activo ON documentos
    FOR EACH ROW EXECUTE FUNCTION fn_inv_documento();

DROP TRIGGER IF EXISTS inv_guia ON guias;
CREATE CONSTRAINT TRIGGER inv_guia
    AFTER INSERT OR UPDATE OF estado, activo ON guias
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION fn_inv_guia();


-- -----------------------------------------------------------------------------
-- LAS OPERACIONES (las llama la API, ya con la operación y sus líneas
-- guardadas en la misma transacción). Revisan que sean de la empresa de quien
-- llama: corren sin RLS.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION inv_aplicar_operacion(p_op uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    o          inv_operaciones%ROWTYPE;
    l          record;
    v_actual   numeric;
    v_estricto boolean;
    v_delta    numeric;
BEGIN
    SELECT * INTO o FROM inv_operaciones WHERE id = p_op FOR UPDATE;
    IF NOT FOUND OR (o.empresa_id IS DISTINCT FROM ctx_empresa() AND NOT ctx_es_super()) THEN
        RAISE EXCEPTION 'INV_NO_EXISTE';
    END IF;
    IF o.aplicada_en IS NOT NULL THEN RETURN; END IF;
    IF NOT inv_activo(o.empresa_id) THEN RAISE EXCEPTION 'INV_APAGADO'; END IF;

    v_estricto := inv_bloquea(o.empresa_id, o.sucursal_id);

    FOR l IN SELECT * FROM inv_operacion_lineas WHERE operacion_id = o.id AND activo ORDER BY producto_id LOOP
        IF o.tipo = 'ingreso' THEN
            PERFORM inv_mover(o.empresa_id, l.producto_id, o.sucursal_id, 'compra', l.cantidad, 0,
                              'operacion', o.id, false, NULL, NULL, o.id, NULL, o.nota, l.costo_unitario, o.moneda);

        ELSIF o.tipo = 'ajuste' THEN
            PERFORM inv_mover(o.empresa_id, l.producto_id, o.sucursal_id, 'ajuste', l.cantidad, 0,
                              'operacion', o.id, v_estricto, NULL, NULL, o.id, o.motivo, o.nota);

        ELSIF o.tipo = 'stock_inicial' THEN
            IF EXISTS (SELECT 1 FROM inv_movimientos m
                        WHERE m.producto_id = l.producto_id AND m.sucursal_id = o.sucursal_id AND m.tipo = 'stock_inicial') THEN
                RAISE EXCEPTION 'INV_YA_INICIAL|%', l.producto_id;
            END IF;
            SELECT COALESCE(fisico, 0) INTO v_actual FROM inv_stock
             WHERE producto_id = l.producto_id AND sucursal_id = o.sucursal_id;
            v_delta := l.cantidad - COALESCE(v_actual, 0);
            UPDATE inv_operacion_lineas SET sistema = COALESCE(v_actual, 0) WHERE id = l.id;
            IF v_delta <> 0 THEN
                PERFORM inv_mover(o.empresa_id, l.producto_id, o.sucursal_id, 'stock_inicial', v_delta, 0,
                                  'operacion', o.id, false, NULL, NULL, o.id, NULL, o.nota, l.costo_unitario, o.moneda);
            ELSE
                -- En cero y ya en cero: igual queda constancia de que se cargó.
                INSERT INTO inv_movimientos (empresa_id, producto_id, sucursal_id, tipo, cantidad, reservado,
                                             saldo_fisico, saldo_reservado, fuente, fuente_id, operacion_id, usuario_id)
                VALUES (o.empresa_id, l.producto_id, o.sucursal_id, 'stock_inicial', 0, 0,
                        COALESCE(v_actual, 0),
                        COALESCE((SELECT reservado FROM inv_stock WHERE producto_id = l.producto_id AND sucursal_id = o.sucursal_id), 0),
                        'operacion', o.id, o.id, ctx_usuario());
            END IF;

        ELSIF o.tipo = 'conteo' THEN
            IF l.contado IS NULL THEN CONTINUE; END IF;
            SELECT COALESCE(fisico, 0) INTO v_actual FROM inv_stock
             WHERE producto_id = l.producto_id AND sucursal_id = o.sucursal_id;
            v_delta := l.contado - COALESCE(v_actual, 0);
            UPDATE inv_operacion_lineas SET sistema = COALESCE(v_actual, 0), cantidad = v_delta WHERE id = l.id;
            PERFORM inv_mover(o.empresa_id, l.producto_id, o.sucursal_id, 'conteo', v_delta, 0,
                              'operacion', o.id, false, NULL, NULL, o.id, NULL, 'diferencia de conteo');
        END IF;
    END LOOP;

    IF o.tipo = 'stock_inicial' THEN
        INSERT INTO inv_sucursales AS s (empresa_id, sucursal_id, stock_inicial_en)
        VALUES (o.empresa_id, o.sucursal_id, now())
        ON CONFLICT (sucursal_id) DO UPDATE SET stock_inicial_en = COALESCE(s.stock_inicial_en, now());
    END IF;

    UPDATE inv_operaciones
       SET aplicada_en = now(),
           estado = CASE WHEN tipo = 'conteo' THEN 'cerrado' ELSE estado END
     WHERE id = o.id;
END $$;

-- Anular un ingreso o un ajuste: el movimiento contrario. No bloquea (lo que
-- ya se vendió se vendió): la pantalla avisa antes cuánto quedaría.
CREATE OR REPLACE FUNCTION inv_anular_operacion(p_op uuid, p_motivo text) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    o inv_operaciones%ROWTYPE;
    r record;
BEGIN
    SELECT * INTO o FROM inv_operaciones WHERE id = p_op FOR UPDATE;
    IF NOT FOUND OR (o.empresa_id IS DISTINCT FROM ctx_empresa() AND NOT ctx_es_super()) THEN
        RAISE EXCEPTION 'INV_NO_EXISTE';
    END IF;
    IF o.tipo NOT IN ('ingreso', 'ajuste') OR o.estado <> 'registrada' OR o.aplicada_en IS NULL THEN
        RAISE EXCEPTION 'INV_NO_ANULABLE';
    END IF;

    FOR r IN
        SELECT producto_id, sucursal_id, sum(cantidad) AS c
          FROM inv_movimientos WHERE fuente = 'operacion' AND fuente_id = o.id
         GROUP BY producto_id, sucursal_id HAVING sum(cantidad) <> 0 ORDER BY producto_id
    LOOP
        PERFORM inv_mover(o.empresa_id, r.producto_id, r.sucursal_id,
                          CASE o.tipo WHEN 'ingreso' THEN 'compra_anulada' ELSE 'ajuste_anulado' END,
                          -r.c, 0, 'operacion', o.id, false, NULL, NULL, o.id, NULL, p_motivo);
    END LOOP;

    UPDATE inv_operaciones SET estado = 'anulada', anulada_en = now(), anulada_motivo = p_motivo WHERE id = o.id;
END $$;

-- «Recibí»: lo que llegó entra al destino. Lo que falta, según por qué:
-- llegó roto o no llegó → entra y sale como faltante (queda en el kardex del
-- destino); se envió de menos → vuelve al origen.
-- p_lineas: [{"productoId": "...", "recibido": 48, "motivo": "merma"}]
CREATE OR REPLACE FUNCTION inv_recibir_traslado(p_traslado uuid, p_lineas jsonb, p_nota text) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path TO 'public' AS $$
DECLARE
    t        inv_traslados%ROWTYPE;
    l        record;
    v_rec    numeric;
    v_mot    text;
    v_falta  numeric;
    v_dif    boolean := false;
BEGIN
    SELECT * INTO t FROM inv_traslados WHERE id = p_traslado FOR UPDATE;
    IF NOT FOUND OR (t.empresa_id IS DISTINCT FROM ctx_empresa() AND NOT ctx_es_super()) THEN
        RAISE EXCEPTION 'INV_NO_EXISTE';
    END IF;
    IF t.estado <> 'en_transito' THEN RAISE EXCEPTION 'INV_YA_RECIBIDO'; END IF;

    FOR l IN SELECT * FROM inv_traslado_lineas WHERE traslado_id = t.id AND activo ORDER BY producto_id LOOP
        SELECT NULLIF(x ->> 'recibido', '')::numeric, NULLIF(x ->> 'motivo', '')
          INTO v_rec, v_mot
          FROM jsonb_array_elements(COALESCE(p_lineas, '[]'::jsonb)) x
         WHERE (x ->> 'productoId')::uuid = l.producto_id
         LIMIT 1;
        v_rec := COALESCE(v_rec, l.enviado);
        IF v_rec < 0 OR v_rec > l.enviado THEN RAISE EXCEPTION 'INV_RECIBIDO_MAL|%', l.producto_id; END IF;
        v_falta := l.enviado - v_rec;
        IF v_falta > 0 AND v_mot IS NULL THEN RAISE EXCEPTION 'INV_FALTA_MOTIVO|%', l.producto_id; END IF;
        IF v_falta = 0 THEN v_mot := NULL; ELSE v_dif := true; END IF;

        IF v_falta > 0 AND v_mot IN ('merma', 'perdida') THEN
            PERFORM inv_mover(t.empresa_id, l.producto_id, t.destino_id, 'traslado_entrada', l.enviado, 0,
                              'traslado', t.id, false, NULL, t.guia_id, NULL, NULL, p_nota);
            PERFORM inv_mover(t.empresa_id, l.producto_id, t.destino_id, 'traslado_faltante', -v_falta, 0,
                              'traslado', t.id, false, NULL, t.guia_id, NULL, v_mot,
                              CASE v_mot WHEN 'merma' THEN 'llegó roto' ELSE 'no llegó' END);
        ELSE
            PERFORM inv_mover(t.empresa_id, l.producto_id, t.destino_id, 'traslado_entrada', v_rec, 0,
                              'traslado', t.id, false, NULL, t.guia_id, NULL, NULL, p_nota);
            IF v_falta > 0 THEN   -- se envió de menos: nunca salió del origen
                PERFORM inv_mover(t.empresa_id, l.producto_id, t.origen_id, 'traslado_retorno', v_falta, 0,
                                  'traslado', t.id, false, NULL, t.guia_id, NULL, v_mot, 'se envió de menos');
            END IF;
        END IF;

        UPDATE inv_traslado_lineas SET recibido = v_rec, motivo_diferencia = v_mot WHERE id = l.id;
    END LOOP;

    UPDATE inv_traslados
       SET estado = CASE WHEN v_dif THEN 'con_diferencia' ELSE 'recibido' END,
           recibido_en = now(), recibido_por = ctx_usuario(), nota = p_nota
     WHERE id = t.id;

    RETURN CASE WHEN v_dif THEN 'con_diferencia' ELSE 'recibido' END;
END $$;


-- =============================================================================
-- AISLAMIENTO, BITÁCORA, DUEÑOS Y PERMISOS
-- =============================================================================

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['producto_categorias', 'proveedores', 'inventario_config', 'inv_sucursales',
                             'inv_stock', 'inv_movimientos', 'inv_reservas', 'inv_aplicado',
                             'inv_operaciones', 'inv_operacion_lineas', 'inv_traslados', 'inv_traslado_lineas'] LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
        IF NOT EXISTS (SELECT 1 FROM pg_policies WHERE policyname = 'p_' || t) THEN
            EXECUTE format(
                'CREATE POLICY %I ON %I USING (ctx_es_super() OR empresa_id = ctx_empresa()) ' ||
                'WITH CHECK (ctx_es_super() OR empresa_id = ctx_empresa())', 'p_' || t, t);
        END IF;
    END LOOP;

    -- Las maestras y las operaciones: bitácora completa.
    FOREACH t IN ARRAY ARRAY['producto_categorias', 'proveedores', 'inventario_config', 'inv_sucursales',
                             'inv_operaciones', 'inv_operacion_lineas', 'inv_traslados', 'inv_traslado_lineas'] LOOP
        IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = t::regclass AND NOT tgisinternal AND tgname LIKE 'auditar_%') THEN
            PERFORM vigilar_tabla(t);
        END IF;
    END LOOP;
END $$;

-- El kardex es su propia bitácora: solo inserción (candado) y, en la
-- bitácora, solo un intento de cambiarlo (que el candado no deja pasar).
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'inv_movimientos'::regclass AND tgname = 'inmutable_inv_movimientos') THEN
        CREATE TRIGGER inmutable_inv_movimientos BEFORE UPDATE OR DELETE ON inv_movimientos
            FOR EACH ROW EXECUTE FUNCTION fn_solo_insercion();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'inv_movimientos'::regclass AND tgname = 'auditar_inv_movimientos') THEN
        CREATE TRIGGER auditar_inv_movimientos AFTER UPDATE OR DELETE ON inv_movimientos
            FOR EACH ROW EXECUTE FUNCTION fn_auditar();
    END IF;
    -- El saldo: en la bitácora, los cambios de mínimo (lo demás es el kardex).
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'inv_stock'::regclass AND tgname = 'auditar_inv_stock') THEN
        CREATE TRIGGER auditar_inv_stock AFTER UPDATE OF minimo ON inv_stock
            FOR EACH ROW WHEN (OLD.minimo IS DISTINCT FROM NEW.minimo) EXECUTE FUNCTION fn_auditar();
    END IF;
    -- Reservas y «aplicado»: su historia está en el kardex.
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'inv_reservas'::regclass AND tgname = 'auditar_inv_reservas') THEN
        CREATE TRIGGER auditar_inv_reservas AFTER DELETE ON inv_reservas FOR EACH ROW EXECUTE FUNCTION fn_auditar();
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'inv_aplicado'::regclass AND tgname = 'auditar_inv_aplicado') THEN
        CREATE TRIGGER auditar_inv_aplicado AFTER DELETE ON inv_aplicado FOR EACH ROW EXECUTE FUNCTION fn_auditar();
    END IF;
END $$;

-- La aplicación: las maestras, la configuración y las operaciones por EF; el
-- saldo, el kardex, las reservas y lo aplicado solo los leen (los escriben
-- las funciones).
GRANT SELECT, INSERT, UPDATE ON producto_categorias, proveedores, inventario_config, inv_sucursales,
                                inv_operaciones, inv_operacion_lineas TO plataforma_app;
GRANT SELECT ON inv_traslados, inv_traslado_lineas, inv_stock, inv_movimientos, inv_reservas, inv_aplicado TO plataforma_app;
GRANT UPDATE (minimo) ON inv_stock TO plataforma_app;
-- El mínimo de un producto que todavía no tiene saldo crea su fila (en cero).
GRANT INSERT (empresa_id, producto_id, sucursal_id, minimo) ON inv_stock TO plataforma_app;

-- Las funciones son del dueño de las puertas (027). Las internas no las llama
-- la aplicación; las de operaciones sí.
DO $$
DECLARE f text;
BEGIN
    FOREACH f IN ARRAY ARRAY[
        'inv_principal(uuid)', 'inv_bloquea(uuid, uuid)', 'inv_activo(uuid)',
        'inv_mover(uuid, uuid, uuid, text, numeric, numeric, text, uuid, boolean, uuid, uuid, uuid, text, text, numeric, text)',
        'inv_sync_documento(uuid)', 'inv_sync_guia(uuid)', 'fn_inv_documento()', 'fn_inv_guia()', 'fn_inv_numerar()',
        'inv_aplicar_operacion(uuid)', 'inv_anular_operacion(uuid, text)', 'inv_recibir_traslado(uuid, jsonb, text)'] LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO plataforma_puertas', f);
    END LOOP;

    FOREACH f IN ARRAY ARRAY[
        'inv_mover(uuid, uuid, uuid, text, numeric, numeric, text, uuid, boolean, uuid, uuid, uuid, text, text, numeric, text)',
        'inv_sync_documento(uuid)', 'inv_sync_guia(uuid)'] LOOP
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'plataforma_app') THEN
            EXECUTE format('REVOKE ALL ON FUNCTION %s FROM plataforma_app', f);
        END IF;
    END LOOP;
END $$;

GRANT EXECUTE ON FUNCTION inv_principal(uuid), inv_bloquea(uuid, uuid), inv_activo(uuid),
                          inv_aplicar_operacion(uuid), inv_anular_operacion(uuid, text),
                          inv_recibir_traslado(uuid, jsonb, text) TO plataforma_app;

INSERT INTO permisos (codigo, area, descripcion) VALUES
    ('inventario.ver',        'inventario', 'Ver el stock y el kardex'),
    ('inventario.ingresar',   'inventario', 'Registrar ingresos por compra y proveedores'),
    ('inventario.ajustar',    'inventario', 'Hacer ajustes y cargar el stock inicial'),
    ('inventario.trasladar',  'inventario', 'Recibir traslados entre sucursales'),
    ('inventario.contar',     'inventario', 'Hacer conteos físicos (toma de inventario)'),
    ('inventario.configurar', 'inventario', 'Encender el inventario, su modo y «Empezar a controlar»')
ON CONFLICT (codigo) DO NOTHING;

-- Administrador: todo. Jefe de sucursal (y los roles basados en él): todo
-- menos configurar. Facturador: ver y recibir traslados. Consulta: ver.
INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT r.id, p.codigo
  FROM roles r
 CROSS JOIN (VALUES ('inventario.ver'), ('inventario.ingresar'), ('inventario.ajustar'),
                    ('inventario.trasladar'), ('inventario.contar'), ('inventario.configurar')) AS p (codigo)
 WHERE r.id = '11111111-1111-1111-1111-111111111111'
   AND NOT EXISTS (SELECT 1 FROM rol_permiso o WHERE o.rol_id = r.id AND o.permiso_codigo = p.codigo);

INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT r.id, p.codigo
  FROM roles r
 CROSS JOIN (VALUES ('inventario.ver'), ('inventario.ingresar'), ('inventario.ajustar'),
                    ('inventario.trasladar'), ('inventario.contar')) AS p (codigo)
 WHERE (r.id = '55555555-5555-5555-5555-555555555555' OR r.basado_en = '55555555-5555-5555-5555-555555555555')
   AND NOT EXISTS (SELECT 1 FROM rol_permiso o WHERE o.rol_id = r.id AND o.permiso_codigo = p.codigo);

INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT r.id, p.codigo
  FROM roles r
 CROSS JOIN (VALUES ('inventario.ver'), ('inventario.trasladar')) AS p (codigo)
 WHERE (r.id = '22222222-2222-2222-2222-222222222222' OR r.basado_en = '22222222-2222-2222-2222-222222222222')
   AND NOT EXISTS (SELECT 1 FROM rol_permiso o WHERE o.rol_id = r.id AND o.permiso_codigo = p.codigo);

INSERT INTO rol_permiso (rol_id, permiso_codigo)
SELECT r.id, 'inventario.ver'
  FROM roles r
 WHERE (r.id = '33333333-3333-3333-3333-333333333333' OR r.basado_en = '33333333-3333-3333-3333-333333333333')
   AND NOT EXISTS (SELECT 1 FROM rol_permiso o WHERE o.rol_id = r.id AND o.permiso_codigo = 'inventario.ver');

INSERT INTO migraciones_aplicadas (nombre) VALUES ('052_inventario')
ON CONFLICT (nombre) DO NOTHING;

COMMIT;

DO $$ BEGIN
    RAISE NOTICE '052: inventario por sucursal listo (apagado en todas las empresas hasta que lo enciendan). Nada se borró.';
END $$;