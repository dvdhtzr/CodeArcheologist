# C6c curated repositories: owner-run acquisition OUTSIDE the application.
# Fetches each manifest repository at its exact pinned commit into
# <Target>\<id>, with line-ending conversion disabled, and verifies the pinned
# licence hash. It never builds, restores, tests or executes anything from
# these repositories. The lab later captures them read-only and fails closed
# if a snapshot fingerprint differs from the manifest.
param([Parameter(Mandatory = $true)][string]$Target)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -Raw -LiteralPath (Join-Path $root 'eval/curated/repositories.json') | ConvertFrom-Json
if ($manifest.schemaVersion -ne 'c6c-curated-v1') { throw 'Unsupported curated manifest.' }
New-Item -ItemType Directory -Force -Path $Target | Out-Null
$targetFull = (Resolve-Path -LiteralPath $Target).Path.TrimEnd('\', '/')
if (($targetFull + '\').StartsWith(($root.TrimEnd('\', '/') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
  throw 'Target must be outside the Code Archeologist repository.'
}
function Invoke-Git([string]$dir, [string[]]$arguments) {
  & git -C $dir @arguments
  if ($LASTEXITCODE -ne 0) { throw "git $($arguments -join ' ') failed in $dir" }
}
$env:GIT_LFS_SKIP_SMUDGE = '1'
foreach ($r in $manifest.repositories) {
  $dir = Join-Path $targetFull $r.id
  if (Test-Path -LiteralPath (Join-Path $dir '.git')) {
    $head = (& git -C $dir rev-parse HEAD).Trim()
    if ($head -ne $r.commitSha) { throw "$($r.id): existing checkout is at $head, expected $($r.commitSha)" }
    if ((& git -C $dir status --porcelain)) { throw "$($r.id): existing checkout has local changes" }
  } else {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Invoke-Git $dir @('init', '-q')
    Invoke-Git $dir @('config', 'core.autocrlf', 'false')
    Invoke-Git $dir @('config', 'core.eol', 'lf')
    Invoke-Git $dir @('config', 'core.longpaths', 'true')
    Invoke-Git $dir @('remote', 'add', 'origin', $r.url)
    Invoke-Git $dir @('fetch', '-q', '--depth', '1', 'origin', $r.commitSha)
    Invoke-Git $dir @('checkout', '-q', '--detach', 'FETCH_HEAD')
  }
  if ((& git -C $dir rev-parse HEAD).Trim() -ne $r.commitSha) { throw "$($r.id): commit mismatch" }
  $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $dir $r.licensePath)).Hash.ToLowerInvariant()
  if ($hash -ne $r.licenseSha256) { throw "$($r.id): licence file differs from the pinned SHA-256" }
  # Portability: the git index lists every path even after a case-insensitive
  # filesystem merged colliding directories; refuse such a pinned subtree.
  $subtree = if ($r.subtree) { $r.subtree } else { '.' }
  $seen = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
  foreach ($path in (& git -C $dir ls-files -- $subtree)) {
    $parts = $path -split '/'
    for ($i = 1; $i -le $parts.Length; $i++) {
      $prefix = ($parts[0..($i - 1)] -join '/')
      if ($seen.ContainsKey($prefix) -and $seen[$prefix] -cne $prefix) {
        throw "$($r.id): case-insensitive path collision ($($seen[$prefix]) vs $prefix) in subtree '$subtree'"
      }
      $seen[$prefix] = $prefix
    }
  }
  Write-Host "$($r.id): $($r.commitSha) verified"
}
Write-Host "Acquired curated repositories in $targetFull. Nothing was built or executed."
Write-Host "Run the lab with: --curated-root `"$targetFull`""
