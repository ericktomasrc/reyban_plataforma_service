# Estructura del proyecto `reyban_plataforma`

Monolito modular. Un proyecto por módulo, las capas en carpetas.

**La regla que el compilador hace cumplir:** `Core` no conoce a ningún módulo,
y **un módulo nunca referencia a otro**. Si mañana Facturación necesita algo de
Ventas, va por una interfaz declarada en `Core/Contratos/`.

La marca **①** señala lo que hace falta para la primera rebanada: entrar con un
usuario y que `/yo` devuelva su empresa, sus permisos y su menú. Todo lo demás
viene después.

---

```
reyban_plataforma/
│
├─ .gitignore                      ① lo escribo yo: Windows no crea archivos con punto
├─ .env.ejemplo                    ① plantilla SIN un solo valor real
├─ .env                            ①  NUNCA en git
├─ docker-compose.yml              ① PostgreSQL 17 + Adminer
├─ Directory.Build.props           ① versión de .NET y avisos como errores, en un solo sitio
├─ Plataforma.sln
│
├─ migraciones/                    ① ya las tienes
│  ├─ 001_fundacion.sql
│  ├─ 002_empresas_y_usuarios.sql
│  ├─ 003_seguridad.sql
│  ├─ 004_modulos_y_credenciales.sql
│  ├─ 005_modulo_facturacion.sql
│  ├─ 006_semillas.sql
│  ├─ verificar.sql
│  └─ LEEME.md
│
│
├─ Plataforma.Core/                ① lo común. No conoce ningún módulo
│  │
│  ├─ Dominio/
│  │  ├─ EntidadAuditada.cs        ① base: CreadoEn, CreadoPor, ModificadoEn,
│  │  │                                  ModificadoPor, Activo, Version
│  │  ├─ EntidadDeEmpresa.cs       ① EntidadAuditada + EmpresaId
│  │  ├─ Empresa.cs                ①
│  │  ├─ Usuario.cs                ①
│  │  ├─ Rol.cs                    ①
│  │  ├─ Permiso.cs                ①
│  │  ├─ RolPermiso.cs             ①
│  │  ├─ UsuarioRol.cs             ①
│  │  ├─ Modulo.cs                 ①
│  │  ├─ EmpresaModulo.cs          ①
│  │  ├─ Sesion.cs                 ①
│  │  ├─ TokenUnUso.cs
│  │  ├─ CodigoRecuperacion.cs
│  │  ├─ EventoSeguridad.cs        ①
│  │  ├─ TipoEvento.cs             ① constantes de los tipos del CHECK
│  │  └─ Permisos.cs               ① constantes: Permisos.Facturacion.Emitir
│  │                                  Nunca cadenas sueltas por el código:
│  │                                  un permiso mal escrito no falla, deja pasar
│  │
│  ├─ Datos/
│  │  ├─ ContextoPlataforma.cs     ① el DbContext
│  │  ├─ ConvencionesAuditoria.cs  ① aplica a TODAS las entidades de golpe:
│  │  │                                 Version como token de concurrencia,
│  │  │                                 los cuatro campos como generados por la base,
│  │  │                                 HasQueryFilter(e => e.Activo)
│  │  ├─ FabricaConexion.cs        ① NpgsqlDataSource
│  │  └─ Configuraciones/
│  │     ├─ ConfiguracionEmpresa.cs        ①
│  │     ├─ ConfiguracionUsuario.cs        ①
│  │     ├─ ConfiguracionRol.cs            ①
│  │     ├─ ConfiguracionPermiso.cs        ①
│  │     ├─ ConfiguracionModulo.cs         ①
│  │     ├─ ConfiguracionSesion.cs         ①
│  │     └─ ConfiguracionEventoSeguridad.cs ①
│  │
│  ├─ Seguridad/
│  │  ├─ LlaveMaestra.cs           ① la lee del entorno y FALLA AL ARRANCAR si
│  │  │                                 no está. Arrancar sin ella y descubrirlo
│  │  │                                 al primer certificado es peor
│  │  ├─ HashDeClaves.cs           ① PBKDF2-SHA256, 210.000 iteraciones.
│  │  │                                 El formato guarda algoritmo e iteraciones
│  │  │                                 junto al hash, para poder subirlas con
│  │  │                                 los años sin echar a nadie
│  │  ├─ Cifrador.cs               ① AES-GCM. Para el 2FA y la clave de API
│  │  ├─ GeneradorTokens.cs        ① 32 bytes aleatorios; devuelve el valor
│  │  │                                 y su SHA-256. El valor solo se ve una vez
│  │  ├─ Totp.cs                     el segundo factor
│  │  └─ ContextoPeticion.cs       ① usuario, empresa, es_super, permisos.
│  │                                  Lo llena el middleware, lo leen los endpoints
│  │
│  ├─ Autorizacion/
│  │  ├─ RequierePermisoAttribute.cs  ①
│  │  ├─ RequiereModuloAttribute.cs   ①
│  │  └─ ServicioPermisos.cs          ① lee permisos_efectivos y menu_usuario
│  │
│  └─ Contratos/
│     ├─ IRelojPeru.cs             ① la hora la decide una interfaz, no DateTime.Now
│     └─ IServicioCorreo.cs
│
│
├─ Plataforma.Facturacion/         el módulo. Cliente del servicio, nada más
│  │
│  ├─ Dominio/
│  │  ├─ Cliente.cs                     el receptor del comprobante
│  │  ├─ Producto.cs
│  │  ├─ Documento.cs
│  │  ├─ DocumentoLinea.cs
│  │  ├─ DocumentoHistorial.cs
│  │  ├─ EstadoDocumento.cs             borrador · encolado · aceptado ·
│  │  │                                 observado · rechazado · anulado · error
│  │  └─ CredencialFacturacion.cs
│  │
│  ├─ Datos/Configuraciones/
│  │  ├─ ConfiguracionCliente.cs
│  │  ├─ ConfiguracionProducto.cs
│  │  ├─ ConfiguracionDocumento.cs
│  │  └─ ConfiguracionCredencial.cs
│  │
│  ├─ Cliente/                     ← el puente con el servicio de facturación
│  │  ├─ IClienteFacturacion.cs
│  │  ├─ ClienteFacturacion.cs          HttpClient tipado contra /v1/
│  │  ├─ Contratos.cs                   los DTO del servicio, generados del Swagger
│  │  ├─ ProveedorCredenciales.cs       descifra la clave de la empresa activa
│  │  └─ RegistroDeLlamadas.cs          escribe llamadas_facturacion
│  │                                    Y QUITA LA CABECERA DE LA CLAVE antes de
│  │                                    guardar. Una tabla de depuración con las
│  │                                    llaves de todos los clientes dentro es
│  │                                    peor que no tener tabla
│  │
│  ├─ Servicio/
│  │  ├─ ServicioEmision.cs             el caso de uso completo: valida, guarda
│  │  │                                 el borrador, llama al servicio, actualiza
│  │  ├─ ServicioAnulacion.cs           baja, reversión y anulación de boletas
│  │  ├─ ServicioClientes.cs
│  │  └─ ServicioProductos.cs
│  │
│  ├─ Webhooks/
│  │  ├─ VerificadorHmac.cs             comprueba la firma del servicio
│  │  └─ ReceptorWebhooks.cs            guarda SIEMPRE, aunque la firma falle.
│  │                                    Es lo que distingue «no avisó» de
│  │                                    «avisó y lo rechazamos»
│  │
│  └─ Endpoints/
│     ├─ EndpointsDocumentos.cs
│     ├─ EndpointsClientes.cs
│     ├─ EndpointsProductos.cs
│     ├─ EndpointsCredenciales.cs
│     └─ EndpointsWebhook.cs            sin sesión: entra el servicio, no una persona
│
│
├─ Plataforma.Api/                 ① el host
│  │
│  ├─ Program.cs                   ① compone los módulos
│  ├─ appsettings.json             ① SIN secretos: solo registro y rutas
│  ├─ Properties/launchSettings.json   ① puertos. NO se versiona
│  │
│  ├─ Middleware/
│  │  ├─ MiddlewareContexto.cs     ① LA PIEZA DE RIESGO
│  │  │                                 Abre transacción → fijar_contexto() →
│  │  │                                 deja correr la petición → commit.
│  │  │                                 Sin la transacción, el contexto se pierde
│  │  │                                 entre sentencias y el RLS devuelve cero
│  │  │                                 filas SIN DAR NINGÚN ERROR
│  │  └─ MiddlewareErrores.cs      ① un solo sitio que convierte excepciones en
│  │                                  respuestas, y registra el evento
│  │
│  ├─ Sesion/
│  │  ├─ EndpointsSesion.cs        ① POST /sesion, DELETE /sesion, GET /yo
│  │  ├─ ServicioIngreso.cs        ① contraseña, bloqueo por intentos,
│  │  │                                 eventos de seguridad
│  │  ├─ EndpointsDosFactor.cs
│  │  └─ EndpointsClave.cs             cambiar y restablecer
│  │
│  ├─ Administracion/
│  │  ├─ EndpointsEmpresas.cs          super admin
│  │  ├─ EndpointsModulos.cs           super admin
│  │  ├─ EndpointsUsuarios.cs
│  │  ├─ EndpointsRoles.cs
│  │  └─ EndpointsBitacora.cs
│  │
│  ├─ Arranque/
│  │  ├─ ComprobacionesArranque.cs ① llave maestra presente, base alcanzable,
│  │  │                                 migraciones al día. Falla al arrancar,
│  │  │                                 no a mitad del día
│  │  └─ PrimerAdministrador.cs    ① crea el super admin si no existe, con la
│  │                                  contraseña de una variable de entorno y
│  │                                  cambio forzado. Nunca en una migración:
│  │                                  acabaría en el historial de git
│  │
│  └─ wwwroot/                     ① aquí compila el front
│
│
└─ Plataforma.Web/                 el front
   ├─ package.json
   ├─ tsconfig.json
   ├─ vite.config.ts               ① build.outDir → ../Plataforma.Api/wwwroot
   │                                  y proxy a la API en desarrollo
   ├─ index.html
   └─ src/
      ├─ main.tsx                  ①
      ├─ App.tsx                   ① rutas
      ├─ estilos.css               ① los tokens de color y tipografía, una vez
      │
      ├─ api/
      │  ├─ cliente.ts             ① fetch con credentials:'include' y
      │  │                             manejo del 401 en un solo sitio
      │  └─ tipos.ts               ① generados del Swagger de la plataforma
      │
      ├─ sesion/
      │  ├─ Ingreso.tsx            ①
      │  ├─ DosFactor.tsx
      │  └─ useSesion.ts           ① guarda lo que devuelve /yo
      │
      ├─ componentes/
      │  ├─ Disposicion.tsx        ① cabecera + menú + contenido
      │  ├─ Menu.tsx               ① pinta lo que devuelve /yo, nada más
      │  ├─ Tabla.tsx
      │  ├─ Campo.tsx
      │  └─ Dialogo.tsx
      │
      ├─ facturacion/
      │  ├─ ListaDocumentos.tsx
      │  ├─ EmitirDocumento.tsx
      │  ├─ EditorLineas.tsx       ← la pieza difícil del front
      │  ├─ FichaDocumento.tsx        cabecera, líneas, historial, descargas
      │  ├─ Clientes.tsx
      │  └─ Productos.tsx
      │
      └─ administracion/
         ├─ Empresas.tsx
         ├─ Modulos.tsx
         ├─ Usuarios.tsx
         └─ Roles.tsx
```

---

## Tres decisiones que están dentro de esta estructura

**`Permisos.cs` con constantes, nunca cadenas sueltas.** Un permiso mal escrito
en una cadena no falla al compilar ni al ejecutar: simplemente no coincide con
ninguno, y según cómo esté escrita la comprobación, **deja pasar**. Con
constantes, el error es del compilador.

**El front no decide nada de seguridad.** `Menu.tsx` pinta lo que devuelve
`/yo` y punto. La comprobación real está en cada endpoint. Ocultar el menú solo
evita que la gente pulse cosas que le darían error.

**Los totales los calcula el servidor.** `EditorLineas.tsx` puede ir sumando
mientras el usuario escribe, pero eso es una vista previa. La cifra que se
firma sale del servicio de facturación, que ya sabe de ISC, ICBPER, IVAP,
gratuitas y cargos globales. Si las dos difieren, gana el servicio.

---

## La primera rebanada, en orden

1. `docker-compose.yml`, `.gitignore`, `.env.ejemplo` — los escribo yo
2. Correr las seis migraciones y `verificar.sql`
3. `Plataforma.Core/Dominio` y `Datos` — las entidades y el DbContext
4. `MiddlewareContexto.cs` — la pieza de riesgo, cuanto antes
5. `Seguridad/` — llave maestra, hash, tokens
6. `Sesion/` — ingreso y `GET /yo`
7. El front mínimo: `Ingreso.tsx` y una pantalla que muestre lo de `/yo`

**Cuando el paso 7 funcione:** entras como Ana, ves que trae un solo permiso de
menú, y compruebas que con Beto (empresa B) no se ve nada de la A. Ahí el
cimiento está probado y todo lo demás se apoya en algo que ya sabes que
aguanta.
