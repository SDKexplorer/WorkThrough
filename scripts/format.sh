#!/usr/bin/env bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_dir"
verify=""
if [[ "${1:-}" == "--check" ]]; then verify="--verify-no-changes"; fi
# Folder formatting avoids duplicate edits from multi-target project workspaces.
dotnet run --project tools/WorkThrough.CodeStyle -c Release --no-restore -p:UseSharedCompilation=false -- --braces "${1:-}" "$repo_dir"
files=()
file_list="$(mktemp)"
trap 'rm -f "$file_list"' EXIT
if command -v rg >/dev/null 2>&1; then
  rg --files -0 src samples tests tools -g '*.cs' > "$file_list"
else
  git ls-files -z --cached --others --exclude-standard -- 'src/**/*.cs' 'samples/**/*.cs' 'tests/**/*.cs' 'tools/**/*.cs' > "$file_list"
fi
while IFS= read -r -d '' file; do files+=("$file"); done < "$file_list"
if [[ "${#files[@]}" -eq 0 ]]; then echo 'No C# files found for formatting.' >&2; exit 1; fi
dotnet format whitespace . --folder --include "${files[@]}" $verify
dotnet run --project tools/WorkThrough.CodeStyle -c Release --no-restore -p:UseSharedCompilation=false -- "${1:-}" "$repo_dir"
