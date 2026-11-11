#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOCKFILE="$ROOT/src/CodeArcheologist.Client/package-lock.json"
if [[ ! -f "$LOCKFILE" ]]; then
  echo "Missing frontend lockfile: $LOCKFILE. Check git status and restore the tracked package-lock.json." >&2
  exit 1
fi
# The integration tests start a local API, so avoid mistaking another listener
# on this fixed port for the newly launched instance.
if node -e 'const net=require("node:net");const c=net.connect({host:"127.0.0.1",port:5127});c.once("connect",()=>{console.error("Port 5127 is already occupied. Stop the developer server before verification.");c.destroy();process.exit(2)});c.once("error",()=>process.exit(0));setTimeout(()=>{c.destroy();process.exit(0)},500)' ; then
  :
else
  exit 1
fi
dotnet build "$ROOT/src/CodeArcheologist.Api/CodeArcheologist.Api.csproj" --configuration Release
dotnet run --project "$ROOT/tests/maf-adapter-contract/MafAdapterContractProbe.csproj" --configuration Release -- --gate-c5-self-test
dotnet run --project "$ROOT/tests/maf-adapter-contract/MafAdapterContractProbe.csproj" --configuration Release -- --gate-c6b-self-test
dotnet run --project "$ROOT/tests/evaluation-lab/EvaluationLab.csproj" --configuration Release -- --validate-corpus
dotnet run --project "$ROOT/tests/evaluation-lab/EvaluationLab.csproj" --configuration Release --no-build -- --self-test
npm ci --prefix "$ROOT/src/CodeArcheologist.Client" --no-audit --no-fund
npm --prefix "$ROOT/src/CodeArcheologist.Client" run typecheck
npm --prefix "$ROOT/src/CodeArcheologist.Client" run build
npm ci --prefix "$ROOT/src/CodeArcheologist.MissionControl" --no-audit --no-fund
npm --prefix "$ROOT/src/CodeArcheologist.MissionControl" run typecheck
npm --prefix "$ROOT/src/CodeArcheologist.MissionControl" run build
npm --prefix "$ROOT/src/CodeArcheologist.MissionControl" run test
node --test "$ROOT/tests/security/local-access.test.mjs"
node --test "$ROOT/tests/workspaces/workspaces.test.mjs"
node --test "$ROOT/tests/repositories/registration.test.mjs"
node --test "$ROOT/tests/snapshots/capture.test.mjs"
node --test "$ROOT/tests/investigations/execution-control.test.mjs"
node --test "$ROOT/tests/api/white-label.test.mjs"
node --test "$ROOT/tests/api/capabilities.test.mjs"
node --test "$ROOT/tests/api/source.test.mjs"
npm --prefix "$ROOT/src/CodeArcheologist.MissionControl" run test:live
# Real Chromium UI acceptance against a disposable API and temporary source tree.
npm --prefix "$ROOT/src/CodeArcheologist.MissionControl" run browser:install
npm --prefix "$ROOT/src/CodeArcheologist.MissionControl" run test:browser
echo 'GREEN: Chromium browser acceptance, local build, frontend, security, workspaces, registrations and immutable snapshot checks passed. Cross-platform packaging remains pending.'
