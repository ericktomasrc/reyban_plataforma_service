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
    $p = @{ Uri = "$raiz$ruta"; Method = $metodo; WebSession = $sesion; TimeoutSec = 10 }
    if ($cuerpo) {
        $p.ContentType = 'application/json'
        $p.Body = ($cuerpo | ConvertTo-Json -Depth 5)
    }
    return Invoke-RestMethod @p
}

function RucAlAzar { return "20" + (Get-Random -Minimum 100000000 -Maximum 999999999).ToString() }


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
$claveAna = $altaA.administrador.claveTemporal

try {
    $r = Llamar POST '/api/sesion' @{ correo = $altaA.administrador.correo; clave = $claveAna } $sAna
    Bien "entro con la contrasena temporal"
    if ($r.cambioDeClaveForzado) { Bien "le pide cambiarla, como debe" } else { Mal "no le pide cambiarla" }
} catch {
    Mal "Ana no pudo entrar: $($_.Exception.Message)"
    exit 1
}

$claveAnaNueva = "AnaSegura$marca!"
try {
    Llamar POST '/api/clave' @{ claveActual = $claveAna; claveNueva = $claveAnaNueva } $sAna | Out-Null
    Bien "cambio su contrasena"
} catch { Mal "no pudo cambiarla: $($_.Exception.Message)" }

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
$claveBeto = $altaB.administrador.claveTemporal
$claveBetoNueva = "BetoSeguro$marca!"

try {
    Llamar POST '/api/sesion' @{ correo = $altaB.administrador.correo; clave = $claveBeto } $sBeto | Out-Null
    Llamar POST '/api/clave' @{ claveActual = $claveBeto; claveNueva = $claveBetoNueva } $sBeto | Out-Null
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
