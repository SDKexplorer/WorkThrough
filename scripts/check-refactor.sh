#!/usr/bin/env bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$repo_dir/artifacts"
proof_dir="$(mktemp -d "$repo_dir/artifacts/refactor-XXXXXXXX")"
runtime="${WORKTHROUGH_DOTNET8:-dotnet}"
dotnet publish "$repo_dir/tests/Relocation/Refactor.Before" -c Release --no-restore -o "$proof_dir/before" -m:1 -p:UseSharedCompilation=false --nologo
dotnet publish "$repo_dir/tests/Relocation/Refactor.After" -c Release --no-restore -o "$proof_dir/after" -m:1 -p:UseSharedCompilation=false --nologo
test ! -f "$proof_dir/after/Refactor.Before.dll"
if [[ -z "${DURABLEJOBS_POSTGRES:-}" ]]; then echo 'Supply a disposable DURABLEJOBS_POSTGRES database for the relocation proof.' >&2; exit 2; fi
for provider in postgres sqlserver oracle mysql mariadb; do
  if [[ "$provider" == mysql && -z "${DURABLEJOBS_MYSQL:-}" ]]; then continue; fi
  if [[ "$provider" == mariadb && -z "${DURABLEJOBS_MARIADB:-}" ]]; then continue; fi
  if [[ "$provider" == oracle && -z "${DURABLEJOBS_ORACLE:-}" ]]; then echo "Oracle not run: supply a disposable DURABLEJOBS_ORACLE connection."; continue; fi
  if [[ "$provider" == sqlserver && -z "${DURABLEJOBS_SQLSERVER:-}" ]]; then continue; fi
  "$runtime" "$repo_dir/tests/WorkThrough.Exercises/bin/Release/net8.0/WorkThrough.Exercises.dll" relocation "$provider" "$proof_dir/before/Refactor.Before.dll" "$proof_dir/after/Refactor.After.dll"
done
mv "$proof_dir/before/Refactor.Before.dll" "$proof_dir/before/Refactor.Before.dll.removed"
