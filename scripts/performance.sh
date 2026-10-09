#!/usr/bin/env bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_dir"
runtime="${WORKTHROUGH_DOTNET8:-dotnet}"
profile="${1:---smoke}"
if [[ "$profile" != "--full" && "$profile" != "--smoke" ]]; then echo 'Use --smoke or --full' >&2; exit 2; fi
export WT_GIT_REVISION="$(git rev-parse HEAD)"
export WT_GIT_STATE="$(if [[ -z "$(git status --porcelain)" ]]; then echo clean; else echo modified; fi)"
dotnet build tests/WorkThrough.Exercises -c Release -m:1 -nr:false -p:UseSharedCompilation=false
mkdir -p artifacts/performance
for provider in sqlite postgres sqlserver; do
  if [[ "$provider" == postgres && -z "${DURABLEJOBS_POSTGRES:-}" ]]; then echo 'PostgreSQL not run: supply a disposable DURABLEJOBS_POSTGRES connection.'; continue; fi
  if [[ "$provider" == sqlserver && -z "${DURABLEJOBS_SQLSERVER:-}" ]]; then echo 'SQL Server not run: supply a disposable DURABLEJOBS_SQLSERVER connection.'; continue; fi
  "$runtime" tests/WorkThrough.Exercises/bin/Release/net8.0/WorkThrough.Exercises.dll reliability "$provider"
  "$runtime" tests/WorkThrough.Exercises/bin/Release/net8.0/WorkThrough.Exercises.dll performance "$provider" "artifacts/performance/$provider-${profile#--}.json" "$profile"
done
