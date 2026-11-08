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
# Quote project paths to support repository locations containing spaces.
$api = Start-Process -FilePath 'dotnet' -ArgumentList @('run','--project', ('"' + $apiProject + '"'),'--no-launch-profile') -PassThru
try {
  Write-Host 'Developer UI: http://127.0.0.1:5174/'
  Write-Host 'API: http://127.0.0.1:5127/api/v1/health'
  & npm --prefix $client run dev -- --host 127.0.0.1
  if ($LASTEXITCODE -ne 0) { throw 'Frontend developer server failed.' }
}
finally {
  if (-not $api.HasExited) { Stop-Process -Id $api.Id -Force }
}
