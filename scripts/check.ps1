# Verifies trusted Code Archeologist application source locally.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $root 'src/CodeArcheologist.Api/CodeArcheologist.Api.csproj'
$client = Join-Path $root 'src/CodeArcheologist.Client'
& dotnet build $apiProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'API build failed.' }
& npm ci --prefix $client --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { throw 'Reproducible frontend dependency installation (npm ci) failed.' }
& npm --prefix $client run typecheck
if ($LASTEXITCODE -ne 0) { throw 'TypeScript typecheck failed.' }
& npm --prefix $client run build
if ($LASTEXITCODE -ne 0) { throw 'Frontend production bundle build failed.' }
& node --test (Join-Path $root 'tests/security/local-access.test.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Local security integration tests failed.' }
Write-Host 'Local trusted-source build, typecheck and security checks passed. Cross-platform packaging remains pending.'
