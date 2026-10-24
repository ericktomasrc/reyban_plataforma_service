using Microsoft.EntityFrameworkCore;
using Plataforma.Api.Administracion;
using Plataforma.Core.Correo;
using Plataforma.Core.Datos;
using Plataforma.Core.Seguridad;
using Plataforma.Core.Autorizacion;

namespace Plataforma.Api.Sesion;

public record PeticionCambioCorreo(string? Nuevo, string? Clave, string? Codigo);
public record PeticionConfirmarCorreo(string? Token);

/// <summary>
/// 048 — LA PERSONA IDENTIFICADA.
///
///   GET  /api/perfil/ficha      mi ficha
///   PUT  /api/perfil/ficha      completarla o corregirla (y «Tus datos» al entrar)
///   POST /api/perfil/correo     pedir el cambio de correo (contraseña + código)
///   POST /api/correo/confirmar  el enlace del correo nuevo (sin sesión)
///
/// Y el filtro <see cref="RequiereFicha"/>: emitir, anular, hacer notas y
/// agregar una empresa piden la ficha completa (con el plazo de aviso).
/// </summary>
public static class EndpointsMiFicha
{
    private const int HorasCambioCorreo = 24;

    public static void MapearMiFicha(this IEndpointRouteBuilder rutas)
    {
        var grupo = rutas.MapGroup("/api/perfil").WithTags("Mi perfil");

        grupo.MapGet("/ficha", async (ContextoPlataforma db, ContextoPeticion ctx, CancellationToken ct) =>
        {
            var u = await db.Usuarios.AsNoTracking().Where(x => x.Id == ctx.UsuarioId)
                .Select(x => new
                {
                    x.TipoDocumento, x.NumeroDocumento, x.Nombres, x.ApellidoPaterno, x.ApellidoMaterno,
                    x.Celular, x.FechaNacimiento, x.Direccion, x.Ubigeo, x.Cargo, x.FechaIngreso,
                    x.FichaPlazo, x.FichaConfirmadaEn
                })
                .FirstOrDefaultAsync(ct);
            return u is null ? Results.Unauthorized() : Results.Ok(u);
        })
        .RequiereSesion()
        .WithSummary("Mi ficha");


        // El cargo y la fecha de ingreso los pone quien da el trabajo: aquí no se tocan.
        grupo.MapPut("/ficha", async (PeticionFicha p, ContextoPlataforma db, ContextoPeticion ctx,
                                      CancellationToken ct) =>
        {
            if (ctx.UsuarioId is not { } id) return Results.Unauthorized();

            var usuario = await db.Usuarios.FirstAsync(u => u.Id == id, ct);
            var (f, mal, campo) = await FichaPersonal.RevisarAsync(
                p with { Cargo = usuario.Cargo, FechaIngreso = usuario.FechaIngreso }, db, ct);
            if (f is null) return Results.BadRequest(new { error = mal, campo });

            // El documento no se repite: en el personal de su empresa, o en el equipo de su cuenta.
            var nivel = await db.Database.SqlQuery<string?>(
                $"SELECT nivel_cuenta AS \"Value\" FROM usuarios WHERE id = {id}").FirstOrDefaultAsync(ct);
            string? de;
            if (nivel is null)
            {
                de = await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
                    .Where(u => u.EmpresaId == usuario.EmpresaId && u.Id != id
                                && u.TipoDocumento == f.TipoDocumento && u.NumeroDocumento == f.NumeroDocumento)
                    .Select(u => u.Nombre).FirstOrDefaultAsync(ct);
            }
            else
            {
                de = await db.Database.SqlQuery<string?>(
                    $"SELECT equipo_documento_de({f.TipoDocumento}, {f.NumeroDocumento}, {id}::uuid) AS \"Value\"")
                    .FirstOrDefaultAsync(ct);
            }
            if (de is not null)
                return Results.Conflict(new
                {
                    error = $"Ese {FichaPersonal.NombreDocumento(f.TipoDocumento)} ya figura a nombre de otra persona ({de}). " +
                            "Revísalo; si es tuyo, avisa a tu administrador.",
                    campo = "numeroDocumento"
                });

            f.Aplicar(usuario);
            usuario.FichaConfirmadaEn = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { mensaje = "Tus datos quedaron guardados.", nombre = usuario.Nombre });
        })
        .RequiereSesion()
        .WithSummary("Completar o corregir mi ficha");


        // =====================================================================
        // CAMBIAR MI CORREO
        //
        // Con contraseña y código del teléfono: con el correo se roba una
        // cuenta, y una sesión abierta y olvidada no basta. El cambio vale
        // cuando se abre el enlace que llega a la dirección NUEVA; a la
        // anterior se le avisa después.
        // =====================================================================
        grupo.MapPost("/correo", async (
            PeticionCambioCorreo p, ContextoPlataforma db, ContextoPeticion ctx, Cifrador cifrador,
            IServicioCorreo correo, OpcionesCorreo opciones, IHostEnvironment entorno, CancellationToken ct) =>
        {
            if (ctx.UsuarioId is not { } id) return Results.Unauthorized();

            var nuevo = (p.Nuevo ?? "").Trim();
            if (nuevo.Length is < 5 or > 254 || nuevo.Any(char.IsWhiteSpace) || nuevo.Count(c => c == '@') != 1
                || !nuevo.Split('@')[1].Contains('.'))
                return Results.BadRequest(new { error = "Ese correo no tiene forma de correo.", campo = "nuevo" });

            var usuario = await db.Usuarios.FirstAsync(u => u.Id == id, ct);
            if (string.Equals(usuario.Correo, nuevo, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Es el mismo correo que ya tienes.", campo = "nuevo" });

            if (!HashDeClaves.Verificar(p.Clave ?? "", usuario.ClaveHash, out _))
                return Results.Json(new { error = "La contraseña no es correcta.", campo = "clave" },
                                    statusCode: StatusCodes.Status401Unauthorized);

            if (!usuario.DosfaActivo || usuario.SecretoDosFactor is null)
                return Results.BadRequest(new { error = "Primero configura tu segundo factor." });

            var secreto = Convert.FromBase64String(cifrador.Descifrar(usuario.SecretoDosFactor));
            if (!SegundoFactor.Verificar(secreto, p.Codigo ?? "", usuario.DosfaUltimoPaso, out var paso))
                return Results.Json(new
                {
                    error = "El código de tu teléfono no coincide. Si tiene la hora desajustada, ponla en automática y prueba otra vez.",
                    campo = "codigo"
                }, statusCode: StatusCodes.Status401Unauthorized);
            usuario.DosfaUltimoPaso = paso;

            var libre = await db.Database.SqlQuery<bool>($"SELECT correo_disponible({nuevo}) AS \"Value\"").FirstAsync(ct);
            if (!libre) return Results.Conflict(new { error = "Ese correo ya está en uso.", campo = "nuevo" });

            // Un pedido nuevo anula los anteriores.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cambios_correo SET anulado_en = now()
                 WHERE usuario_id = {id} AND usado_en IS NULL AND anulado_en IS NULL
                """, ct);

            var (token, hash) = GeneradorTokens.Nuevo();
            var expira = DateTime.UtcNow.AddHours(HorasCambioCorreo);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cambios_correo (usuario_id, correo_nuevo, token_hash, expira_en)
                VALUES ({id}, {nuevo}, {hash}, {expira}::timestamptz)
                """, ct);
            await db.SaveChangesAsync(ct);

            var enlace = $"{opciones.UrlBase.TrimEnd('/')}/confirmar-correo/{token}";
            var m = Plantillas.ConfirmarCorreoNuevo(usuario.Nombre, nuevo, enlace, HorasCambioCorreo);
            var enviado = await correo.EnviarAsync(nuevo, m.Asunto, m.Html, m.Texto, ct);

            return Results.Ok(new
            {
                enviado,
                mensaje = enviado
                    ? $"Te mandamos un enlace a {nuevo}. Ábrelo para confirmar (vale {HorasCambioCorreo} horas). Hasta entonces sigues entrando con tu correo de siempre."
                    : "No se pudo enviar el correo de confirmación. Inténtalo en unos minutos.",
                enlaceDePrueba = entorno.IsDevelopment() && !opciones.Habilitado ? enlace : null
            });
        })
        .RequiereSesion()
        .WithSummary("Pedir el cambio de mi correo");


        // El enlace del correo nuevo. Sin sesión: puede abrirse en el teléfono.
        rutas.MapPost("/api/correo/confirmar", async (
            PeticionConfirmarCorreo p, ContextoPlataforma db, IServicioCorreo correo, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(p.Token)) return Results.BadRequest(new { error = "Falta el enlace." });

            var hash = GeneradorTokens.Hashear(p.Token.Trim());
            var texto = await db.Database.SqlQuery<string>(
                $"SELECT confirmar_cambio_correo({hash}) AS \"Value\"").FirstAsync(ct);

            using var doc = System.Text.Json.JsonDocument.Parse(texto);
            var r = doc.RootElement;
            string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

            switch (S("resultado"))
            {
                case "ok":
                    break;
                case "vencido":
                    return Results.BadRequest(new { error = "El enlace venció. Pide el cambio otra vez desde Mi cuenta › Seguridad.", codigo = "vencido" });
                case "ocupado":
                    return Results.Conflict(new { error = "Ese correo ya lo usa otra cuenta: no se cambió.", codigo = "ocupado" });
                default:
                    return Results.BadRequest(new { error = "El enlace no vale o ya se usó.", codigo = "no_existe" });
            }

            var anterior = S("anterior")!;
            var nuevo = S("nuevo")!;
            var nombre = S("nombre") ?? "";

            // El aviso a la dirección anterior, sin enlace (ver Plantillas).
            var m = Plantillas.CorreoCambiado(nombre, anterior, nuevo, nombre);
            await correo.EnviarAsync(anterior, m.Asunto, m.Html, m.Texto, ct);

            return Results.Ok(new { mensaje = $"Listo: desde ahora entras con {nuevo}. Avisamos a {anterior}.", nuevo });
        })
        .WithTags("Mi perfil")
        .WithSummary("Confirmar el correo nuevo (el enlace)");
    }
}