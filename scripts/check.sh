#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet build "$ROOT/src/CodeArcheologist.Api/CodeArcheologist.Api.csproj" --configuration Release
npm ci --prefix "$ROOT/src/CodeArcheologist.Client" --no-audit --no-fund
npm --prefix "$ROOT/src/CodeArcheologist.Client" run typecheck
npm --prefix "$ROOT/src/CodeArcheologist.Client" run build
node --test "$ROOT/tests/security/local-access.test.mjs"
echo 'Local trusted-source build, typecheck and security checks passed. Cross-platform packaging remains pending.'
