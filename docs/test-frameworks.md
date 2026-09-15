# Adopting Stove with your test framework

Stove has no test framework dependency. Your framework owns setup, assertions and teardown; Stove owns
the application and dependencies inside that lifetime. Start with one test project and the modules your
application actually needs. The [runnable examples](../examples/Frameworks/README.md) use PostgreSQL and an
HTTP catalog contract so you can follow a complete request through a real application.

## Choose a framework and runner

These examples target `net10.0` and Microsoft.Testing.Platform (MTP). The repository's `global.json` selects
that runner. A test framework and a test platform are separate choices: these checks demonstrate framework
independence on MTP, not verified compatibility with every VSTest or IDE configuration.

| Framework / tested packages | Lifetime in the example | Token passed to `stove.Test` |
| --- | --- | --- |
| [xUnit v3](../examples/Frameworks/Xunit/OrderTests.cs), `xunit.v3` 4.0.1 | Assembly fixture, `IAsyncLifetime` | `TestContext.Current.CancellationToken` |
| [NUnit](../examples/Frameworks/NUnit/OrderTests.cs), `NUnit` 4.6.1 + `NUnit3TestAdapter` 6.3.0 | Namespace `SetUpFixture`, `OneTimeSetUp` / `OneTimeTearDown` | `TestContext.CurrentContext.CancellationToken` |
| [MSTest](../examples/Frameworks/MSTest/OrderTests.cs), `MSTest` 4.4.0 | Static `AssemblyInitialize` / `AssemblyCleanup` in a nonstatic `[TestClass]` | Injected `TestContext.CancellationToken` |
| [TUnit](../examples/Frameworks/TUnit/OrderTests.cs), `TUnit` 1.67.0 | Static `Before(TestSession)` / `After(TestSession)` | `TestContext.Current!.Execution.CancellationToken` |

Keep NUnit tests within the setup fixture's namespace. Assembly/session hooks run once per runner process,
not once across every test project or CI shard. NUnit and MSTest examples explicitly enable method parallelism;
TUnit uses its default scheduling. An xUnit assembly fixture does not itself enable parallel methods in one class.

## Start a separate consumer

Copy the lifecycle and tests from your chosen example into your own test project. Reference your application
project and make its entry point accessible (`public partial class Program` for a top-level ASP.NET Core app).
Use the following as a standalone NUnit project; replace `YOUR_STOVE_VERSION` with one available package version
and use that same version for every Stove module. The placeholder is intentionally not a release claim.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableNUnitRunner>true</EnableNUnitRunner>
    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NUnit" Version="4.6.1" />
    <PackageReference Include="NUnit3TestAdapter" Version="6.3.0" />
    <PackageReference Include="StoveDotnet.AspNetCore" Version="YOUR_STOVE_VERSION" />
    <PackageReference Include="StoveDotnet.Http" Version="YOUR_STOVE_VERSION" />
    <PackageReference Include="StoveDotnet.Postgres" Version="YOUR_STOVE_VERSION" />
    <PackageReference Include="StoveDotnet.WireMock" Version="YOUR_STOVE_VERSION" />
    <ProjectReference Include="../YourApplication/YourApplication.csproj" />
  </ItemGroup>
</Project>
```

For xUnit, replace the NUnit packages with `xunit.v3` and remove both NUnit runner properties. For TUnit,
use `TUnit` and remove those properties. For MSTest, use the `MSTest` metapackage, replace `EnableNUnitRunner`
with `EnableMSTestRunner`, and keep `TestingPlatformDotnetTestSupport`. Keep `OutputType` as `Exe` in all cases.
The checked-in `.csproj` files omit package versions because this repository uses central package management;
standalone projects need explicit versions as above (or their own `Directory.Packages.props`).

Place this `global.json` at your consumer repository root, adapting its SDK policy to your project:

```json
{
  "sdk": { "version": "10.0.100", "rollForward": "latestFeature" },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

Docker or Podman must be running and reachable through its Docker-compatible API. Preserve the container
configuration that already works on your machine; `DOCKER_HOST` is only needed when your runtime's endpoint
is not detected automatically. The examples require no manually provisioned database, fixed ports or credentials.

## Adapt the environment to your application

Use [ExampleEnvironment](../examples/Frameworks/Shared/ExampleEnvironment.cs) as a small setup reference:

1. Register only the dependencies the application needs. Map each module's exposed configuration to the actual
   application configuration keys before starting the application.
2. Run schema setup through migrations so application startup sees a ready database. Replace the example SQL
   with your application's migration mechanism; don't maintain a second production schema in tests.
3. Start Stove from the framework's once-per-run hook and dispose it from teardown. Dispose even when tests fail.
4. Wrap each scenario in `stove.Test`, passing the framework token. Inside, module operations use the scope's
   cancellation token. Assert HTTP contracts, persistence and outgoing dependency requests using native assertions.

For contract-heavy services, a useful first slice is a valid request plus an upstream rejection: verify status,
response payload, persistence (or its absence) and the outgoing request. The sample only demonstrates the valid
slice; add your actual timeout, invalid payload, authentication and broker contracts as needed. WireMock proves
behavior against your configured contract; keeping that contract aligned with the external provider remains necessary.

Shared containers do not imply isolated database state. Use unique business keys and seed only the rows each test
needs, as these examples do. Avoid a whole-database reset while other tests are running. Scoped WireMock stubs
need test correlation propagated by the application; the sample's outgoing HttpClient calls participate in tracing.
See [module conventions](modules.md) for provider behavior and the main README for correlation details.

## Run and diagnose a single test

From the Stove repository root:

```shell
dotnet test --project examples/Frameworks/Xunit -c Release --filter "FullyQualifiedName~Creates_order_through_real_application"
dotnet test --project examples/Frameworks/NUnit -c Release --filter "FullyQualifiedName~Creates_order_through_real_application"
dotnet test --project examples/Frameworks/MSTest -c Release --filter "FullyQualifiedName~Creates_order_through_real_application"
dotnet test --project examples/Frameworks/TUnit -c Release --treenode-filter "/*/*/*/Creates_order_through_real_application"
```

Use your own project path in a consumer. These are .NET 10 MTP commands; no extra `--` separator is needed.
An assertion failure inside `stove.Test` produces `StoveTestFailedException` with the original failure, test name,
trace ID and module evidence in runner output. Cancellation and recognized framework skip/inconclusive exceptions
pass through. The [verification script](../scripts/verify-framework-examples.ps1) tests these outcomes in actual
runner processes and checks teardown; the ordinary example suites contain no deliberately failing tests.
