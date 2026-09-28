# =============================================================================
# probar.ps1
#
# Recorre el sistema de punta a punta: ingreso, cambio de contrasena forzado,
# alta de dos empresas, y las comprobaciones de aislamiento entre ellas.
#
# CON LA API CORRIENDO EN OTRA VENTANA:
#
#     .\probar.ps1
#     .\probar.ps1 -Puerto 5255
#
#
# DEJA DATOS EN LA BASE: dos empresas de prueba con RUC al azar. Es
# desarrollo y no estorban. Para empezar de cero:
#
#     docker compose down -v ; docker compose up -d ; .\migrar.ps1
#
#
# CAMBIA EL .env: al obligar el primer cambio de contrasena, la del archivo
# deja de valer. El script genera una nueva y la escribe ahi, para que la
# proxima vez siga funcionando.
# =============================================================================

param(
    [int]$Puerto = 5255
)

$ErrorActionPreference = 'Continue'
$raiz = "http://localhost:$Puerto"
$fallos = 0

function Titulo($t) { Write-Host ""; Write-Host $t -ForegroundColor Cyan }
function Bien($t)   { Write-Host "    OK   $t" -ForegroundColor Green }
function Mal($t)    { Write-Host "    MAL  $t" -ForegroundColor Red; $script:fallos++ }

function Codigo($errorRecord) {
    try { return $errorRecord.Exception.Response.StatusCode.value__ } catch { return 0 }
}

function Llamar($metodo, $ruta, $cuerpo, $sesion) {
    $p = @{ Uri = "$raiz$ruta"; Method = $metodo; TimeoutSec = 10 }

    # Sin sesion para las llamadas anonimas, como las del enlace de invitacion.
    # Pasar WebSession = $null hace que Invoke-RestMethod se queje.
    if ($sesion) { $p.WebSession = $sesion }
    if ($cuerpo) {
        $p.ContentType = 'application/json'
        $p.Body = ($cuerpo | ConvertTo-Json -Depth 5)
    }
    return Invoke-RestMethod @p
}

function RucAlAzar { return "20" + (Get-Random -Minimum 100000000 -Maximum 999999999).ToString() }


# EL MISMO ALGORITMO QUE CUALQUIER APLICACION DE CODIGOS (RFC 6238).
#
# Aqui hace de telefono: con el secreto que devuelve la API, calcula el codigo
# de seis digitos de la ventana actual. Es lo que permite probar el segundo
# factor de punta a punta sin un movil delante.
function CodigoTotp($secretoBase64) {
    $clave    = [Convert]::FromBase64String($secretoBase64)
    $paso     = [long][Math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 30)
    $contador = [BitConverter]::GetBytes($paso)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($contador) }

    $hmac = New-Object System.Security.Cryptography.HMACSHA1
    $hmac.Key = $clave
    $hash = $hmac.ComputeHash($contador)

    $desp = $hash[$hash.Length - 1] -band 0x0F
    $num  = ((($hash[$desp]     -band 0x7F) -shl 24) -bor
             (($hash[$desp + 1] -band 0xFF) -shl 16) -bor
             (($hash[$desp + 2] -band 0xFF) -shl 8)  -bor
              ($hash[$desp + 3] -band 0xFF))

    return ($num % 1000000).ToString().PadLeft(6, '0')
}


# Configura el segundo factor de una sesion recien abierta que no lo tenia.
# Devuelve el secreto, para poder calcular codigos despues.
function Configurar2FA($sesion) {
    $prep = Llamar POST '/api/2fa/preparar' $null $sesion
    Llamar POST '/api/2fa/confirmar' @{ codigo = (CodigoTotp $prep.secreto) } $sesion | Out-Null
    return $prep.secreto
}


# --- Credenciales -------------------------------------------------------------

if (-not (Test-Path '.env')) {
    Write-Host "No encuentro el .env. Corre esto desde la raiz." -ForegroundColor Red
    exit 1
}

$env_ = Get-Content '.env'
$correo = (($env_ | Where-Object { $_ -match '^PLATAFORMA_ADMIN_CORREO=' }) -replace '^[^=]+=', '').Trim()
$clave  = (($env_ | Where-Object { $_ -match '^PLATAFORMA_ADMIN_CLAVE=' })  -replace '^[^=]+=', '').Trim()

Write-Host ""
Write-Host "=== Probando $raiz ===" -ForegroundColor Cyan


# =============================================================================
Titulo "1. Salud y puerta cerrada"
# =============================================================================
try {
    $r = Invoke-RestMethod "$raiz/salud" -TimeoutSec 5
    if ($r.estado -eq 'vivo') { Bien "/salud responde sin sesion" } else { Mal "responde raro" }
} catch {
    Mal "la API no responde. Arrancala: dotnet run --project Plataforma.Api"
    exit 1
}

try { Invoke-RestMethod "$raiz/api/yo" -TimeoutSec 5 | Out-Null; Mal "DEJO PASAR sin sesion" }
catch { if ((Codigo $_) -eq 401) { Bien "/api/yo sin sesion da 401" } else { Mal "esperaba 401, dio $(Codigo $_)" } }


# =============================================================================
Titulo "2. Ingreso del super administrador"
# =============================================================================
$sSuper = New-Object Microsoft.PowerShell.Commands.WebRequestSession
try {
    $r = Llamar POST '/api/sesion' @{ correo = $correo; clave = $clave } $sSuper
    Bien "entro"
    $debeCambiar = $r.cambioDeClaveForzado
} catch {
    Mal "no entro: revisa PLATAFORMA_ADMIN_CLAVE en el .env"
    exit 1
}

# EL SEGUNDO FACTOR ES OBLIGATORIO PARA TODOS, asi que la sesion nace a medias
# aunque la contrasena sea correcta.
try {
    Llamar GET '/api/admin/empresas' $null $sSuper | Out-Null
    Mal "DEJO PASAR sin superar el segundo factor"
} catch {
    if ((Codigo $_) -eq 401) { Bien "sin segundo factor no pasa de la puerta" }
    else { Mal "esperaba 401, dio $(Codigo $_)" }
}

if ($r.debeConfigurarSegundoFactor) {
    Bien "le toca configurar el segundo factor"
    try {
        $secretoSuper = Configurar2FA $sSuper
        Bien "segundo factor configurado y codigos de recuperacion entregados"
    } catch {
        Mal "no pudo configurar el segundo factor: $($_.Exception.Message)"
        exit 1
    }
} else {
    Mal "ya tenia segundo factor. Este guion espera una base recien migrada."
    exit 1
}


# =============================================================================
Titulo "3. El cambio de contrasena obligatorio bloquea de verdad"
# =============================================================================
if ($debeCambiar) {
    Bien "pide cambiar la contrasena"

    try {
        Llamar GET '/api/admin/empresas' $null $sSuper | Out-Null
        Mal "DEJO ENTRAR a otro endpoint sin cambiar la contrasena. No es obligatorio entonces."
    } catch {
        if ((Codigo $_) -eq 403) { Bien "403 en el resto de endpoints, como debe" }
        else { Mal "esperaba 403, dio $(Codigo $_)" }
    }

    # Contrasena nueva, y al .env para que la proxima vez funcione.
    $claveNueva = -join ((1..16) | ForEach-Object {
        "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789"[(Get-Random -Max 55)] })

    try {
        Llamar POST '/api/clave' @{ claveActual = $clave; claveNueva = $claveNueva } $sSuper | Out-Null
        Bien "contrasena cambiada"

        ($env_ -replace '^PLATAFORMA_ADMIN_CLAVE=.*', "PLATAFORMA_ADMIN_CLAVE=$claveNueva") |
            Set-Content '.env' -Encoding UTF8
        Bien ".env actualizado con la nueva"
        $clave = $claveNueva
    } catch {
        Mal "no pudo cambiarla: $($_.Exception.Message)"
        exit 1
    }

    # La sesion del cambio sigue valiendo; las demas se cerraron.
    try {
        Llamar GET '/api/admin/empresas' $null $sSuper | Out-Null
        Bien "ya entra al resto de endpoints"
    } catch {
        Mal "sigue bloqueado despues de cambiarla: $(Codigo $_)"
    }
} else {
    Bien "la contrasena ya estaba cambiada"
}


function TokenDe($enlace) {
    # El enlace es .../invitacion/<token>. Solo interesa el ultimo tramo.
    if (-not $enlace) { return $null }
    return ($enlace -split '/')[-1]
}

# =============================================================================
Titulo "4. Alta de dos empresas con su administrador"
# =============================================================================
$rucA = RucAlAzar
$rucB = RucAlAzar
$marca = Get-Random -Minimum 1000 -Maximum 9999

try {
    $altaA = Llamar POST '/api/admin/empresas' @{
        ruc = $rucA; razonSocial = "EXPORTADORA DEL SUR SAC"
        correoAdministrador = "ana$marca@empresa-a.pe"; nombreAdministrador = "Ana"
        modulos = @('facturacion')
    } $sSuper
    Bien "empresa A creada ($rucA) con modulo de facturacion"
} catch {
    Mal "no pudo crear la empresa A: $($_.Exception.Message)"
    exit 1
}

try {
    $altaB = Llamar POST '/api/admin/empresas' @{
        ruc = $rucB; razonSocial = "FERRETERIA EL CLAVO EIRL"
        correoAdministrador = "beto$marca@empresa-b.pe"; nombreAdministrador = "Beto"
        modulos = @()          # A PROPOSITO: sin modulos
    } $sSuper
    Bien "empresa B creada ($rucB) SIN modulos"
} catch {
    Mal "no pudo crear la empresa B: $($_.Exception.Message)"
    exit 1
}

try {
    Llamar POST '/api/admin/empresas' @{
        ruc = $rucA; razonSocial = "OTRA"
        correoAdministrador = "otro$marca@x.pe"; nombreAdministrador = "Otro"
    } $sSuper | Out-Null
    Mal "ACEPTO UN RUC REPETIDO"
} catch {
    if ((Codigo $_) -eq 409) { Bien "rechaza un RUC repetido" } else { Mal "esperaba 409, dio $(Codigo $_)" }
}


# =============================================================================
Titulo "5. Ana entra en su empresa"
# =============================================================================
$sAna = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$claveAnaNueva = "AnaSegura$marca!"

# NO HAY CONTRASENA QUE COPIAR: el alta manda un enlace por correo. Con
# MAIL_ENABLED=false la API devuelve el enlace en claro solo en desarrollo,
# que es lo que permite probar el flujo entero sin un buzon de por medio.
$tokenAna = TokenDe $altaA.administrador.enlaceDePrueba

if (-not $tokenAna) {
    Mal "el alta no devolvio enlace. Con MAIL_ENABLED=true esto es normal; para probar, ponlo en false."
    exit 1
}
Bien "el alta genero un enlace de invitacion"

try {
    $inv = Llamar GET "/api/invitacion/$tokenAna" $null $null
    if ($inv.correo -eq $altaA.administrador.correo) { Bien "el enlace dice de quien es, sin sesion" }
    else { Mal "el enlace resolvio otro correo: $($inv.correo)" }
    if ($inv.primeraVez) { Bien "figura como primera vez" } else { Mal "no figura como primera vez" }
    if ($inv.secreto -and $inv.direccionQr) { Bien "trae el QR del segundo factor" }
    else { Mal "no trae el segundo factor" }
} catch {
    Mal "no se pudo comprobar el enlace: $($_.Exception.Message)"
    exit 1
}

$secretoAna = $inv.secreto

try {
    Llamar POST "/api/invitacion/$tokenAna" @{
        clave = "corta"; secreto = $secretoAna; codigo = (CodigoTotp $secretoAna)
    } $null | Out-Null
    Mal "ACEPTO UNA CONTRASENA DE 5 CARACTERES"
} catch {
    if ((Codigo $_) -eq 400) { Bien "rechaza una contrasena demasiado corta" }
    else { Mal "esperaba 400, dio $(Codigo $_)" }
}

try {
    Llamar POST "/api/invitacion/$tokenAna" @{
        clave = $claveAnaNueva; secreto = $secretoAna; codigo = "000000"
    } $null | Out-Null
    Mal "ACEPTO UN CODIGO INVENTADO"
} catch {
    if ((Codigo $_) -eq 400) { Bien "rechaza un codigo que no cuadra con el QR" }
    else { Mal "esperaba 400, dio $(Codigo $_)" }
}

try {
    $fin = Llamar POST "/api/invitacion/$tokenAna" @{
        clave = $claveAnaNueva; secreto = $secretoAna; codigo = (CodigoTotp $secretoAna)
    } $null
    Bien "eligio contrasena y segundo factor desde el enlace"

    if ($fin.codigosRecuperacion.Count -eq 8) { Bien "le dio 8 codigos de recuperacion" }
    else { Mal "esperaba 8 codigos, dio $($fin.codigosRecuperacion.Count)" }

    $codigoRescateAna = $fin.codigosRecuperacion[0]
} catch {
    Mal "no pudo terminar la invitacion: $($_.Exception.Message)"
    exit 1
}

try {
    Llamar POST "/api/invitacion/$tokenAna" @{
        clave = "OtraCosa$marca!"; secreto = $secretoAna; codigo = (CodigoTotp $secretoAna)
    } $null | Out-Null
    Mal "EL ENLACE SIRVIO DOS VECES"
} catch {
    if ((Codigo $_) -eq 404) { Bien "el enlace ya no sirve una segunda vez" }
    else { Mal "esperaba 404, dio $(Codigo $_)" }
}

try {
    $r = Llamar POST '/api/sesion' @{ correo = $altaA.administrador.correo; clave = $claveAnaNueva } $sAna
    Bien "entro con la contrasena que eligio"
    if ($r.cambioDeClaveForzado) { Mal "le pide cambiarla, y acaba de elegirla ella" }
    else { Bien "no le pide cambiarla, porque la eligio ella" }
    if ($r.debeConfigurarSegundoFactor) { Mal "le pide configurar el factor, y ya lo hizo en el enlace" }
    else { Bien "ya tiene segundo factor: le toca el codigo" }
} catch {
    Mal "Ana no pudo entrar: $($_.Exception.Message)"
    exit 1
}

try {
    Llamar POST '/api/2fa' @{ codigo = "123456" } $sAna | Out-Null
    Mal "ACEPTO UN CODIGO CUALQUIERA"
} catch {
    if ((Codigo $_) -eq 401) { Bien "rechaza un codigo inventado" }
    else { Mal "esperaba 401, dio $(Codigo $_)" }
}

$codigoAna = CodigoTotp $secretoAna
try {
    Llamar POST '/api/2fa' @{ codigo = $codigoAna } $sAna | Out-Null
    Bien "supero el segundo factor"
} catch {
    Mal "no supero el segundo factor: $($_.Exception.Message)"
    exit 1
}

# UN CODIGO NO SIRVE DOS VECES, aunque siga dentro de sus treinta segundos.
# Es lo que impide que quien lo vea por encima del hombro lo reutilice.
try {
    Llamar POST '/api/2fa' @{ codigo = $codigoAna } $sAna | Out-Null
    Mal "EL MISMO CODIGO SE ACEPTO DOS VECES"
} catch {
    if ((Codigo $_) -eq 401) { Bien "el mismo codigo no se acepta dos veces" }
    else { Mal "esperaba 401, dio $(Codigo $_)" }
}


# --- Los codigos de recuperacion, que son la salida si se pierde el telefono
$sRescate = New-Object Microsoft.PowerShell.Commands.WebRequestSession
try {
    Llamar POST '/api/sesion' @{ correo = $altaA.administrador.correo; clave = $claveAnaNueva } $sRescate | Out-Null

    try {
        Llamar POST '/api/2fa/recuperacion' @{ codigo = "ZZZZZ-ZZZZZ" } $sRescate | Out-Null
        Mal "ACEPTO UN CODIGO DE RECUPERACION INVENTADO"
    } catch {
        if ((Codigo $_) -eq 401) { Bien "rechaza un codigo de recuperacion inventado" }
        else { Mal "esperaba 401, dio $(Codigo $_)" }
    }

    $r = Llamar POST '/api/2fa/recuperacion' @{ codigo = $codigoRescateAna } $sRescate
    Bien "entro con un codigo de recuperacion (le quedan $($r.quedan))"

    try {
        Llamar POST '/api/2fa/recuperacion' @{ codigo = $codigoRescateAna } $sRescate | Out-Null
        Mal "EL CODIGO DE RECUPERACION SIRVIO DOS VECES"
    } catch {
        if ((Codigo $_) -eq 401) { Bien "cada codigo de recuperacion se quema al usarlo" }
        else { Mal "esperaba 401, dio $(Codigo $_)" }
    }
} catch {
    Mal "fallo la prueba de recuperacion: $($_.Exception.Message)"
}

try {
    $yoAna = Llamar GET '/api/yo' $null $sAna

    if ($yoAna.empresa.ruc -eq $rucA) { Bien "ve su empresa: $($yoAna.empresa.razonSocial)" }
    else { Mal "no ve la empresa correcta" }

    if (-not $yoAna.usuario.esSuperAdmin) { Bien "NO es super administradora" }
    else { Mal "figura como super administradora. Grave." }

    if ($yoAna.permisos -contains 'facturacion.emitir') { Bien "trae permisos del rol Administrador" }
    else { Mal "no trae permisos: $($yoAna.permisos -join ', ')" }

    if ($yoAna.permisos -contains 'plataforma.empresas_gestionar') {
        Mal "TIENE PERMISOS DE PLATAFORMA. El admin de empresa no debe tenerlos."
    } else { Bien "sin permisos de plataforma, que son solo tuyos" }

    if ($yoAna.menu.codigo -contains 'facturacion') { Bien "su menu trae facturacion" }
    else { Mal "su menu sale vacio y su empresa si contrato el modulo" }
} catch {
    Mal "/api/yo fallo para Ana: $($_.Exception.Message)"
}


# =============================================================================
Titulo "6. Beto entra, y su empresa no contrato modulos"
# =============================================================================
$sBeto = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$claveBetoNueva = "BetoSeguro$marca!"
$tokenBeto = TokenDe $altaB.administrador.enlaceDePrueba

try {
    $invB = Llamar GET "/api/invitacion/$tokenBeto" $null $null
    $secretoBeto = $invB.secreto

    Llamar POST "/api/invitacion/$tokenBeto" @{
        clave = $claveBetoNueva; secreto = $secretoBeto; codigo = (CodigoTotp $secretoBeto)
    } $null | Out-Null

    Llamar POST '/api/sesion' @{ correo = $altaB.administrador.correo; clave = $claveBetoNueva } $sBeto | Out-Null
    Llamar POST '/api/2fa' @{ codigo = (CodigoTotp $secretoBeto) } $sBeto | Out-Null
    $yoBeto = Llamar GET '/api/yo' $null $sBeto
    Bien "entro"

    if ($yoBeto.menu.Count -eq 0) { Bien "su menu sale vacio, que es lo correcto sin modulos" }
    else { Mal "le sale menu y su empresa no contrato nada" }

    if ($yoBeto.permisos -contains 'facturacion.emitir') {
        Bien "tiene el permiso (es administrador) aunque no pueda usarlo sin el modulo"
    }
} catch {
    Mal "Beto no pudo entrar: $($_.Exception.Message)"
}


# =============================================================================
Titulo "7. EL AISLAMIENTO, que es lo que de verdad importa"
# =============================================================================
try {
    $usuariosAna = Llamar GET '/api/usuarios' $null $sAna
    if ($usuariosAna.Count -eq 1 -and $usuariosAna[0].correo -eq $altaA.administrador.correo) {
        Bien "Ana ve un solo usuario: ella misma"
    } else {
        Mal "Ana ve $($usuariosAna.Count) usuarios: $($usuariosAna.correo -join ', ')"
    }
} catch { Mal "fallo al listar usuarios: $($_.Exception.Message)" }

try {
    Llamar GET '/api/admin/empresas' $null $sAna | Out-Null
    Mal "ANA VE LA LISTA DE EMPRESAS. Veria a todos tus clientes."
} catch {
    if ((Codigo $_) -eq 403) { Bien "Ana no entra al panel de plataforma" }
    else { Mal "esperaba 403, dio $(Codigo $_)" }
}

try {
    $idB = $altaB.empresa.id
    Llamar PUT "/api/admin/empresas/$idB/modulos/facturacion" $null $sAna | Out-Null
    Mal "ANA LE ACTIVO UN MODULO A LA EMPRESA DE BETO."
} catch {
    if ((Codigo $_) -eq 403) { Bien "Ana no puede activar modulos a nadie" }
    else { Mal "esperaba 403, dio $(Codigo $_)" }
}

try {
    Llamar POST '/api/usuarios' @{
        correo = $altaB.administrador.correo; nombre = "Colado"; roles = @()
    } $sAna | Out-Null
    Mal "ANA CREO UN USUARIO CON EL CORREO DE BETO"
} catch {
    if ((Codigo $_) -eq 409) { Bien "el correo de Beto figura como ocupado, sin decir de quien es" }
    else { Mal "esperaba 409, dio $(Codigo $_)" }
}


# =============================================================================
Titulo "8. Suspender una empresa corta sus sesiones"
# =============================================================================
try {
    $idA = $altaA.empresa.id
    Llamar POST "/api/admin/empresas/$idA/suspender" @{ motivo = "prueba" } $sSuper | Out-Null
    Bien "empresa A suspendida"

    try {
        Llamar GET '/api/yo' $null $sAna | Out-Null
        Mal "ANA SIGUE DENTRO con la empresa suspendida"
    } catch {
        if ((Codigo $_) -eq 401) { Bien "la sesion de Ana dejo de valer al instante" }
        else { Mal "esperaba 401, dio $(Codigo $_)" }
    }

    Llamar POST "/api/admin/empresas/$idA/reactivar" $null $sSuper | Out-Null
    Bien "empresa A reactivada"
} catch {
    Mal "fallo la suspension: $($_.Exception.Message)"
}


# --- Resumen -----------------------------------------------------------------

Write-Host ""
if ($fallos -eq 0) {
    Write-Host "=== TODO BIEN ===" -ForegroundColor Green
    Write-Host ""
    Write-Host "Empresas de prueba creadas:"
    Write-Host "  A  $rucA  $($altaA.administrador.correo)  clave: $claveAnaNueva"
    Write-Host "  B  $rucB  $($altaB.administrador.correo)  clave: $claveBetoNueva"
} else {
    Write-Host "=== $fallos comprobacion(es) fallaron ===" -ForegroundColor Red
}
Write-Host ""
