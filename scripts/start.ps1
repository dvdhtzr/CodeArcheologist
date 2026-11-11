# Launches only trusted Code Archeologist developer processes.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $root 'src/CodeArcheologist.Api/CodeArcheologist.Api.csproj'
$client = Join-Path $root 'src/CodeArcheologist.MissionControl'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required.' }
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw 'Node.js and npm are required.' }
# Use the exact Release build validated by scripts/check.ps1. Plain 'dotnet
# run' defaults to Debug and can trigger a separate restore/build with stale
# assets; never turn a successful Release verification into a Debug startup.
& dotnet build $apiProject --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'API Release build failed; developer processes were not started.' }
$apiDll = Join-Path $root 'src/CodeArcheologist.Api/bin/Release/net10.0/CodeArcheologist.Api.dll'
if (-not (Test-Path -LiteralPath $apiDll -PathType Leaf)) {
  throw "Verified Release API assembly is missing: $apiDll"
}
if (-not (Test-Path (Join-Path $client 'node_modules'))) {
  & npm ci --prefix $client --no-audit --no-fund
  if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency installation failed.' }
}
# Generate a new 128-bit local pairing code for this run.
$secretBytes = New-Object byte[] 16
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($secretBytes) } finally { $rng.Dispose() }
$code = ([BitConverter]::ToString($secretBytes)).Replace('-', '').ToLowerInvariant()
$env:CODE_ARCHEOLOGIST_BOOTSTRAP_CODE = $code
$env:CODE_ARCHEOLOGIST_DEV_UI = '1'
try {
  # Launch only the already verified Release assembly. 'exec' does not
  # compile, restore packages, or select a separate Debug configuration.
  # Quote the DLL path so repository directories may contain spaces.
  $api = Start-Process -FilePath 'dotnet' -ArgumentList @('exec', ('"' + $apiDll + '"')) -PassThru
}
finally {
  Remove-Item Env:CODE_ARCHEOLOGIST_BOOTSTRAP_CODE -ErrorAction SilentlyContinue
  Remove-Item Env:CODE_ARCHEOLOGIST_DEV_UI -ErrorAction SilentlyContinue
}
Write-Host "Local browser pairing code: $code"
try {
  Write-Host 'Mission Control (primary UI): http://127.0.0.1:5175/'
  Write-Host 'Legacy explorer remains available separately via npm --prefix src/CodeArcheologist.Client run dev'
  Write-Host 'API: http://127.0.0.1:5127/api/v1/health'
  & npm --prefix $client run dev -- --host 127.0.0.1
  if ($LASTEXITCODE -ne 0) { throw 'Frontend developer server failed.' }
}
finally {
  if (-not $api.HasExited) { Stop-Process -Id $api.Id -Force }
}
