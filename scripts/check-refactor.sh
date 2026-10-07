#!/usr/bin/env bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$repo_dir/artifacts"
proof_dir="$(mktemp -d "$repo_dir/artifacts/refactor-XXXXXXXX")"
runtime="${WORKTHROUGH_DOTNET8:-dotnet}"
dotnet publish "$repo_dir/samples/Refactor.Before" -c Release --no-restore -o "$proof_dir/before" -m:1 -p:UseSharedCompilation=false --nologo
dotnet publish "$repo_dir/samples/Refactor.After" -c Release --no-restore -o "$proof_dir/after" -m:1 -p:UseSharedCompilation=false --nologo
"$runtime" "$proof_dir/before/Refactor.Before.dll" "Data Source=$proof_dir/jobs.db" "$proof_dir/receipt.txt"
mv "$proof_dir/before/Refactor.Before.dll" "$proof_dir/before/Refactor.Before.dll.removed"
test ! -f "$proof_dir/after/Refactor.Before.dll"
dotnet --roll-forward LatestMajor "$proof_dir/after/Refactor.After.dll" "Data Source=$proof_dir/jobs.db" "$proof_dir/receipt.txt"
