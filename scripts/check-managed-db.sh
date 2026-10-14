#!/usr/bin/env bash
# Qualifies one provider against a managed database server (Azure SQL Managed Instance, Amazon RDS, Azure Database for PostgreSQL
# or MySQL, Oracle Autonomous/RDS): encrypted connections, the full provider suite, the reliability exercise and a soak.
#
#   WT_MANAGED_DB_DISPOSABLE=1 DURABLEJOBS_SQLSERVER='Server=...;Encrypt=True;...' scripts/check-managed-db.sh sqlserver [soak-minutes]
#
# The suites create and drop their own databases, schemas and logins, so the connection needs those rights on a server that holds
# nothing else. Never point this at production data. Azure SQL Database (a single database) is not supported by the test harness,
# which needs CREATE DATABASE and USE; run SQL Server checks on Managed Instance or RDS.
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_dir"
provider="${1:-}"
minutes="${2:-30}"
runtime="${WORKTHROUGH_DOTNET8:-dotnet}"
case "$provider" in
  sqlserver) variable=DURABLEJOBS_SQLSERVER ;;
  postgres) variable=DURABLEJOBS_POSTGRES ;;
  mysql) variable=DURABLEJOBS_MYSQL ;;
  mariadb) variable=DURABLEJOBS_MARIADB ;;
  oracle) variable=DURABLEJOBS_ORACLE ;;
  *) echo 'Usage: check-managed-db.sh sqlserver|postgres|mysql|mariadb|oracle [soak-minutes]' >&2; exit 2 ;;
esac
connection="${!variable:-}"
if [[ -z "$connection" ]]; then echo "Supply $variable for a disposable managed server." >&2; exit 2; fi
if [[ "${WT_MANAGED_DB_DISPOSABLE:-}" != 1 ]]; then
  echo "Set WT_MANAGED_DB_DISPOSABLE=1 to confirm the server is disposable: the suites create and drop databases and logins." >&2; exit 2
fi
if [[ "$provider" == sqlserver && "$connection" == *".database.windows.net"* ]]; then
  echo 'Azure SQL Database is not supported by the test harness (no CREATE DATABASE or USE); use Managed Instance or RDS.' >&2; exit 2
fi

# Only the chosen provider runs; the others' variables are cleared so a run cannot touch another server.
for other in DURABLEJOBS_SQLSERVER DURABLEJOBS_POSTGRES DURABLEJOBS_MYSQL DURABLEJOBS_MARIADB DURABLEJOBS_ORACLE; do
  if [[ "$other" != "$variable" ]]; then unset "$other"; fi
done
export WT_REQUIRE_TLS=1
export WT_GIT_REVISION="$(git rev-parse HEAD)"
export WT_GIT_STATE="$(if [[ -z "$(git status --porcelain)" ]]; then echo clean; else echo modified; fi)"
results="artifacts/managed/$provider-$(date -u +%Y-%m-%dT%H%M%SZ)"
mkdir -p "$results"

echo "== $provider: encrypted connections and provider suite"
dotnet test tests/WorkThrough.Tests -f net10.0 --logger "trx;LogFileName=suite.trx" --results-directory "$results"

echo "== $provider: reliability exercise and $minutes minute soak"
dotnet build tests/WorkThrough.Exercises -c Release -m:1 -nr:false -p:UseSharedCompilation=false
exercises=tests/WorkThrough.Exercises/bin/Release/net8.0/WorkThrough.Exercises.dll
"$runtime" "$exercises" reliability "$provider"
"$runtime" "$exercises" soak "$provider" "$minutes" "$results/soak.json"
echo "PASS: $provider qualified against the managed server; evidence in $results"
