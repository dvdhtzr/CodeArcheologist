# R3 PR4b2 Phase A: owner-run, fail-closed preflight. NEVER starts a scored experiment.
# Only reads curated source repositories; nothing from them is built or executed.
param(
  [Parameter(Mandatory = $true)][string]$CuratedRoot,
  [string]$Out = (Join-Path $env:TEMP ("ca-r3-pr4b2-preflight-" + (Get-Date -Format yyyyMMddHHmmss)))
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$lab = Join-Path $root 'tests/evaluation-lab/EvaluationLab.csproj'
$adapter = Join-Path $root 'tests/maf-adapter-contract/MafAdapterContractProbe.csproj'
$curated = (Resolve-Path -LiteralPath $CuratedRoot -ErrorAction Stop).Path
$repoRoot = (Resolve-Path -LiteralPath $root).Path
if (($curated.TrimEnd('\', '/') + '\').StartsWith(($repoRoot.TrimEnd('\', '/') + '\'),
    [StringComparison]::OrdinalIgnoreCase)) {
  throw 'Curated root must be outside Code Archeologist.'
}
if ((Test-Path -LiteralPath $Out) -and @(Get-ChildItem -Force -LiteralPath $Out).Count -gt 0) {
  throw 'Preflight output directory already contains files; no overwrite allowed.'
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$outFull = (Resolve-Path -LiteralPath $Out).Path
if (($outFull.TrimEnd('\', '/') + '\').StartsWith(($repoRoot.TrimEnd('\', '/') + '\'),
    [StringComparison]::OrdinalIgnoreCase)) {
  throw 'Preflight output must be outside Code Archeologist.'
}
Push-Location $repoRoot
try {
  function Invoke-Checked([string]$Label, [string]$Exe, [string[]]$Arguments) {
    Write-Host "PRECHECK: $Label"
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit $LASTEXITCODE" }
  }
  function GitValue([string]$Dir, [string[]]$Arguments) {
    $answer = & git -C $Dir @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git verification failed in $Dir" }
    return ($answer -join "`n").Trim()
  }
  $commit = GitValue $repoRoot @('rev-parse', 'HEAD')
  if ($commit -notmatch '^[0-9a-f]{40}$') { throw 'Invalid executing Git revision.' }
  if (GitValue $repoRoot @('status', '--porcelain')) {
    throw 'Code Archeologist checkout is dirty; reproducible preflight requires a clean commit.'
  }
  Invoke-Checked 'Frozen 50-case protocol and labels' 'dotnet' @(
    'run', '--project', $lab, '--configuration', 'Release', '--', '--r3-pr4-validate'
  )
  $manifestPath = Join-Path $repoRoot 'eval/curated/repositories.json'
  $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
  if ($manifest.schemaVersion -ne 'c6c-curated-v1' -or @($manifest.repositories).Count -ne 15) {
    throw 'Expected exactly 15 pinned curated repositories.'
  }
  $curatedResults = @()
  foreach ($r in $manifest.repositories) {
    $dir = Join-Path $curated $r.id
    if (-not (Test-Path -LiteralPath (Join-Path $dir '.git'))) {
      throw "$($r.id): missing acquired Git checkout. Use scripts/acquire-curated.ps1 first."
    }
    if ((GitValue $dir @('rev-parse', 'HEAD')) -cne $r.commitSha) {
      throw "$($r.id): pinned commit differs."
    }
    if (GitValue $dir @('status', '--porcelain')) {
      throw "$($r.id): acquired checkout has changes."
    }
    $licence = Join-Path $dir $r.licensePath
    if (-not (Test-Path -LiteralPath $licence -PathType Leaf)) {
      throw "$($r.id): missing pinned licence file."
    }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $licence).Hash.ToLowerInvariant() -cne $r.licenseSha256) {
      throw "$($r.id): pinned licence digest differs."
    }
    $source = if ($r.subtree) { Join-Path $dir $r.subtree } else { $dir }
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
      throw "$($r.id): pinned subtree is missing."
    }
    $fingerprint = $null
    if ($r.group -ne 'capture-limit') {
      Write-Host "PRECHECK: $($r.id) read-only snapshot fingerprint"
      $survey = & dotnet run --project $lab --configuration Release -- --survey $source 2>&1
      if ($LASTEXITCODE -ne 0) { throw "$($r.id): snapshot survey failed: $($survey -join '; ')" }
      $line = @($survey | Where-Object { "$_" -match '^fingerprint [0-9a-f]{64} files=\d+' })
      if ($line.Count -ne 1 -or "$($line[0])" -notmatch '^fingerprint ([0-9a-f]{64}) files=(\d+)') {
        throw "$($r.id): snapshot survey did not provide a unique fingerprint."
      }
      $fingerprint = $Matches[1]
      $count = [int]$Matches[2]
      if ($fingerprint -cne $r.snapshotFingerprint -or $count -ne [int]$r.snapshotFileCount) {
        throw "$($r.id): source fingerprint/file count differs from the approved manifest."
      }
    }
    $curatedResults += [pscustomobject]@{
      id = $r.id; commit = $r.commitSha; license = $r.license
      subtree = $r.subtree; fingerprint = $fingerprint; status = 'verified'
    }
    Write-Host "VERIFIED: $($r.id)"
  }
  Invoke-Checked 'Full model-free and frontend regression' 'powershell.exe' @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repoRoot 'scripts/check.ps1')
  )
  $version = Invoke-RestMethod -Method Get -Uri 'http://127.0.0.1:11434/api/version' -TimeoutSec 12
  $tags = Invoke-RestMethod -Method Get -Uri 'http://127.0.0.1:11434/api/tags' -TimeoutSec 12
  $model = @($tags.models | Where-Object { $_.name -ceq 'qwen3.5:9b' })
  if ($model.Count -ne 1 -or "$($model[0].digest)" -notmatch '^[0-9a-fA-F]{64}$' -or
      [string]::IsNullOrWhiteSpace("$($version.version)")) {
    throw 'Ollama model tag, exact digest, or server version could not be observed.'
  }
  Invoke-Checked 'Genuine adaptive MAF/Ollama acceptance' 'dotnet' @(
    'run', '--project', $adapter, '--configuration', 'Release', '--', '--r3-adaptive-run', '1'
  )
  $smoke = Join-Path $outFull 'provisional-smoke'
  Invoke-Checked 'Genuine fixed/adaptive paired provisional smoke' 'dotnet' @(
    'run', '--project', $lab, '--configuration', 'Release', '--',
    '--r3-pr4-run', '--out', $smoke, '--cases', 'F01-attribute-private-helper',
    '--comparators', 'gate-c,gate-c-adaptive', '--provisional'
  )
  foreach ($arm in @('gate-c', 'gate-c-adaptive')) {
    $stem = "F01-attribute-private-helper.$arm.t1"
    $report = Get-Content -Raw -LiteralPath (Join-Path $smoke "$stem.report.json") | ConvertFrom-Json
    $activity = Get-Content -Raw -LiteralPath (Join-Path $smoke "$stem.pr4-activity.json") | ConvertFrom-Json
    $score = Get-Content -Raw -LiteralPath (Join-Path $smoke "$stem.score.json") | ConvertFrom-Json
    $minimumCalls = if ($arm -eq 'gate-c') { 5 } else { 7 }
    if ($report.outcome -ne 'completed' -or $score.integrityBreaches.Count -ne 0 -or
        $report.gateC.checkpointCount -ne 3 -or -not $report.gateC.reconciliationAccepted -or
        @($report.gateC.roleStatuses | Where-Object { $_ -ne 'CompletedValidated' }).Count -ne 0 -or
        $activity.modelCalls -lt $minimumCalls -or $activity.modelCallFailures -ne 0 -or
        -not $activity.checkpointReplayAccepted -or
        $activity.modelCallAccountingBasis -ne 'opt-in-MAF-request-attempts' -or
        ($arm -eq 'gate-c-adaptive' -and -not $activity.receiptReplayAccepted)) {
      throw "$arm smoke did not prove three validated roles, full replay, and meaningful MAF call accounting."
    }
    Write-Host "VERIFIED: $arm completed; provider attempts=$($activity.modelCalls); failures=0"
  }
  $runMetadata = Get-Content -Raw -LiteralPath (Join-Path $smoke 'pr4-run-metadata.json') | ConvertFrom-Json
  if ($runMetadata.executionIdentity.gitCommit -cne $commit -or
      $runMetadata.executionIdentity.modelDigest -cne "$($model[0].digest)".ToLowerInvariant() -or
      $runMetadata.executionIdentity.ollamaVersion -cne $version.version) {
    throw 'Provisional run provenance disagrees with independent preflight observations.'
  }
  $protocolSha = (Get-FileHash -Algorithm SHA256 -LiteralPath (
    Join-Path $repoRoot 'eval/r3-pr4-experiment.json')).Hash.ToLowerInvariant()
  $record = [ordered]@{
    schemaVersion = 'r3-pr4b2-preflight-v1'
    readiness = 'preflight-green-only; no scored experiment authorized'
    recordedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    executingGitCommit = $commit
    protocolSha256 = $protocolSha
    modelTag = 'qwen3.5:9b'
    modelDigest = "$($model[0].digest)".ToLowerInvariant()
    ollamaVersion = $version.version
    dotnetVersion = (& dotnet --version)
    operatingSystem = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    fullLocalCheck = 'GREEN'
    adaptiveMafProbe = 'GREEN'
    pairedProvisionalSmoke = 'GREEN'
    curated = $curatedResults
  }
  $recordFile = Join-Path $outFull 'preflight-record.json'
  $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $recordFile -Encoding UTF8
  Write-Host "GREEN: PR4b2 Phase A preflight passed. Record: $recordFile"
  Write-Host 'NO full scored evaluation was started. Owner authorization remains required.'
}
finally {
  Pop-Location
}
