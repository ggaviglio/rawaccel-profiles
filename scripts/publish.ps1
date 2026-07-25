$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $repoRoot "src\MouseApp\MouseApp.csproj"

Write-Host "Cleaning..." -ForegroundColor Cyan
dotnet clean $csproj
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Publishing..." -ForegroundColor Cyan
dotnet publish $csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:PublishReadyToRun=true `
    /p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Publish complete." -ForegroundColor Green
