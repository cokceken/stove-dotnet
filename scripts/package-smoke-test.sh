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
    <PackageReference Include="StoveDotnet.Hosting" Version="$version" />
    <PackageReference Include="StoveDotnet.Containers" Version="$version" />
    <PackageReference Include="StoveDotnet.Time" Version="$version" />
    <PackageReference Include="StoveDotnet.Oidc" Version="$version" />
    <PackageReference Include="StoveDotnet.Http" Version="$version" />
    <PackageReference Include="StoveDotnet.Kafka" Version="$version" />
    <PackageReference Include="StoveDotnet.Postgres" Version="$version" />
    <PackageReference Include="StoveDotnet.SqlServer" Version="$version" />
    <PackageReference Include="StoveDotnet.MongoDb" Version="$version" />
    <PackageReference Include="StoveDotnet.MySql" Version="$version" />
    <PackageReference Include="StoveDotnet.RabbitMq" Version="$version" />
    <PackageReference Include="StoveDotnet.Redis" Version="$version" />
    <PackageReference Include="StoveDotnet.Telemetry" Version="$version" />
    <PackageReference Include="StoveDotnet.WireMock" Version="$version" />
  </ItemGroup>
</Project>
EOF
cat > Program.cs <<'EOF'
using StoveDotnet;
using StoveDotnet.Hosting;
using StoveDotnet.Containers;
using DotNet.Testcontainers.Builders;
using StoveDotnet.Time;
using StoveDotnet.Oidc;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StoveDotnet.Http;
using StoveDotnet.Kafka;
using StoveDotnet.Postgres;
using StoveDotnet.SqlServer;
using StoveDotnet.MongoDb;
using StoveDotnet.MySql;
using MongoDB.Bson;
using MongoDB.Driver;
using MySqlConnector;
using StoveDotnet.RabbitMq;
using StoveDotnet.Redis;
using StoveDotnet.Telemetry;
using StoveDotnet.WireMock;

var builder = StoveBuilder.Create()
    .WithContainer("custom", o =>
    {
        o.CreateContainer = () => new ContainerBuilder("alpine:3.22").WithCommand("sleep", "300").Build();
        o.ConfigureExposedConfiguration = c => [new("Custom:Host", c.Container.Hostname)];
    })
    .WithTelemetry()
    .WithPostgres(o => o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:Db", c.ConnectionString)])
    .WithSqlServer(o => o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:SqlServer", c.ConnectionString)])
    .WithMongoDb("documents", o => o.ConfigureExposedConfiguration = c => [new("Mongo:ConnectionString", c.ConnectionString), new("Mongo:Database", c.Database)])
    .WithMySql(o => o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:MySql", c.ConnectionString)])
    .WithKafka(o => o.Observation.MaxMessagesPerTest = 1000)
    .WithRabbitMq(o => { o.Bindings.Add(new("events", "#")); o.Observation.UncorrelatedMessages = UncorrelatedMessagePolicy.Exclude; })
    .WithRedis()
    .WithWireMock("payments")
    .WithHttpClient(o => o.BaseAddress = new Uri("http://localhost"));

Console.WriteLine($"StoveDotnet packages restored and compiled ({builder.GetType().Name})");

// Compile the bounded absence API without starting a broker in this packaging check.
_ = (Func<StoveTestContext, Task>)(t => t.Kafka().ShouldNotBePublished<object>(_ => true, TimeSpan.FromSeconds(1)));

_ = (Func<StoveTestContext, Task>)(t => t.RabbitMq().ShouldNotBePublished<object>(_ => true, TimeSpan.FromSeconds(1)));

// Compile native document/relational APIs without containers.
_ = (Func<StoveTestContext, Task>)(t => t.MongoDb("documents").ShouldQuery<BsonDocument>("records", Builders<BsonDocument>.Filter.Empty, _ => { }));
_ = (Func<StoveTestContext, Task>)(t => t.MySql().Execute("select @id", new MySqlParameter("id", 1)));

// Runtime check without containers: start WireMock from the packages and run a real stove.Test against it.
await using var stove = await StoveBuilder.Create().WithWireMock("payments").WithOidc().StartAsync();
await stove.Test(async t =>
{
    var payments = t.WireMock("payments");
    var oidc = t.Oidc();
    if (oidc.IssueToken().Split('.').Length != 3) throw new InvalidOperationException("Invalid JWT shape.");
    using var metadataClient = new HttpClient();
    var metadata = await metadataClient.GetStringAsync(oidc.ExposedConfiguration.MetadataAddress);
    if (!metadata.Contains("jwks_uri", StringComparison.Ordinal)) throw new InvalidOperationException("OIDC discovery is unavailable.");
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
var clock = new FakeTimeProvider();
await using var workers = await StoveBuilder.Create()
    .WithHostApplication("worker", configuration =>
    {
        var host = Host.CreateApplicationBuilder();
        host.Configuration.AddInMemoryCollection(configuration);
        host.Services.UseStoveTime(clock);
        return host.Build();
    }, o => o.Configuration["Role"] = "worker")
    .StartAsync();
if (workers.GetApplication("worker").Services.GetRequiredService<IConfiguration>()["Role"] != "worker")
    throw new InvalidOperationException("Named host configuration was not applied.");
Console.WriteLine("StoveDotnet.Hosting works at runtime");
var timer = Task.Delay(TimeSpan.FromHours(1), clock);
clock.Advance(TimeSpan.FromHours(1));
await timer.WaitAsync(TimeSpan.FromSeconds(5));
Console.WriteLine("StoveDotnet.Time and StoveDotnet.Oidc work at runtime");
EOF

dotnet build -c Release --nologo -v quiet
dotnet run -c Release --no-build
echo "Package smoke test passed for version $version"
