#!/usr/bin/env bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_dir"
verify=""
if [[ "${1:-}" == "--check" ]]; then verify="--verify-no-changes"; fi
# Folder formatting avoids duplicate edits from multi-target project workspaces.
dotnet run --project tools/WorkThrough.CodeStyle -c Release --no-restore -p:UseSharedCompilation=false -- --braces "${1:-}" "$repo_dir"
files=()
while IFS= read -r file; do files+=("$file"); done < <(rg --files src samples tests tools -g '*.cs')
dotnet format whitespace . --folder --include "${files[@]}" $verify
dotnet run --project tools/WorkThrough.CodeStyle -c Release --no-restore -p:UseSharedCompilation=false -- "${1:-}" "$repo_dir"
