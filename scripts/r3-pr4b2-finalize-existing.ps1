# Resume PR4b2 Phase A final record only, AFTER the full preflight printed:
# VERIFIED: provisional paired smoke cannot pass the scored census.
# This does not rerun full regression or real model tests and does not authorize Phase B.
param(
  [Parameter(Mandatory = $true)][string]$Out,
  [Parameter(Mandatory = $true)][string]$CuratedRoot,
  [Parameter(Mandatory = $true)][switch]$ConfirmPriorGatesPassed
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$outFull = (Resolve-Path -LiteralPath $Out -ErrorAction Stop).Path
$curated = (Resolve-Path -LiteralPath $CuratedRoot -ErrorAction Stop).Path
$recordFile = Join-Path $outFull 'preflight-record.json'
if (Test-Path -LiteralPath $recordFile) {
  throw "Record already exists; refusing to overwrite: $recordFile"
}
function Require([bool]$ok, [string]$message) {
  if (-not $ok) { throw $message }
}
function Read-Json([string]$path) {
  Require (Test-Path -LiteralPath $path -PathType Leaf) "Missing preflight output: $path"
  return (Get-Content -Raw -LiteralPath $path | ConvertFrom-Json)
}
function GitValue([string]$dir, [string[]]$arguments) {
  $answer = & git -C $dir @arguments
  if ($LASTEXITCODE -ne 0) { throw "Cannot verify Git data in $dir" }
  return ($answer -join "`n").Trim()
}
Push-Location $root
try {
  Require (-not [string]::IsNullOrWhiteSpace((GitValue $root @('rev-parse', 'HEAD')))) 'Unknown current Git HEAD.'
  Require (-not (GitValue $root @('status', '--porcelain'))) 'Trusted Code Archeologist checkout has local changes.'
  $currentCommit = GitValue $root @('rev-parse', 'HEAD')
  $smoke = Join-Path $outFull 'provisional-smoke'
  $negative = Join-Path $outFull 'negative-expectations'
  $metadata = Read-Json (Join-Path $smoke 'pr4-run-metadata.json')
  Require ($null -ne $metadata.executionIdentity) 'Existing smoke has no recorded execution identity.'
  Require ($metadata.provisional -eq $true) 'Existing smoke was not provisional.'
  $identity = $metadata.executionIdentity
  Require ($identity.modelTag -ceq 'qwen3.5:9b') 'Unexpected model tag in prior run.'
  Require ("$($identity.modelDigest)" -cmatch '^[0-9a-f]{64}$') 'Missing prior model digest.'
  Require ("$($identity.gitCommit)" -cmatch '^[0-9a-f]{40}$') 'Missing prior runner Git commit.'
  Require (-not [string]::IsNullOrWhiteSpace([string]$identity.ollamaVersion)) 'Missing prior Ollama version.'
  Require (-not [string]::IsNullOrWhiteSpace([string]$identity.operatingSystem)) 'Missing .NET runner OS description.'
  Require (-not [string]::IsNullOrWhiteSpace([string]$identity.processArchitecture)) 'Missing .NET runner architecture.'
  Require (-not [string]::IsNullOrWhiteSpace([string]$identity.dotnetRuntime)) 'Missing .NET runtime description.'
  $protocolSha = (Get-FileHash -Algorithm SHA256 -LiteralPath (
    Join-Path $root 'eval/r3-pr4-experiment.json')).Hash.ToLowerInvariant()
  Require ($metadata.protocolSha256 -ceq $protocolSha) 'Frozen protocol hash differs from the prior smoke.'
  $priorCommit = [string]$identity.gitCommit
  $commitType = GitValue $root @('cat-file', '-t', $priorCommit)
  Require ($commitType -eq 'commit') 'Git no longer has the original preflight commit.'
  $changed = @( & git -C $root diff --name-only $priorCommit HEAD )
  Require ($LASTEXITCODE -eq 0) 'Cannot compare current and original preflight source.'
  $allowed = @(
    'scripts/r3-pr4b2-preflight.ps1',
    'scripts/r3-pr4b2-finalize-existing.ps1',
    'docs/architecture/r3-pr4b2-execution-preflight.md'
  )
  $unrelated = @($changed | Where-Object { $_ -notin $allowed })
  Require ($unrelated.Count -eq 0) (
    'Source has changed since the original smoke, beyond preflight scripts: ' + ($unrelated -join ', '))
  $negativeSummary = Read-Json (Join-Path $negative 'summary.json')
  Require ($negativeSummary.provisional -eq $true) 'Four-case run was not provisional.'
  Require (@($negativeSummary.expectations).Count -eq 4) 'Expected four negative-case results.'
  Require (@($negativeSummary.expectations | Where-Object { -not $_.confirmed }).Count -eq 0) 'Negative-case expectation failed.'
  Require (@($negativeSummary.failures).Count -eq 0) 'Negative-case summary contains failures.'
  $results = @()
  foreach ($arm in @('gate-c', 'gate-c-adaptive')) {
    $stem = "F01-attribute-private-helper.$arm.t1"
    $report = Read-Json (Join-Path $smoke "$stem.report.json")
    $score = Read-Json (Join-Path $smoke "$stem.score.json")
    $activity = Read-Json (Join-Path $smoke "$stem.pr4-activity.json")
    $minimumCalls = if ($arm -eq 'gate-c') { 5 } else { 7 }
    Require ($report.comparator -ceq $arm -and $report.outcome -eq 'completed') "$arm report did not complete."
    Require ($score.comparator -ceq $arm -and @($score.integrityBreaches).Count -eq 0) "$arm score has breaches."
    Require ($report.gateC.checkpointCount -eq 3 -and $report.gateC.reconciliationAccepted) "$arm checkpoints/reconciliation invalid."
    Require (@($report.gateC.roleStatuses | Where-Object { $_ -ne 'CompletedValidated' }).Count -eq 0) "$arm role incomplete."
    Require ($activity.modelCalls -ge $minimumCalls -and $activity.modelCallFailures -eq 0) "$arm live MAF call accounting failed."
    Require ($activity.checkpointReplayAccepted -and $activity.modelCallAccountingBasis -eq 'opt-in-MAF-request-attempts') "$arm activity invalid."
    if ($arm -eq 'gate-c-adaptive') { Require ($activity.receiptReplayAccepted) 'Adaptive receipt replay failed.' }
    $results += [pscustomobject]@{ comparator = $arm; modelCalls = $activity.modelCalls; completed = $true }
  }
  $manifest = Read-Json (Join-Path $root 'eval/curated/repositories.json')
  Require (@($manifest.repositories).Count -eq 15) 'Curated manifest no longer contains 15 repositories.'
  $curatedResults = @()
  foreach ($item in $manifest.repositories) {
    $dir = Join-Path $curated $item.id
    Require (Test-Path -LiteralPath (Join-Path $dir '.git')) "Missing curated repository $($item.id)."
    Require ((GitValue $dir @('rev-parse', 'HEAD')) -ceq $item.commitSha) "$($item.id) commit changed."
    Require (-not (GitValue $dir @('status', '--porcelain'))) "$($item.id) working tree changed."
    $licence = Join-Path $dir $item.licensePath
    Require (Test-Path -LiteralPath $licence -PathType Leaf) "$($item.id) missing licence."
    Require ((Get-FileHash -Algorithm SHA256 -LiteralPath $licence).Hash.ToLowerInvariant() -ceq
      $item.licenseSha256) "$($item.id) licence changed."
    $subtree = if ($item.subtree) { Join-Path $dir $item.subtree } else { $dir }
    Require (Test-Path -LiteralPath $subtree -PathType Container) "$($item.id) subtree missing."
    $curatedResults += [pscustomobject]@{
      id = $item.id; commit = $item.commitSha; license = $item.license
      subtree = $item.subtree; snapshotFingerprint = $item.snapshotFingerprint
      verifiedPreviously = $true
    }
  }
  $ollamaVersion = Invoke-RestMethod -Method Get -Uri 'http://127.0.0.1:11434/api/version' -TimeoutSec 12
  $tags = Invoke-RestMethod -Method Get -Uri 'http://127.0.0.1:11434/api/tags' -TimeoutSec 12
  $model = @($tags.models | Where-Object { $_.name -ceq 'qwen3.5:9b' })
  Require ($model.Count -eq 1) 'The exact model tag is not uniquely installed.'
  Require ("$($model[0].digest)".ToLowerInvariant() -ceq [string]$identity.modelDigest) 'Ollama digest changed since smoke.'
  Require ([string]$ollamaVersion.version -ceq [string]$identity.ollamaVersion) 'Ollama version changed since smoke.'
  $record = [ordered]@{
    schemaVersion = 'r3-pr4b2-preflight-v1'
    readiness = 'recovered-final-record-after-prior-gates; scored-study-not-authorized'
    recordedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    originalExecutingGitCommit = $priorCommit
    finalizerGitCommit = $currentCommit
    protocolSha256 = $protocolSha
    modelTag = $identity.modelTag
    modelDigest = $identity.modelDigest
    ollamaVersion = $identity.ollamaVersion
    dotnetRuntime = $identity.dotnetRuntime
    operatingSystem = $identity.operatingSystem
    processArchitecture = $identity.processArchitecture
    fullLocalCheck = 'owner-confirmed-passed-in-original-invocation; not-rerun'
    adaptiveMafProbe = 'owner-confirmed-passed-in-original-invocation; not-rerun'
    pairedProvisionalSmoke = 'revalidated-from-existing-artifacts'
    curatedFingerprints = 'owner-confirmed-passed-in-original-invocation; not-rerun'
    modelCalls = $results
    curated = $curatedResults
  }
  $record | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $recordFile -Encoding UTF8
  Write-Host "GREEN: existing Phase A evidence revalidated and finalization record written: $recordFile"
  Write-Host 'Full regression and live model calls were NOT rerun; prior success was owner-confirmed.'
  Write-Host 'No scored 232-trial experiment was started or authorized.'
}
finally { Pop-Location }
