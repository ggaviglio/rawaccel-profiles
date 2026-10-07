$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent

& (Join-Path $PSScriptRoot "publish.ps1")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$publishFolder = Join-Path $repoRoot "src\RawAccelProfiles\bin\Release\net10.0\win-x64\publish"
$exePath = Join-Path $publishFolder "RawAccelProfiles.exe"

$version = ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)).ProductVersion
if (-not $version) { $version = "0.1.0" }
$version = $version -replace '\+.*$', ''

$distFolder = Join-Path $repoRoot "dist"
New-Item -ItemType Directory -Force -Path $distFolder | Out-Null

$folderName = "RawAccelProfiles_v${version}"
$zipName = "${folderName}.zip"
$zipPath = Join-Path $distFolder $zipName

# Stage files into a named subfolder so the zip extracts cleanly
$stagingFolder = Join-Path $distFolder $folderName
if (Test-Path $stagingFolder) { Remove-Item $stagingFolder -Recurse -Force }
Copy-Item -Path $publishFolder -Destination $stagingFolder -Recurse

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $stagingFolder -DestinationPath $zipPath
Remove-Item $stagingFolder -Recurse -Force

Write-Host "Package ready: dist\$zipName" -ForegroundColor Green
