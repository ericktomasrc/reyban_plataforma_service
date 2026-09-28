-- =============================================================================
-- 009 — AISLAR LA BITÁCORA
--
-- La tabla existe desde la 001 y hasta ahora solo se escribía. Al ponerle una
-- pantalla, pasa a leerse — y sin política, un administrador de empresa vería
-- los cambios de todos los demás clientes.
--
-- POR QUÉ NO SE ARREGLA CON UN `WHERE` EN LA CONSULTA: porque entonces el
-- aislamiento dependería de que quien escriba esa consulta se acuerde. Es la
-- regla de oro número 6, y la bitácora no es una excepción: es justamente la
-- tabla donde un descuido se nota más tarde y duele más.
-- =============================================================================

ALTER TABLE bitacora ENABLE ROW LEVEL SECURITY;
ALTER TABLE bitacora FORCE  ROW LEVEL SECURITY;

-- ESCRIBIR SIEMPRE SE PUEDE; LEER, NO. Es el mismo criterio que en
-- `eventos_seguridad`, y por el mismo motivo:
--
-- El disparador de auditoría corre dentro de la operación que lo provocó, y
-- muchas de esas filas no tienen empresa — un cambio en `permisos`, en
-- `modulos`, o cualquier cosa que haga el super administrador. Con un
-- `WITH CHECK` estricto, esas escrituras fallarían y tumbarían la operación
-- original: alguien no podría guardar un cambio porque la bitácora se negó a
-- anotarlo.
--
-- Un registro que se niega a registrar es peor que no tenerlo.
CREATE POLICY p_bitacora ON bitacora
    USING (ctx_es_super() OR empresa_id = ctx_empresa())
    WITH CHECK (true);

COMMENT ON TABLE bitacora IS
    'Todo cambio de toda tabla. Se lee filtrada por empresa: las filas sin empresa (migraciones, arranque, acciones del super administrador) solo las ve el super administrador.';


-- =============================================================================
-- ÍNDICE PARA LA PANTALLA
--
-- Los cuatro índices de la 001 sirven para buscar por fila, por usuario, por
-- empresa o por fecha. La pantalla filtra además por TABLA dentro de una
-- empresa —«enséñame qué pasó con los usuarios»— y eso no lo cubre ninguno.
--
-- Sin él, ese filtro recorrería la bitácora entera, que es la tabla que más
-- crece de todo el sistema.
-- =============================================================================

CREATE INDEX ix_bitacora_empresa_tabla
    ON bitacora (empresa_id, tabla, ocurrido_en DESC);


-- =============================================================================
-- Y LO MISMO PARA LOS EVENTOS DE SEGURIDAD
--
-- Su política ya estaba bien desde la 003. Falta solo el índice: la pantalla
-- filtra por tipo dentro de una empresa.
-- =============================================================================

CREATE INDEX ix_eventos_empresa_tipo
    ON eventos_seguridad (empresa_id, tipo, ocurrido_en DESC);


INSERT INTO migraciones_aplicadas (nombre) VALUES ('009_bitacora_visible');
