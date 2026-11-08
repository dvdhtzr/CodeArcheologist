# Verifies trusted Code Archeologist application source locally.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $root 'src/CodeArcheologist.Api/CodeArcheologist.Api.csproj'
$client = Join-Path $root 'src/CodeArcheologist.Client'
& dotnet build $apiProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'API build failed.' }
& npm install --prefix $client --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency installation failed.' }
& npm --prefix $client run typecheck
if ($LASTEXITCODE -ne 0) { throw 'TypeScript typecheck failed.' }
& npm --prefix $client run build
if ($LASTEXITCODE -ne 0) { throw 'Frontend production bundle build failed.' }
Write-Host 'Builds/typecheck completed. Runtime, security and packaging checks are not yet verified.'
