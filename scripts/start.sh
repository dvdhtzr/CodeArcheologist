#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
API="$ROOT/src/CodeArcheologist.Api/CodeArcheologist.Api.csproj"
CLIENT="$ROOT/src/CodeArcheologist.Client"
command -v dotnet >/dev/null || { echo '.NET 10 SDK required for development' >&2; exit 1; }
command -v npm >/dev/null || { echo 'Node.js and npm required for development' >&2; exit 1; }
if [[ ! -d "$CLIENT/node_modules" ]]; then
  npm ci --prefix "$CLIENT" --no-audit --no-fund
fi
code="$(node -e "process.stdout.write(require('node:crypto').randomBytes(16).toString('hex'))")"
echo "Local browser pairing code: $code"
CODE_ARCHEOLOGIST_BOOTSTRAP_CODE="$code" CODE_ARCHEOLOGIST_DEV_UI=1 dotnet run --project "$API" --no-launch-profile &
api_pid=$!
cleanup() { kill "$api_pid" 2>/dev/null || true; wait "$api_pid" 2>/dev/null || true; }
trap cleanup EXIT INT TERM
echo 'Developer UI: http://127.0.0.1:5174/  (API: http://127.0.0.1:5127/)'
npm --prefix "$CLIENT" run dev -- --host 127.0.0.1
