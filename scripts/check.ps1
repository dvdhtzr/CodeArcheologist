# Verifies trusted Code Archeologist application source locally.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $root 'src/CodeArcheologist.Api/CodeArcheologist.Api.csproj'
$client = Join-Path $root 'src/CodeArcheologist.Client'
$lockfile = Join-Path $client 'package-lock.json'
if (-not (Test-Path -LiteralPath $lockfile -PathType Leaf)) {
  throw "Frontend lockfile is missing: $lockfile. From the repository root, check 'git status --short' and restore the tracked file with 'git restore -- src/CodeArcheologist.Client/package-lock.json'."
}
if (Get-NetTCPConnection -LocalPort 5127 -State Listen -ErrorAction SilentlyContinue) {
  throw 'Port 5127 is already in use. Stop the Code Archeologist developer server before running security tests.'
}
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
