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
npm ci --prefix "$ROOT/src/CodeArcheologist.Client" --no-audit --no-fund
npm --prefix "$ROOT/src/CodeArcheologist.Client" run typecheck
npm --prefix "$ROOT/src/CodeArcheologist.Client" run build
node --test "$ROOT/tests/security/local-access.test.mjs"
node --test "$ROOT/tests/workspaces/workspaces.test.mjs"
echo 'GREEN: Local build, frontend and security/workspace persistence tests passed. Cross-platform packaging remains pending.'
