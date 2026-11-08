#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet build "$ROOT/src/CodeArcheologist.Api/CodeArcheologist.Api.csproj" --configuration Release
npm install --prefix "$ROOT/src/CodeArcheologist.Client" --no-audit --no-fund
npm --prefix "$ROOT/src/CodeArcheologist.Client" run typecheck
npm --prefix "$ROOT/src/CodeArcheologist.Client" run build
echo 'Builds/typecheck completed. Runtime, security and packaging checks remain pending.'
