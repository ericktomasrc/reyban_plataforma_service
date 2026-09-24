# =============================================================================
# preparar.ps1
#
# Deja el proyecto listo: arregla el .gitignore, genera el .env con secretos
# nuevos y comprueba que todo esté en su sitio.
#
# CÓMO SE CORRE, desde la raíz de reyban_plataforma:
#
#     .\preparar.ps1
#
# Si Windows se queja de que no se pueden ejecutar scripts:
#
#     powershell -ExecutionPolicy Bypass -File .\preparar.ps1
#
#
# POR QUÉ UN SCRIPT Y NO COMANDOS PEGADOS:
#
# Un bloque de PowerShell con líneas en blanco dentro, pegado en la consola,
# se parte: la consola cree que el comando terminó en la línea vacía y trata
# el resto como texto suelto. Es lo que acaba de pasar dos veces. En un
# archivo eso no ocurre.
# =============================================================================

$ErrorActionPreference = 'Stop'

Write-Host ""
Write-Host "=== Preparando reyban_plataforma ===" -ForegroundColor Cyan
Write-Host ""


# --- Comprobar que estamos donde toca --------------------------------------

if (-not (Test-Path 'Plataforma.sln')) {
    Write-Host "No encuentro Plataforma.sln." -ForegroundColor Red
    Write-Host "Corre este script desde la raiz de reyban_plataforma."
    exit 1
}


# --- 1. Limpiar el .env corrupto -------------------------------------------

if (Test-Path '.env') {
    $primera = (Get-Content '.env' -TotalCount 1)
    if ($primera -like '*RandomNumberGenerator*' -or $primera -like '$*') {
        Write-Host "[1] El .env tenia el script pegado dentro. Lo borro." -ForegroundColor Yellow
        Remove-Item '.env'
    } else {
        Write-Host "[1] Ya existe un .env valido." -ForegroundColor Yellow
        Write-Host "    Si quieres regenerarlo, borralo a mano y vuelve a correr esto."
        Write-Host "    OJO: la llave maestra cambiaria y lo ya cifrado quedaria ilegible."
        $seguir = Read-Host "    Escribe SI para regenerarlo de todas formas"
        if ($seguir -eq 'SI') { Remove-Item '.env' } else { $saltarEnv = $true }
    }
}


# --- 2. Arreglar el .gitignore ----------------------------------------------

Write-Host "[2] Revisando el .gitignore..."

if (-not (Test-Path '.gitignore')) {
    Write-Host "    No existe. Lo creo." -ForegroundColor Yellow
    Set-Content '.gitignore' -Encoding UTF8 -Value @('# .gitignore')
}

$lineas = @(Get-Content '.gitignore')

# Cortar el bloque que se colo como texto, si esta.
$marca = $lineas | Select-String -SimpleMatch "@'" | Select-Object -First 1
if ($marca) {
    Write-Host "    Encontre el bloque pegado por error. Lo corto." -ForegroundColor Yellow
    $corte = $marca.LineNumber - 2
    if ($corte -ge 0) {
        $lineas = $lineas[0..$corte]
    } else {
        $lineas = @()
    }
    Set-Content '.gitignore' -Encoding UTF8 -Value $lineas
}

# Anadir las reglas propias, si no estan ya.
if (-not ($lineas -contains '# --- Plataforma ---')) {
    Add-Content '.gitignore' -Encoding UTF8 -Value @(
        '',
        '# --- Plataforma ---',
        '',
        '# Los secretos. Lo mas importante de este archivo.',
        '.env',
        '.env.*',
        '!.env.ejemplo',
        '',
        '# Certificados digitales, si alguno acaba por aqui.',
        '*.pfx',
        '*.p12',
        '',
        '# Puertos y secretos de desarrollo.',
        '**/Properties/launchSettings.json',
        '',
        '# Se instala con npm install, no se versiona.',
        'node_modules/',
        '',
        '# Lo genera el build del front. Se recompila.',
        'Plataforma.Api/wwwroot/'
    )
    Write-Host "    Reglas anadidas." -ForegroundColor Green
} else {
    Write-Host "    Ya tenia las reglas." -ForegroundColor Green
}


# --- 3. Generar el .env ------------------------------------------------------

if (-not $saltarEnv) {

    Write-Host "[3] Generando secretos..."

    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes = [byte[]]::new(32)

    $rng.GetBytes($bytes); $llave = [Convert]::ToBase64String($bytes)

    # Las contrasenas de base de datos van sin +, / ni = para que no haya
    # que escaparlas en la cadena de conexion ni en la URL de Adminer.
    function Nueva-Clave {
        $b = [byte[]]::new(24)
        $rng.GetBytes($b)
        return ([Convert]::ToBase64String($b) -replace '[+/=]', 'x').Substring(0, 24)
    }

    $clavePg  = Nueva-Clave
    $claveApp = Nueva-Clave
    $claveAdm = Nueva-Clave

    Set-Content '.env' -Encoding UTF8 -Value @(
        '# =============================================================',
        '# Secretos de desarrollo. NUNCA entra en git.',
        '# Generado por preparar.ps1',
        '# =============================================================',
        '',
        '# ----- PostgreSQL -----',
        'POSTGRES_DB=plataforma',
        'POSTGRES_USER=postgres',
        "POSTGRES_PASSWORD=$clavePg",
        '',
        '# ----- El rol de la aplicacion -----',
        '#',
        '# La aplicacion NO se conecta como postgres. Usa plataforma_app,',
        '# que no tiene BYPASSRLS: si se conectara como superusuario, el',
        '# aislamiento entre empresas dejaria de existir en silencio.',
        "PLATAFORMA_CLAVE_APP=$claveApp",
        "PLATAFORMA_CADENA_CONEXION=Host=localhost;Port=5433;Database=plataforma;Username=plataforma_app;Password=$claveApp",
        '',
        '# ----- Llave maestra -----',
        '#',
        '# Cifra el secreto del segundo factor y las claves de API del',
        '# servicio de facturacion.',
        '#',
        '# SI SE PIERDE, todo eso queda indescifrable y no hay vuelta atras.',
        '# Guardala TAMBIEN en un gestor de contrasenas.',
        "PLATAFORMA_LLAVE_MAESTRA=$llave",
        '',
        '# ----- Primer super administrador -----',
        '#',
        '# Lo crea la aplicacion al arrancar si no existe ninguno, y obliga',
        '# a cambiar la clave en el primer ingreso.',
        'PLATAFORMA_ADMIN_CORREO=reybanconsulting@gmail.com',
        "PLATAFORMA_ADMIN_CLAVE=$claveAdm",
        '',
        '# ----- Servicio de facturacion -----',
        'FACTURACION_BASE_URL=http://localhost:5000'
    )

    Write-Host "    .env creado." -ForegroundColor Green
    Write-Host ""
    Write-Host "    ======================================================" -ForegroundColor Yellow
    Write-Host "    COPIA ESTO A TU GESTOR DE CONTRASENAS AHORA" -ForegroundColor Yellow
    Write-Host "    ======================================================" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "    Llave maestra:    $llave"
    Write-Host "    Clave del admin:  $claveAdm"
    Write-Host ""
}


# --- 4. Comprobar que git no ve el .env -------------------------------------

Write-Host "[4] Comprobando que git ignora el .env..."

$visto = git status --short --untracked-files=all 2>$null | Select-String -SimpleMatch '.env'
if ($visto -and $visto -notlike '*ejemplo*') {
    Write-Host "    PELIGRO: git ve el .env. No hagas commit." -ForegroundColor Red
    Write-Host "    $visto"
} else {
    Write-Host "    Bien: el .env esta ignorado." -ForegroundColor Green
}


# --- 5. Comprobar el SDK -----------------------------------------------------

Write-Host "[5] Comprobando el SDK de .NET..."

$sdks = dotnet --list-sdks 2>$null
$tiene10 = $sdks | Select-String -SimpleMatch '10.'

if ($tiene10) {
    Write-Host "    SDK 10 instalado." -ForegroundColor Green
} else {
    Write-Host "    FALTA EL SDK DE .NET 10." -ForegroundColor Red
    Write-Host ""
    Write-Host "    Tienes instalado:"
    $sdks | ForEach-Object { Write-Host "      $_" }
    Write-Host ""
    Write-Host "    Descargalo de https://dotnet.microsoft.com/download/dotnet/10.0"
    Write-Host "    (el instalador que dice SDK, no el que dice Runtime)"
    Write-Host ""
    Write-Host "    Hasta entonces 'dotnet build' va a fallar con NETSDK1045."
}

Write-Host ""
Write-Host "=== Listo ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Siguiente paso: instala el SDK 10 si falta, y luego:"
Write-Host "    dotnet build"
Write-Host "    docker compose up -d"
Write-Host ""
