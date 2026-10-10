-- =============================================================================
-- 011 — AFECTACIÓN HABITUAL DEL CLIENTE
--
-- Un campo en `clientes`, y detrás de él el error más caro que se comete
-- facturando.
--
-- EL PROBLEMA QUE RESUELVE:
--
--   El mismo producto no lleva siempre la misma afectación del IGV. Unas
--   zapatillas se venden gravadas en Lima y exportadas a Miami. La afectación
--   no es del producto: es de la operación, y la operación la define a quién
--   le vendes.
--
--   Hasta ahora la línea de un comprobante heredaba la afectación del
--   producto, que es el valor correcto el 90% de las veces. El 10% restante
--   —el cliente de exportación— dependía de que la persona se acordara de
--   cambiarlo en el desplegable.
--
--   Cuando no se acuerda, la factura sale con IGV a un cliente que no lo paga.
--   Y sale bien formada: el servicio la firma, SUNAT la acepta, y el error
--   aparece semanas después cuando el contador cuadra el mes. Para entonces
--   solo se arregla con una nota de crédito.
--
-- LA DECISIÓN: ES UNA PREFERENCIA, NO UNA REGLA.
--
--   El campo admite NULL, y NULL es el caso normal: «este cliente no tiene
--   nada especial, usa lo que diga el producto».
--
--   Cuando tiene valor, la pantalla lo propone al elegir un producto y avisa
--   en ámbar si la línea no coincide. AVISA, NO PROHÍBE. Un cliente de
--   exportación puede comprar algo en el mercado local perfectamente, y una
--   plataforma que lo impida obliga a mentir en la ficha del cliente para
--   poder facturar — que es peor que el error que quería evitar.
--
-- POR QUÉ NO SE HIZO CON UNA TABLA CLIENTE↔PRODUCTO, que era la otra idea:
--
--   Habría hecho falta duplicar cada producto por cada afectación —las mismas
--   zapatillas dos veces, con dos códigos— y mantener a mano las relaciones
--   entre cada cliente y cada producto. Con 200 clientes y 500 productos son
--   cien mil relaciones que alguien tiene que cuidar, y el día que se da de
--   alta un producto nuevo no le aparece a nadie hasta que se relacione, con
--   una venta esperando. Lo que pasa entonces es que se relaciona todo con
--   todos para salir del paso, y la protección desaparece.
--
--   Un campo en el cliente da el mismo resultado y se escribe una sola vez.
--
-- NO HACE FALTA TOCAR NINGÚN DISPARADOR: el de la bitácora de `clientes` se
-- creó con `vigilar_tabla`, que engancha un AFTER INSERT OR UPDATE OR DELETE
-- sin lista de columnas. La columna nueva entra en la bitácora sola.
-- =============================================================================

BEGIN;

ALTER TABLE clientes
    ADD COLUMN afectacion_habitual char(2);

-- LOS MISMOS CÓDIGOS QUE ADMITE UN PRODUCTO, del catálogo 07 de SUNAT:
-- 10 gravado, 17 IVAP, 20 exonerado, 30 inafecto, 40 exportación.
--
-- No están los de operación gratuita (11 a 16, 21, 31 a 37) porque una
-- bonificación no es una costumbre de un cliente: es algo que se decide en una
-- venta concreta, y se elige en la línea.
ALTER TABLE clientes ADD CONSTRAINT ck_clientes_afectacion_habitual CHECK (
    afectacion_habitual IS NULL OR
    afectacion_habitual IN ('10', '17', '20', '30', '40'));

COMMENT ON COLUMN clientes.afectacion_habitual IS
    'Afectacion del IGV que se propone al facturar a este cliente. NULL = usar la del producto. Es una preferencia, no una restriccion.';


INSERT INTO migraciones_aplicadas (nombre) VALUES ('011_afectacion_habitual');

COMMIT;