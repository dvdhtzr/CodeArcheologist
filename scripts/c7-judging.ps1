# C7 judging helper (ADR 0022). Run from the repository root:
#   powershell -ExecutionPolicy Bypass -File .\scripts\c7-judging.ps1
# First run: rebuilds the lab and writes the ChatGPT packets.
# Later runs (after the answers file is filled in): imports the answers and prints the verdict.
$repo    = Split-Path -Parent $PSScriptRoot
$runs    = "$env:TEMP\ca-lab-scored-1"
$judging = "$env:USERPROFILE\CodeArcheologistJudging"
$answers = "$env:USERPROFILE\CodeArcheologistChatGPTAnswers.txt"
$lab     = Join-Path $repo "tests\evaluation-lab\bin\Release\net10.0\EvaluationLab.dll"
Set-Location $repo

cmd /c "dotnet build tests\evaluation-lab\EvaluationLab.csproj -c Release -v q 2>&1" | Select-Object -Last 2
"Code version: $(git log --oneline -1)"
if (-not (Test-Path "$runs\summary.json")) { Write-Host "Scored run not found at $runs." -ForegroundColor Red; return }

if (-not (Test-Path "$judging\DO-NOT-UPLOAD-answer-key.json")) {
  Write-Host "`nCreating the ChatGPT packets." -ForegroundColor Cyan
  cmd /c "dotnet `"$lab`" --export-judging --runs `"$runs`" --out `"$judging`" --calibration-checklist-runs `"$env:TEMP\ca-lab-real-repos`" --calibration-checklist `"$env:USERPROFILE\CodeArcheologistIssueChecklist.json`" --calibration-judge-runs `"$env:TEMP\ca-lab-multiview-test`" --calibration-judgments `"$env:USERPROFILE\CodeArcheologistJudgments.json`" 2>&1"
  if (-not (Test-Path $answers)) { New-Item $answers -ItemType File | Out-Null }
  explorer "$judging\upload-these-to-chatgpt"
  notepad $answers
  Write-Host "`nNext: give each packet to ChatGPT, paste its replies into Notepad, save, then run this script again." -ForegroundColor Green
  return
}

$lines = @(Get-Content $answers -ErrorAction SilentlyContinue | Where-Object { $_ -match 'Q\d{3}' }).Count
if ($lines -eq 0) {
  Write-Host "The packets exist but the answers file has no answers yet. Give the packets to ChatGPT first." -ForegroundColor Yellow
  explorer "$judging\upload-these-to-chatgpt"; notepad $answers; return
}

Write-Host "`nFound $lines answer lines - importing and producing the verdict." -ForegroundColor Cyan
$out = @("Code version: $(git log --oneline -1)", "Answer lines: $lines")
$out += cmd /c "dotnet `"$lab`" --import-judging --export `"$judging`" --answers `"$answers`" 2>&1"
$out += cmd /c "dotnet `"$lab`" --verdict --runs `"$runs`" --export `"$judging`" 2>&1"
$out | Set-Clipboard
$out
Write-Host "`nThe results are on your clipboard - paste them to Claude." -ForegroundColor Green
