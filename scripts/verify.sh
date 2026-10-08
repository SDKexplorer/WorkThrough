#!/usr/bin/env bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_dir"
mkdir -p artifacts/packages
runtime8="${WORKTHROUGH_DOTNET8:-dotnet}"
(cd dashboard-ui && npm ci && npm run format:check && npm run build)
dotnet restore WorkThrough.slnx --disable-parallel -m:1 -nr:false
dotnet build WorkThrough.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/WorkThrough.Tests -c Release -f net10.0 --no-build --no-restore -m:1 -nr:false
sdk8_dir="$(mktemp -d "$repo_dir/artifacts/consumer-sdk8-XXXXXXXX")"
cp tests/PackageConsumer/PackageConsumer.csproj tests/PackageConsumer/Program.cs "$sdk8_dir/"
cat > "$sdk8_dir/global.json" <<'JSON'
{"sdk":{"version":"8.0.100","rollForward":"latestFeature","allowPrerelease":false}}
JSON
(cd "$sdk8_dir" && "$runtime8" --version && "$runtime8" vstest "$repo_dir/tests/WorkThrough.Tests/bin/Release/net8.0/WorkThrough.Tests.dll")
"$repo_dir/scripts/check-refactor.sh"
for project in src/*/*.csproj; do
  dotnet pack "$project" -c Release --no-build --no-restore -o "$repo_dir/artifacts/packages" -m:1 -nr:false
done
package_cache="$(mktemp -d "$repo_dir/artifacts/package-cache-XXXXXXXX")"
(cd "$sdk8_dir" && "$runtime8" restore --configfile "$repo_dir/tests/PackageConsumer/NuGet.Config" --packages "$package_cache" --disable-parallel -m:1 -nr:false && "$runtime8" run -c Release --no-restore -p:UseSharedCompilation=false)
dotnet restore tests/PackageConsumer -p:TargetFramework=net10.0 --configfile tests/PackageConsumer/NuGet.Config --packages "$package_cache" --disable-parallel -m:1 -nr:false
dotnet run --project tests/PackageConsumer -c Release -f net10.0 -p:TargetFramework=net10.0 --no-restore -p:UseSharedCompilation=false
sample_dir="$(mktemp -d "$repo_dir/artifacts/processor-sample-XXXXXXXX")"
Jobs__ConnectionString="Data Source=$sample_dir/jobs.db" "$runtime8" samples/Demo.Processor/bin/Release/net8.0/Demo.Processor.dll --once
if Jobs__ConnectionString="Data Source=$sample_dir/jobs.db" "$runtime8" samples/Demo.Worker/bin/Release/net8.0/Demo.Worker.dll --preflight > artifacts/preflight-smoke.json; then
  echo 'FAIL: the order worker incorrectly claimed coverage for the invoice sample.' >&2
  exit 1
else
  preflight_status=$?
  test "$preflight_status" -eq 2
  echo 'PASS: deployment preflight rejected a worker that does not support persisted invoice work.'
fi
