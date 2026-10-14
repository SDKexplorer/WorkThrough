#!/usr/bin/env bash
# Installs the packed templates into a private hive, creates every template/storage combination against the local packages,
# builds each, runs the tests template, and runs the PostgreSQL worker and web projects until their first job succeeds. Needs a
# disposable DURABLEJOBS_POSTGRES database; each run uses fresh schemas in it.
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
runtime="${WORKTHROUGH_DOTNET8:-dotnet}"
work_dir="$(mktemp -d "$repo_dir/artifacts/templates-XXXXXXXX")"
hive="$work_dir/hive"
: "${DURABLEJOBS_POSTGRES:?Set DURABLEJOBS_POSTGRES to a disposable PostgreSQL database.}"
export WORKTHROUGH_TEST_POSTGRES="${WORKTHROUGH_TEST_POSTGRES:-$DURABLEJOBS_POSTGRES}"
run_id="$(date +%s)$$"
cp "$repo_dir/tests/PackageConsumer/NuGet.Config" "$work_dir/NuGet.Config"
sed -i.bak "s|../../artifacts/packages|$repo_dir/artifacts/packages|" "$work_dir/NuGet.Config"
dotnet new install "$repo_dir"/artifacts/packages/WorkThrough.Templates.*.nupkg --debug:custom-hive "$hive" > /dev/null

for template in worker web; do
  for storage in postgresql sqlserver oracle mysql; do
    project="$work_dir/$template-$storage"
    dotnet new "workthrough-$template" -n Demo -o "$project" --storage "$storage" --debug:custom-hive "$hive" > /dev/null
    dotnet build "$project" -c Release --configfile "$work_dir/NuGet.Config" -p:RestorePackagesPath="$work_dir/packages" -p:UseSharedCompilation=false -nr:false --nologo -v:q > "$project/build.log" \
      || { cat "$project/build.log"; exit 1; }
  done
done
echo 'PASS: both templates build with every storage provider.'

tests="$work_dir/tests"
dotnet new workthrough-tests -n Demo.Tests -o "$tests" --debug:custom-hive "$hive" > /dev/null
{ dotnet restore "$tests" --configfile "$work_dir/NuGet.Config" -p:RestorePackagesPath="$work_dir/packages" -nr:false -v:q \
  && dotnet test "$tests" -c Release --no-restore -p:RestorePackagesPath="$work_dir/packages" -p:UseSharedCompilation=false -nr:false --nologo -v:q; } > "$tests/test.log" \
  || { cat "$tests/test.log"; exit 1; }
echo 'PASS: the tests template runs its flow tests with stubbed jobs.'

wait_for_log() {
  for _ in $(seq 1 120); do
    if grep -q "$2" "$1"; then return 0; fi
    sleep 0.5
  done
  cat "$1"
  return 1
}

worker="$work_dir/worker-postgresql"
cd "$worker"
ConnectionStrings__WorkThrough="$DURABLEJOBS_POSTGRES;Search Path=wt_template_worker_$run_id" DOTNET_ENVIRONMENT=Development "$runtime" bin/Release/net8.0/Demo.dll > run.log 2>&1 &
worker_pid=$!
wait_for_log run.log "Application started"
kill "$worker_pid"
echo 'PASS: the worker template starts, resolves its jobs and seeds its schedule.'

web="$work_dir/web-postgresql"
port="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1])')"
cd "$web"
ConnectionStrings__WorkThrough="$DURABLEJOBS_POSTGRES;Search Path=wt_template_web_$run_id" ASPNETCORE_ENVIRONMENT=Development "$runtime" bin/Release/net8.0/Demo.dll --urls "http://127.0.0.1:$port" > run.log 2>&1 &
web_pid=$!
trap 'kill "$web_pid" 2>/dev/null || true' EXIT
wait_for_log run.log "Application started"
curl -fsS -X POST "http://127.0.0.1:$port/signups" -H 'Content-Type: application/json' -d '{"email":"ada@example.com"}' > /dev/null
wait_for_log run.log "Sending a welcome email to ada@example.com"
curl -fsS "http://127.0.0.1:$port/health" > /dev/null
curl -fsS -o /dev/null "http://127.0.0.1:$port/jobs/"
echo 'PASS: the web template accepts a request, runs its job, and serves health and the console.'
