#!/usr/bin/env bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_dir"
runtime="${WORKTHROUGH_DOTNET8:-dotnet}"
profile="${1:---smoke}"
case "$profile" in
  --smoke) retained=1000; baseline=5; investigation=10; repetitions=1 ;;
  --full) retained=100000; baseline=60; investigation=120; repetitions=3 ;;
  --soak) retained=100000; baseline=120; investigation=7200; repetitions=1 ;;
  *) echo 'Use --smoke, --full or --soak [sqlite|postgres|sqlserver|oracle]' >&2; exit 2 ;;
esac
providers=(sqlite postgres sqlserver oracle)
if [[ -n "${2:-}" ]]; then
  case "$2" in
    sqlite|postgres|sqlserver|oracle) providers=("$2") ;;
    *) echo 'Unknown provider.' >&2; exit 2 ;;
  esac
fi
export WT_GIT_REVISION="$(git rev-parse HEAD)"
export WT_GIT_STATE="$(if [[ -z "$(git status --porcelain)" ]]; then echo clean; else echo modified; fi)"
dotnet build tests/WorkThrough.Exercises -c Release -m:1 -nr:false -p:UseSharedCompilation=false
mkdir -p artifacts/performance
for provider in "${providers[@]}"; do
  case "$provider" in
    sqlite) variable=DURABLEJOBS_SQLITE ;;
    postgres) variable=DURABLEJOBS_POSTGRES ;;
    sqlserver) variable=DURABLEJOBS_SQLSERVER ;;
    oracle) variable=DURABLEJOBS_ORACLE ;;
  esac
  if [[ "$provider" != sqlite && -z "${!variable:-}" ]]; then
    if [[ -n "${2:-}" ]]; then echo "Supply a disposable $variable connection." >&2; exit 2; fi
    echo "$provider not run: supply a disposable $variable connection."
    continue
  fi
  for ((run=1; run<=repetitions; run++)); do
    "$runtime" tests/WorkThrough.Exercises/bin/Release/net8.0/WorkThrough.Exercises.dll investigation \
      "$provider" "artifacts/performance/$provider-investigation-${profile#--}-$run.json" "$retained" "$baseline" "$investigation"
  done
done
