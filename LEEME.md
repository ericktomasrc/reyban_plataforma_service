# Base de datos de la plataforma

**PostgreSQL 17** · esquema completo, probado · septiembre de 2026

Base de datos **propia y separada** de la del servicio de facturación. Pueden
vivir en el mismo contenedor de PostgreSQL, pero son dos bases con dos
usuarios distintos, y el usuario de la plataforma no tiene ningún permiso
sobre la de facturación.

---

## Cómo se corre

En orden, una sola vez cada una:

```bash
psql -U postgres -d plataforma -v ON_ERROR_STOP=1 -f 001_fundacion.sql
psql -U postgres -d plataforma -v ON_ERROR_STOP=1 -f 002_empresas_y_usuarios.sql
psql -U postgres -d plataforma -v ON_ERROR_STOP=1 -f 003_seguridad.sql
psql -U postgres -d plataforma -v ON_ERROR_STOP=1 -f 004_modulos_y_credenciales.sql
psql -U postgres -d plataforma -v ON_ERROR_STOP=1 -f 005_modulo_facturacion.sql
psql -U postgres -d plataforma -v ON_ERROR_STOP=1 -f 006_semillas.sql
```

Y después, **conectado como `plataforma_app`, no como `postgres`**:

```bash
psql -U plataforma_app -d plataforma -f verificar.sql
```

Tiene que terminar en `TODAS LAS COMPROBACIONES PASARON`.

> **Por qué como `plataforma_app`:** un superusuario **se salta el Row Level
> Security siempre**. Una prueba de aislamiento hecha como `postgres` pasa
> aunque el aislamiento no exista. Es el tipo de prueba que da confianza y no
> protege nada.

Lo primero que hay que hacer después: cambiar la contraseña del rol, que en la
migración viene como `CAMBIAR_EN_DESPLIEGUE`.

```sql
ALTER ROLE plataforma_app PASSWORD '<generada al azar>';
```

---

## Las convenciones

**Campos de auditoría en toda tabla de negocio:**

| Columna | |
|---|---|
| `creado_en` | timestamptz, NOT NULL |
| `creado_por` | uuid, NULL = lo hizo el sistema, no una persona |
| `modificado_en` | timestamptz |
| `modificado_por` | uuid |
| `activo` | boolean, NOT NULL, `true` |
| `version` | integer, NOT NULL, para concurrencia optimista |

**Los rellena la base, no la aplicación.** Un disparador `BEFORE INSERT OR
UPDATE` los escribe y pisa lo que venga. No hay forma de insertar una fila
mintiendo sobre quién la creó, ni desde el código ni desde `psql`. La
comprobación 4 de `verificar.sql` intenta exactamente eso y falla, como debe.

**Nada se borra.** El rol de aplicación **no tiene permiso `DELETE` en ninguna
tabla**. Se desactiva con `activo = false`. Un cliente que desaparece de la
tabla deja sus documentos apuntando al vacío, y esos documentos son
justamente lo que una fiscalización va a pedir.

**Los índices únicos son parciales**, `WHERE activo`, para que desactivar y
volver a crear funcione. Con dos excepciones deliberadas: el RUC de una
empresa y la numeración de un documento son únicos **siempre**, activos o no.

---

## La auditoría, en dos niveles

Son dos preguntas distintas y hacen falta las dos.

### Nivel 1 — `bitacora`: qué cambió en cada fila

Una fila por cada INSERT, UPDATE y DELETE de **cada tabla**, con el contenido
completo antes y después en JSONB, más la lista de campos que cambiaron, el
usuario, la IP y el navegador.

Es genérica a propósito. "Registrar todo sin excepción" y "acordarse de
registrar" son incompatibles: el día que alguien cree una tabla y olvide su
bitácora, el agujero no se nota hasta que hace falta.

Por eso existe la vista **`tablas_sin_vigilar`**. Si devuelve filas, hay una
tabla desprotegida. Correrla después de cada migración.

### Nivel 2 — historiales de negocio

`documento_historial`, `eventos_seguridad`, `llamadas_facturacion`,
`webhooks_recibidos`.

La bitácora responde *«quién tocó esta fila»*. Un historial responde *«por qué
este comprobante está rechazado»* — y eso se le enseña al cliente en pantalla.
Una fila de bitácora con dos JSONB dentro no se le puede enseñar a nadie.

Además hay cosas que **no cambian ninguna fila** y por tanto no dejan rastro
en la bitácora, y son justo las que interesa vigilar:

- un ingreso con contraseña incorrecta
- un 403 por falta de permiso
- un enlace de restablecer contraseña caducado que alguien intentó usar
- una llamada al servicio de facturación que se quedó sin respuesta

Todo eso vive en `eventos_seguridad`.

### Las dos tablas son de solo inserción

Doble candado, a propósito redundante:

1. el rol de aplicación solo tiene `INSERT` y `SELECT`;
2. un disparador que salta aunque quien lo intente sea el propietario.

El segundo existe porque el primero se deshace con un `GRANT` despistado. Un
candado que depende de que nadie se equivoque no es un candado.

---

## Lo que NUNCA entra en la bitácora

Esta es la parte que más fácil se hace mal, y hacerla mal convierte la
auditoría en la peor filtración del sistema.

Los disparadores se enganchan listando las columnas a ocultar:

```sql
SELECT vigilar_tabla('usuarios', ARRAY['clave_hash', 'secreto_2fa']);
```

Sin esa lista, cada cambio de contraseña dejaría una copia del hash anterior
en una tabla que nadie revisa, que se respalda a diario y que crece para
siempre.

Hoy se ocultan:

| Tabla | Columnas |
|---|---|
| `usuarios` | `clave_hash`, `secreto_2fa` |
| `credenciales_facturacion` | `clave_cifrada`, `nonce` |
| `sesiones` | `token_hash` |
| `tokens_un_uso` | `token_hash` |
| `codigos_recuperacion` | `codigo_hash` |
| `webhooks_recibidos` | `cuerpo` (ya está entero en su propia tabla) |

**El orden importa, y es el error caro de todo esto.** El disparador calcula
primero *qué* cambió, sobre los valores de verdad, y solo después los tapa.
Al revés, un cambio de contraseña compararía `[oculto]` contra `[oculto]`,
concluiría que no cambió nada, y **no registraría absolutamente nada**. El
movimiento que más falta hace vigilar sería el único invisible.

Esto no es hipotético: la primera versión de estas migraciones tenía ese fallo
y lo encontró la comprobación 8.

---

## El aislamiento entre empresas

Dos barreras, y la que cuenta es la segunda.

1. `empresa_id` en toda tabla de negocio.
2. **Row Level Security**, activo y con `FORCE`.

Con RLS, las filas de otra empresa **no existen** para la consulta, aunque
alguien escriba un `SELECT *` sin `WHERE`. Es el motor de la base de datos, no
el código de aplicación.

`FORCE` hace que las políticas se apliquen también al propietario de la tabla,
no solo al rol de aplicación. Sin él, una sesión de mantenimiento se las salta
sin querer.

**El super administrador es la única excepción**, y está escrita a la vista en
cada política como `ctx_es_super()`. Vive en su propia bandera y no en un
permiso más, para que cueste escribirla y se vea de lejos.

**El rol de aplicación NO debe tener `BYPASSRLS` ni `SUPERUSER`.** Si lo
tuviera, el aislamiento dejaría de existir sin que nadie se entere.

---

## ⚠ Lo que hay que saber antes de escribir la primera línea de C#

### El contexto se fija por petición, DENTRO de una transacción

Antes de cualquier consulta:

```sql
SELECT fijar_contexto(:usuario, :empresa, :ip, :agente, :es_super);
```

Eso deja el usuario y la empresa en la conexión, y de ahí los leen el RLS y la
bitácora.

**El detalle que cuesta una tarde de depuración:** `fijar_contexto` usa
`set_config(..., true)`, o sea **local a la transacción**. Si EF Core ejecuta
la consulta fuera de una transacción explícita, cada sentencia es su propia
transacción, el contexto se pierde entre una y otra, y **el RLS devuelve cero
filas sin dar ningún error**.

Entonces: **el middleware abre una transacción, llama a `fijar_contexto`, deja
correr la petición y hace commit.** No hay atajo.

Es local a la transacción a propósito. Con un pool de conexiones, un valor que
sobreviviera se filtraría a la petición del siguiente usuario — que es la
misma clase de fallo que el RLS viene a evitar.

### Configuración de EF Core

```csharp
// La versión la lleva la base. EF la usa para detectar choques.
builder.Property(e => e.Version)
       .IsConcurrencyToken()
       .ValueGeneratedOnAddOrUpdate();

// Estas cuatro también las escribe la base: EF no debe mandarlas.
builder.Property(e => e.CreadoEn).ValueGeneratedOnAdd();
builder.Property(e => e.CreadoPor).ValueGeneratedOnAdd();
builder.Property(e => e.ModificadoEn).ValueGeneratedOnAddOrUpdate();
builder.Property(e => e.ModificadoPor).ValueGeneratedOnAddOrUpdate();

// Nada borrado aparece nunca en una consulta normal.
builder.HasQueryFilter(e => e.Activo);
```

Y `SaveChanges` nunca emite `DELETE`: el borrado es `entidad.Activo = false`.

### Un UPDATE que no cambia nada no cuenta

Si la fila queda idéntica salvo por las columnas de auditoría, el disparador
devuelve la fila original: no sube la versión, no toca `modificado_en`, no
escribe en la bitácora.

Sirve para dos cosas. Que `version` y la bitácora cuenten la misma historia. Y
que abrir un formulario y guardarlo sin tocar nada no haga fallar el guardado
de otro usuario por un conflicto de concurrencia inventado.

---

## Tokens y secretos

**La regla:** un token se guarda como **hash**; un secreto que hay que
recuperar se guarda **cifrado**.

| Qué | Cómo | Por qué |
|---|---|---|
| Contraseñas | PBKDF2-SHA256, 210.000 iteraciones | Las elige una persona, y las personas eligen mal |
| Tokens de sesión y de un solo uso | SHA-256 | Son 32 bytes aleatorios: no hay diccionario que los adivine, y no hay que recuperarlos |
| Secreto del 2FA | Cifrado (AES-GCM) | Hay que leerlo para validar el código |
| Clave de API de facturación | Cifrada (AES-GCM) | Hay que enviarla en cada llamada |

De un hash no se vuelve atrás, y por eso quien robe un volcado de la base **no
puede suplantar a nadie**. Si se guardaran los tokens tal cual, un respaldo
filtrado sería una sesión abierta por cada usuario conectado.

**La llave maestra de la plataforma vive en una variable de entorno, nunca en
la base.** Guardar la llave junto a lo que protege es cerrar la puerta y
dejarla en la cerradura. Consecuencia: la llave es parte del respaldo y va
guardada **aparte**. Sin ella, un respaldo restaurado trae las credenciales
convertidas en ruido.

---

## Por qué sesiones en tabla y no un JWT

Un JWT vale hasta que expira y no hay forma de anularlo. Si despides a alguien
a las diez de la mañana, su token sigue funcionando hasta que caduque.

Con una fila en `sesiones`, cerrarle la puerta es un `UPDATE`.

Añadido: la cookie es `HttpOnly`, así que el JavaScript de la página no puede
leerla. Un JWT en `localStorage` se lo lleva cualquier XSS.

---

## Permisos, no roles

**El código pregunta siempre por permisos.** Un endpoint dice «hace falta
`facturacion.emitir`» y le da igual qué rol lo tenga. Así, cuando mañana se
cree un rol nuevo que también deba emitir, ningún código se entera.

Si los endpoints preguntaran por roles, cada rol nuevo sería un despliegue.

**Los permisos son una migración; los roles, una pantalla.** Un permiso existe
porque hay una línea de código que lo comprueba. Crearlos desde la interfaz
produciría permisos que no protegen nada, y —peor— la sensación de que sí.

**Ocultar el menú no es seguridad.** La vista `menu_usuario` es comodidad: evita
que la gente pulse cosas que le darían error. La validación real va en cada
endpoint. Si alguien escribe la URL a mano, recibe 403 aunque el menú nunca se
lo haya mostrado.

**Dos niveles de administración, separados desde el principio.** El área
`plataforma` es solo tuya: crear empresas, activar módulos, ver la bitácora de
todos, suplantar usuarios. El rol de Administrador de una empresa **no recibe
ninguno de esos permisos**. Si se mezclaran en un solo rol "administrador",
tarde o temprano un cliente se activa un módulo que no paga.

---

## La frontera con el servicio de facturación

**La plataforma no sabe de SUNAT.** No calcula IGV, no arma XML, no conoce los
catálogos, no decide si una operación es gratuita. Todo eso está en el
servicio y ya está probado contra el ambiente beta, documento por documento.

Aquí solo hay: qué escribió el usuario, qué se le mandó al servicio, y qué
contestó.

**Los totales de `documentos` son una copia informativa.** La cifra que se
firmó y viajó a SUNAT es la que devolvió el servicio. Si algún día las dos
difieren, gana el servicio y esta fila está mal. Se guardan igualmente porque
un listado no puede hacer una llamada HTTP por cada fila de la pantalla.

**No hay tabla de series ni de correlativos.** Los asigna el servicio, con
candado. Duplicarlos aquí sería crear una segunda fuente de verdad para el
dato que más caro cuesta equivocar: un correlativo duplicado es un problema
tributario, no un error de programa.

**No se copian el XML, el CDR ni el PDF.** Se guarda `comprobante_id` y con él
se piden al servicio. Copiarlos sería tener dos originales del mismo documento
legal.

**Una clave de API por empresa.** En el servicio, una clave equivale a un RUC.
Si la plataforma usara una sola para todos sus clientes, todas las facturas
saldrían a nombre de la misma empresa.

**La clave de API nunca llega al navegador.** Quien llama a `/v1/` es el
backend de la plataforma. Una clave de emisor en el navegador la ve cualquiera
con F12, y con ella se emite en nombre de esa empresa.

**Y nunca se guarda en `llamadas_facturacion`.** El código tiene que quitarla
de la cabecera antes de registrar la llamada. Una tabla de depuración con las
llaves de todos los clientes dentro es peor que no tener tabla.

---

## Las tablas

**Fundación** — `bitacora`, `migraciones_aplicadas`

**Empresas y acceso** — `empresas`, `usuarios`, `permisos`, `roles`,
`rol_permiso`, `usuario_rol`

**Seguridad** — `sesiones`, `tokens_un_uso`, `codigos_recuperacion`,
`eventos_seguridad`

**Módulos** — `modulos`, `empresa_modulo`, `credenciales_facturacion`

**Facturación** — `clientes`, `productos`, `documentos`, `documento_lineas`,
`documento_historial`, `llamadas_facturacion`, `webhooks_recibidos`

**Vistas** — `permisos_efectivos`, `menu_usuario`, `tablas_sin_vigilar`

---

## Al añadir una tabla nueva

1. Ponerle las seis columnas de auditoría.
2. Ponerle `empresa_id` si es de negocio.
3. `SELECT vigilar_tabla('la_tabla', ARRAY['columnas','sensibles']);`
4. `ENABLE` y `FORCE ROW LEVEL SECURITY` + su política.
5. `GRANT SELECT, INSERT, UPDATE` — **nunca `DELETE`**.
6. Correr `verificar.sql`, que comprobará que no quedó sin vigilar.

---

## Lo que falta

- Purga de `llamadas_facturacion` a los 90 días: el cuerpo de las peticiones
  viejas ya no le sirve a nadie y es lo que más pesa.
- Particionado de `bitacora` por mes, el día que crezca. No antes.
- Decidir la retención de `eventos_seguridad`. Los de ingreso conviene
  guardarlos años; los de navegación, no.
