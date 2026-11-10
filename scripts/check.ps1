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
$contractProject = Join-Path $root 'tests/ollama-contract/OllamaContractProbe.csproj'
& dotnet run --project $contractProject --configuration Release -- --self-test
if ($LASTEXITCODE -ne 0) { throw 'Model-free Ollama structured-output contract checks failed.' }
$adapterProject = Join-Path $root 'tests/maf-adapter-contract/MafAdapterContractProbe.csproj'
& dotnet run --project $adapterProject --configuration Release -- --gate-b-self-test
if ($LASTEXITCODE -ne 0) { throw 'Gate B model-free structural contract checks failed.' }
& dotnet run --project $adapterProject --configuration Release -- --gate-c1-self-test
if ($LASTEXITCODE -ne 0) { throw 'Gate C1 model-free investigation contract checks failed.' }
& dotnet run --project $adapterProject --configuration Release -- --gate-c5-self-test
if ($LASTEXITCODE -ne 0) { throw 'Gate C5a model-free report and persistence checks failed.' }
& dotnet run --project $adapterProject --configuration Release -- --gate-c6b-self-test
if ($LASTEXITCODE -ne 0) { throw 'Gate C6b model-free ownership, fencing and worker checks failed.' }
$labProject = Join-Path $root 'tests/evaluation-lab/EvaluationLab.csproj'
& dotnet run --project $labProject --configuration Release -- --validate-corpus
if ($LASTEXITCODE -ne 0) { throw 'C6c evaluation corpus validation failed.' }
& dotnet run --project $labProject --configuration Release --no-build -- --self-test
if ($LASTEXITCODE -ne 0) { throw 'C6c evaluation lab model-free self-test failed.' }
& npm ci --prefix $client --no-audit --no-fund
if ($LASTEXITCODE -ne 0) { throw 'Reproducible frontend dependency installation (npm ci) failed.' }
& npm --prefix $client run typecheck
if ($LASTEXITCODE -ne 0) { throw 'TypeScript typecheck failed.' }
& npm --prefix $client run build
if ($LASTEXITCODE -ne 0) { throw 'Frontend production bundle build failed.' }
& node --test (Join-Path $root 'tests/security/local-access.test.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Local security integration tests failed.' }
& node --test (Join-Path $root 'tests/workspaces/workspaces.test.mjs')
if ($LASTEXITCODE -ne 0) { throw 'SQLite workspace integration tests failed.' }
& node --test (Join-Path $root 'tests/repositories/registration.test.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Local repository registration integration tests failed.' }
& node --test (Join-Path $root 'tests/snapshots/capture.test.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Read-only snapshot integration tests failed.' }
& node --test (Join-Path $root 'tests/investigations/execution-control.test.mjs')
if ($LASTEXITCODE -ne 0) { throw 'C6b execution control HTTP integration tests failed.' }
& node --test (Join-Path $root 'tests/api/white-label.test.mjs')
if ($LASTEXITCODE -ne 0) { throw 'W1 white-label API and SignalR integration tests failed.' }
Write-Host 'GREEN: Local build, model-free Ollama, Gate B, C1, C5a and C6b (ownership/fencing/worker) contracts, frontend, security, workspaces, registrations and immutable snapshot checks passed. Real Ollama probe is separate. Cross-platform packaging remains pending.'
