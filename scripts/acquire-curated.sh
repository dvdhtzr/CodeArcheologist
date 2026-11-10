#!/usr/bin/env bash
# C6c curated repositories: owner-run acquisition OUTSIDE the application.
# Fetches each manifest repository at its exact pinned commit into
# <target>/<id>, with line-ending conversion disabled, and verifies the
# pinned licence hash. It never builds, restores, tests or executes anything
# from these repositories. The lab later captures them read-only and fails
# closed if a snapshot fingerprint differs from the manifest.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MANIFEST="$ROOT/eval/curated/repositories.json"
if [[ $# -ne 1 ]]; then echo "Usage: scripts/acquire-curated.sh <target-directory-outside-the-repository>" >&2; exit 2; fi
mkdir -p "$1"
TARGET="$(cd "$1" && pwd)"
case "$TARGET/" in "$ROOT/"*) echo "Target must be outside the Code Archeologist repository." >&2; exit 2;; esac

sha256() { node -e 'const c=require("node:crypto"),f=require("node:fs");process.stdout.write(c.createHash("sha256").update(f.readFileSync(process.argv[1])).digest("hex"))' "$1"; }

node -e '
const m = JSON.parse(require("node:fs").readFileSync(process.argv[1], "utf8"));
if (m.schemaVersion !== "c6c-curated-v1") { console.error("unsupported manifest"); process.exit(2); }
for (const r of m.repositories) console.log([r.id, r.url, r.commitSha, r.licensePath, r.licenseSha256, r.subtree || "."].join("\t"));
' "$MANIFEST" | while IFS=$'\t' read -r id url sha licence licenceSha subtree; do
  dir="$TARGET/$id"
  if [[ -d "$dir/.git" ]]; then
    head="$(git -C "$dir" rev-parse HEAD)"
    [[ "$head" == "$sha" ]] || { echo "$id: existing checkout is at $head, expected $sha" >&2; exit 1; }
    [[ -z "$(git -C "$dir" status --porcelain)" ]] || { echo "$id: existing checkout has local changes" >&2; exit 1; }
  else
    mkdir -p "$dir"
    git -C "$dir" init -q
    git -C "$dir" config core.autocrlf false
    git -C "$dir" config core.eol lf
    git -C "$dir" remote add origin "$url"
    GIT_LFS_SKIP_SMUDGE=1 git -C "$dir" fetch -q --depth 1 origin "$sha"
    GIT_LFS_SKIP_SMUDGE=1 git -C "$dir" checkout -q --detach FETCH_HEAD
  fi
  [[ "$(git -C "$dir" rev-parse HEAD)" == "$sha" ]] || { echo "$id: commit mismatch" >&2; exit 1; }
  [[ "$(sha256 "$dir/$licence")" == "$licenceSha" ]] || { echo "$id: licence file differs from the pinned SHA-256" >&2; exit 1; }
  # Portability: refuse a pinned subtree whose paths differ only by case; a
  # case-insensitive filesystem (Windows, default macOS) would merge them.
  if ! git -C "$dir" ls-files -z -- "$subtree" | node -e '
    const seen = new Map(); let bad = null;
    for (const p of require("node:fs").readFileSync(0, "utf8").split("\0").filter(Boolean)) {
      const parts = p.split("/");
      for (let i = 1; i <= parts.length; i++) {
        const prefix = parts.slice(0, i).join("/"), key = prefix.toLowerCase();
        if (seen.has(key) && seen.get(key) !== prefix) bad = seen.get(key) + " vs " + prefix;
        seen.set(key, prefix);
      }
    }
    if (bad) { console.error(bad); process.exit(1); }'; then
    echo "$id: case-insensitive path collision in subtree '$subtree'; this checkout is not portable" >&2; exit 1
  fi
  echo "$id: $sha verified"
done
echo "Acquired curated repositories in $TARGET. Nothing was built or executed."
echo "Run the lab with: --curated-root \"$TARGET\""
