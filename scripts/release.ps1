$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent

& (Join-Path $PSScriptRoot "publish.ps1")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$publishFolder = Join-Path $repoRoot "src\RawAccelProfiles\bin\Release\net10.0\win-x64\publish"
$exeName = "RawAccelProfiles.exe"
$exePath = Join-Path $publishFolder $exeName

$version = ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)).ProductVersion
if (-not $version) { $version = "0.1.0" }
$version = $version -replace '\+.*$', ''

$distFolder = Join-Path $repoRoot "dist"
New-Item -ItemType Directory -Force -Path $distFolder | Out-Null

$folderName = "RawAccelProfiles_v${version}"
$zipName = "${folderName}.zip"
$zipPath = Join-Path $distFolder $zipName

# Stage only the files that actually ship, into a named subfolder so the zip extracts
# cleanly instead of spraying loose files into the user's current directory.
#
# This is an allowlist on purpose, not "copy everything and delete the bad bits". The
# publish folder is also where the app writes profiles.json (AppContext.BaseDirectory),
# so running the published exe even once from there leaves real user profiles and
# settings sitting next to it -- and `dotnet clean` won't remove them, because they
# aren't tracked build outputs. A blocklist would ship any file nobody thought to
# exclude; an allowlist ships nothing nobody opted in.
$shipFiles = @($exeName)

$stagingFolder = Join-Path $distFolder $folderName
if (Test-Path $stagingFolder) { Remove-Item $stagingFolder -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stagingFolder | Out-Null

foreach ($pattern in $shipFiles) {
    $matched = Get-ChildItem -Path (Join-Path $publishFolder $pattern) -ErrorAction SilentlyContinue
    if (-not $matched) { Write-Error "Expected to ship '$pattern' but it is not in $publishFolder"; exit 1 }
    Copy-Item -Path $matched.FullName -Destination $stagingFolder
}

# Say out loud what was left behind, so a genuinely-needed new sidecar file can't be
# dropped silently by the allowlist above.
$skipped = Get-ChildItem -Path $publishFolder -File |
    Where-Object { $_.Name -notin (Get-ChildItem $stagingFolder -File).Name }
if ($skipped) {
    Write-Host "Not shipped (excluded by design):" -ForegroundColor Yellow
    $skipped | ForEach-Object { Write-Host "  $($_.Name)" -ForegroundColor Yellow }
}

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path $stagingFolder -DestinationPath $zipPath
Remove-Item $stagingFolder -Recurse -Force

Write-Host "Package ready: dist\$zipName" -ForegroundColor Green
