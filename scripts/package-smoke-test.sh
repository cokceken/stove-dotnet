#!/usr/bin/env bash
# Restores and compiles a throwaway project against the freshly packed StoveDotnet packages, proving they are
# consumable from a feed (dependencies resolve, public API compiles) and not only through project references.
set -euo pipefail

artifacts="$(cd "${1:?usage: package-smoke-test.sh <artifacts-dir>}" && (pwd -W 2>/dev/null || pwd))" # pwd -W: Windows path under Git Bash
version="$(ls "$artifacts"/StoveDotnet.[0-9]*.nupkg | head -n1 | sed -E 's/.*StoveDotnet\.(.+)\.nupkg/\1/')"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
# Isolated package cache: never leave locally built versions in the user's global cache, where they would shadow the
# same version published to nuget.org.
export NUGET_PACKAGES="$work/packages"

cd "$work"
cat > global.json <<'EOF'
{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }
EOF
cat > nuget.config <<EOF
<configuration>
  <packageSources>
    <clear />
    <add key="artifacts" value="$artifacts" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF
cat > Smoke.csproj <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="StoveDotnet" Version="$version" />
    <PackageReference Include="StoveDotnet.AspNetCore" Version="$version" />
    <PackageReference Include="StoveDotnet.Http" Version="$version" />
    <PackageReference Include="StoveDotnet.Kafka" Version="$version" />
    <PackageReference Include="StoveDotnet.Postgres" Version="$version" />
    <PackageReference Include="StoveDotnet.Redis" Version="$version" />
    <PackageReference Include="StoveDotnet.Telemetry" Version="$version" />
    <PackageReference Include="StoveDotnet.WireMock" Version="$version" />
  </ItemGroup>
</Project>
EOF
cat > Program.cs <<'EOF'
using StoveDotnet;
using StoveDotnet.Http;
using StoveDotnet.Kafka;
using StoveDotnet.Postgres;
using StoveDotnet.Redis;
using StoveDotnet.Telemetry;
using StoveDotnet.WireMock;

var builder = StoveBuilder.Create()
    .WithTelemetry()
    .WithPostgres(o => o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:Db", c.ConnectionString)])
    .WithKafka()
    .WithRedis()
    .WithWireMock("payments")
    .WithHttpClient(o => o.BaseAddress = new Uri("http://localhost"));

Console.WriteLine($"StoveDotnet packages restored and compiled ({builder.GetType().Name})");

// Runtime check without containers: start WireMock from the packages and run a real stove.Test against it.
await using var stove = await StoveBuilder.Create().WithWireMock("payments").StartAsync();
await stove.Test(async t =>
{
    var payments = t.WireMock("payments");
    payments.MockGet("/items/{id}", responseBody: new { ok = true });
    using var client = new HttpClient { BaseAddress = payments.ExposedConfiguration.BaseUrl };
    var body = await client.GetStringAsync("/items/42");
    var requests = await payments.ShouldHaveBeenCalled("GET", "/items/{id}");
    if (body != "{\"ok\":true}" || requests[0].PathParameters["id"] != "42")
    {
        throw new InvalidOperationException($"Unexpected WireMock behavior: {body}");
    }
});
Console.WriteLine("StoveDotnet.WireMock works at runtime");
EOF

dotnet build -c Release --nologo -v quiet
dotnet run -c Release --no-build
echo "Package smoke test passed for version $version"
