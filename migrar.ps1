# =============================================================================
# migrar.ps1
#
# Aplica las migraciones que falten a la base de la plataforma y comprueba que
# el aislamiento funciona de verdad.
#
#     .\migrar.ps1
#
#
# QUÉ HACE, EN ORDEN:
#
#   1. Copia la carpeta migraciones/ dentro del contenedor
#   2. Corre SOLO LAS QUE FALTEN sobre la base `plataforma`
#   3. Le pone al rol `plataforma_app` la contraseña del .env
#   4. Crea una base desechable, corre ahí las migraciones Y verificar.sql,
#      y la borra
#
# POR QUÉ EL PASO 4 NO SE HACE SOBRE LA BASE BUENA:
#
# `verificar.sql` crea dos empresas de mentira, dos usuarios, un cliente y un
# documento. Son necesarios para probar que una empresa no ve a la otra, pero
# no tienen nada que hacer en la base con la que vas a trabajar. Corriéndolo
# en una base aparte, se comprueba lo mismo y no queda basura.
#
# POR QUÉ SE CONECTA COMO `plataforma_app` Y NO COMO `postgres`:
#
# Un superusuario SE SALTA el Row Level Security siempre. Una prueba de
# aislamiento hecha como postgres pasa aunque el aislamiento no exista. Es el
# tipo de prueba que da confianza y no protege nada.
# =============================================================================

# 'Continue' y no 'Stop', a propósito.
#
# Con 'Stop', PowerShell aborta en cuanto una orden nativa escribe UNA SOLA
# línea en el canal de errores — aunque no sea un fallo. `psql` lo hace al
# preguntar por una tabla que todavía no existe, que es justo lo que hay que
# hacer para saber si la base está vacía.
#
# Los fallos de verdad se detectan con $LASTEXITCODE, que es lo que psql usa
# para decir si algo salió mal.
$ErrorActionPreference = 'Continue'

$contenedor = 'plataforma_postgres'
$baseReal   = 'plataforma'
$basePrueba = 'plataforma_verificacion'

Write-Host ""
Write-Host "=== Migraciones de la plataforma ===" -ForegroundColor Cyan
Write-Host ""


# --- Comprobaciones previas -------------------------------------------------

if (-not (Test-Path 'migraciones\001_fundacion.sql')) {
    Write-Host "No encuentro migraciones\001_fundacion.sql." -ForegroundColor Red
    Write-Host "Corre esto desde la raiz de reyban_plataforma."
    exit 1
}

$vivo = docker ps --filter "name=$contenedor" --format "{{.Names}}"
if (-not $vivo) {
    Write-Host "El contenedor $contenedor no esta corriendo." -ForegroundColor Red
    Write-Host "Arranca Docker Desktop y corre: docker compose up -d"
    exit 1
}


# --- Leer la contraseña del rol desde el .env -------------------------------

if (-not (Test-Path '.env')) {
    Write-Host "No encuentro el .env. Corre preparar.ps1 primero." -ForegroundColor Red
    exit 1
}

$claveApp = (Get-Content '.env' |
             Where-Object { $_ -match '^PLATAFORMA_CLAVE_APP=' }) -replace '^PLATAFORMA_CLAVE_APP=', ''

if (-not $claveApp) {
    Write-Host "El .env no tiene PLATAFORMA_CLAVE_APP." -ForegroundColor Red
    exit 1
}


# --- Función para correr un .sql dentro del contenedor ----------------------

function Invoke-Sql {
    param(
        [string]$Base,
        [string]$Archivo,
        [string]$Usuario = 'postgres',
        [string]$Clave   = $null
    )

    if ($Clave) {
        $salida = docker exec -e "PGPASSWORD=$Clave" $contenedor `
            psql -h localhost -U $Usuario -d $Base -v ON_ERROR_STOP=1 -q -f $Archivo 2>&1
    } else {
        $salida = docker exec $contenedor `
            psql -U $Usuario -d $Base -v ON_ERROR_STOP=1 -q -f $Archivo 2>&1
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "FALLO en $Archivo" -ForegroundColor Red
        $salida | ForEach-Object { Write-Host "    $_" }
        exit 1
    }

    return $salida
}


# --- 1. Copiar las migraciones al contenedor --------------------------------

Write-Host "[1] Copiando migraciones al contenedor..."
docker exec $contenedor rm -rf /tmp/migraciones 2>$null | Out-Null
docker cp migraciones "${contenedor}:/tmp/migraciones" | Out-Null
Write-Host "    Hecho." -ForegroundColor Green


# --- 2. Aplicar a la base real ----------------------------------------------

# LA LISTA SALE DE LA CARPETA, NO DE AQUÍ ESCRITA A MANO.
#
# Antes estaba fija, con los seis nombres puestos. Al añadir la séptima
# migración el guion siguió aplicando seis y no dijo nada: la base se quedó
# vieja y el fallo apareció mucho después, como una columna que no existe.
#
# `Sort-Object Name` es lo que hace que el orden sea 001, 002, 003… El número
# delante del nombre no es decoración: una migración que cree una tabla tiene
# que correr antes que la que le añade una columna.
$archivos = Get-ChildItem 'migraciones\*.sql' |
            Where-Object { $_.Name -ne 'verificar.sql' } |
            Sort-Object Name |
            ForEach-Object { $_.Name }

if ($archivos.Count -eq 0) {
    Write-Host "No hay migraciones en la carpeta migraciones\." -ForegroundColor Red
    exit 1
}

# ¿Cuáles están ya aplicadas?
#
# Se pregunta primero si la TABLA existe, no cuántas filas tiene. Consultar
# una tabla inexistente es un error de psql, y un error aquí no significa
# nada: significa que la base está limpia, que es el caso normal la primera
# vez.
$existeTabla = docker exec $contenedor psql -U postgres -d $baseReal -tAc `
    "SELECT count(*) FROM information_schema.tables WHERE table_name = 'migraciones_aplicadas'"

$aplicadas = @()
if ($existeTabla -and [int]$existeTabla.Trim() -gt 0) {
    $aplicadas = docker exec $contenedor psql -U postgres -d $baseReal -tAc `
        "SELECT nombre FROM migraciones_aplicadas" |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ }
}

# El nombre que cada migración escribe en la tabla es su archivo sin el .sql.
$pendientes = $archivos | Where-Object {
    $aplicadas -notcontains [System.IO.Path]::GetFileNameWithoutExtension($_)
}

if ($pendientes.Count -eq 0) {
    Write-Host "[2] La base esta al dia: $($aplicadas.Count) migracion(es)." -ForegroundColor Green
    Write-Host ""
    docker exec $contenedor psql -U postgres -d $baseReal -c `
        "SELECT nombre, aplicada_en FROM migraciones_aplicadas ORDER BY nombre"
} else {
    if ($aplicadas.Count -gt 0) {
        Write-Host "[2] La base tiene $($aplicadas.Count). Aplicando $($pendientes.Count) que falta(n)..." -ForegroundColor Yellow
    } else {
        Write-Host "[2] Base vacia. Aplicando las $($archivos.Count) migraciones..."
    }

    foreach ($a in $pendientes) {
        Write-Host "    $a"
        Invoke-Sql -Base $baseReal -Archivo "/tmp/migraciones/$a" | Out-Null
    }
    Write-Host "    Aplicadas." -ForegroundColor Green
}


# --- 3. La contraseña del rol de aplicación ---------------------------------

Write-Host "[3] Fijando la contrasena de plataforma_app..."

$sql = "ALTER ROLE plataforma_app PASSWORD '$claveApp'; GRANT CONNECT ON DATABASE $baseReal TO plataforma_app;"
docker exec $contenedor psql -U postgres -d $baseReal -v ON_ERROR_STOP=1 -q -c $sql | Out-Null

if ($LASTEXITCODE -ne 0) {
    Write-Host "    Fallo al fijar la contrasena." -ForegroundColor Red
    exit 1
}
Write-Host "    Hecho (la del .env)." -ForegroundColor Green


# --- 4. Verificación en una base desechable ---------------------------------

Write-Host "[4] Verificando el aislamiento en una base aparte..."

docker exec $contenedor psql -U postgres -d postgres -q -c `
    "DROP DATABASE IF EXISTS $basePrueba" | Out-Null
docker exec $contenedor psql -U postgres -d postgres -q -c `
    "CREATE DATABASE $basePrueba" | Out-Null

foreach ($a in $archivos) {
    Invoke-Sql -Base $basePrueba -Archivo "/tmp/migraciones/$a" | Out-Null
}

docker exec $contenedor psql -U postgres -d $basePrueba -q -c `
    "GRANT CONNECT ON DATABASE $basePrueba TO plataforma_app" | Out-Null

Write-Host ""
$resultado = docker exec -e "PGPASSWORD=$claveApp" $contenedor `
    psql -h localhost -U plataforma_app -d $basePrueba -v ON_ERROR_STOP=1 `
    -f /tmp/migraciones/verificar.sql 2>&1

$codigo = $LASTEXITCODE

$resultado | ForEach-Object {
    $linea = $_ -replace '^psql:.*?NOTICE:\s*', ''
    if ($linea -match 'PASARON')        { Write-Host "    $linea" -ForegroundColor Green }
    elseif ($linea -match 'ERROR|FALLO'){ Write-Host "    $linea" -ForegroundColor Red }
    elseif ($linea.Trim())              { Write-Host "    $linea" }
}

docker exec $contenedor psql -U postgres -d postgres -q -c `
    "DROP DATABASE IF EXISTS $basePrueba" | Out-Null

Write-Host ""

if ($codigo -ne 0) {
    Write-Host "LA VERIFICACION FALLO. No sigas hasta arreglarlo." -ForegroundColor Red
    exit 1
}


# --- Resumen -----------------------------------------------------------------

Write-Host "=== Listo ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Base:     $baseReal"
Write-Host "Adminer:  http://localhost:8081"
Write-Host "          Sistema: PostgreSQL | Servidor: postgres"
Write-Host "          Usuario: postgres   | Base: $baseReal"
Write-Host ""
Write-Host "Tablas creadas:"
docker exec $contenedor psql -U postgres -d $baseReal -tAc `
    "SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE'" |
    ForEach-Object { Write-Host "    $_ tablas" }
Write-Host ""
