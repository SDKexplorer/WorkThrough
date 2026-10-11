#!/usr/bin/env bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_dir"
mkdir -p artifacts/packages
runtime8="${WORKTHROUGH_DOTNET8:-dotnet}"
(cd dashboard-ui && npm ci && npm run format:check && npm run build)
dotnet restore WorkThrough.slnx --disable-parallel -m:1 -nr:false
dotnet build WorkThrough.slnx -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
./scripts/format.sh --check
dotnet test tests/WorkThrough.Tests -c Release -f net10.0 --no-build --no-restore -m:1 -nr:false
sdk8_dir="$(mktemp -d "$repo_dir/artifacts/consumer-sdk8-XXXXXXXX")"
cp tests/PackageConsumer/PackageConsumer.csproj tests/PackageConsumer/*.cs "$sdk8_dir/"
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
sample_dir="$(mktemp -d "$repo_dir/artifacts/shop-sample-XXXXXXXX")"
Shop__Sqlite="Data Source=$sample_dir/shop.db" "$runtime8" samples/Shop.Api/bin/Release/net8.0/Shop.Api.dll --smoke
Shop__Sqlite="Data Source=$sample_dir/shop.db" "$runtime8" samples/Shop.Worker/bin/Release/net8.0/Shop.Worker.dll --preflight > artifacts/preflight-smoke.json
echo 'PASS: deployment preflight confirmed the shop worker covers stored work and its seeded schedule.'
(cd "$sample_dir" && "$runtime8" "$repo_dir/samples/QuickStart/bin/Release/net8.0/QuickStart.dll" > quickstart.log)
echo 'PASS: the one-file quick start registered its job by reflection and ran it.'

# Independent process termination and a bounded performance/correctness smoke campaign.
WORKTHROUGH_DOTNET8="$runtime8" ./scripts/performance.sh --smoke

# Official MCP clients must remain bounded and redacted while real workers make progress.
WORKTHROUGH_DOTNET8="$runtime8" ./scripts/investigation.sh --smoke
