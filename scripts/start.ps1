# Launches only trusted Code Archeologist developer processes.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $root 'src/CodeArcheologist.Api/CodeArcheologist.Api.csproj'
$client = Join-Path $root 'src/CodeArcheologist.Client'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required.' }
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw 'Node.js and npm are required.' }
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
  # Quote project paths to support repository locations containing spaces.
  $api = Start-Process -FilePath 'dotnet' -ArgumentList @('run','--project', ('"' + $apiProject + '"'),'--no-launch-profile') -PassThru
}
finally {
  Remove-Item Env:CODE_ARCHEOLOGIST_BOOTSTRAP_CODE -ErrorAction SilentlyContinue
  Remove-Item Env:CODE_ARCHEOLOGIST_DEV_UI -ErrorAction SilentlyContinue
}
Write-Host "Local browser pairing code: $code"
try {
  Write-Host 'Developer UI: http://127.0.0.1:5174/'
  Write-Host 'API: http://127.0.0.1:5127/api/v1/health'
  & npm --prefix $client run dev -- --host 127.0.0.1
  if ($LASTEXITCODE -ne 0) { throw 'Frontend developer server failed.' }
}
finally {
  if (-not $api.HasExited) { Stop-Process -Id $api.Id -Force }
}
