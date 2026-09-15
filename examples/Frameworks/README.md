# Test framework examples

Choose a runnable example: [xUnit v3](Xunit/OrderTests.cs), [NUnit](NUnit/OrderTests.cs),
[MSTest](MSTest/OrderTests.cs), or [TUnit](TUnit/OrderTests.cs).
See the [adopter guide](../../docs/test-frameworks.md) for package setup, lifecycle, cancellation and filtering.

From the repository root, with the .NET 10 SDK and Docker or Podman running:

```shell
dotnet test --project examples/Frameworks/Xunit -c Release
dotnet test --project examples/Frameworks/NUnit -c Release
dotnet test --project examples/Frameworks/MSTest -c Release
dotnet test --project examples/Frameworks/TUnit -c Release
```

Each project has five tests and references only the shared environment and its own framework. The
[application](SampleApi/Program.cs) uses Npgsql and HttpClient, with no Stove or test framework dependency.
The [environment](Shared/ExampleEnvironment.cs) starts PostgreSQL in Testcontainers, migrates its schema,
starts a scoped WireMock catalog and starts the real application on Kestrel. Each runner starts and disposes
one environment per run. Data uses unique IDs; database rows are not automatically reset between tests.

The tests cover HTTP-to-database writes, seeded reads, overlapping test scopes with different responses for
the same catalog URL, cancellation propagation, and a runner probe. Each example uses its framework's own
assertions. The parallel test overlaps four scopes even when a framework schedules its test methods sequentially.

`Runner_probe` passes normally. The verification script selects it alone with
`STOVE_FRAMEWORK_PROBE=failure` or `skip` to check real runner outcomes. Do not set this variable for ordinary runs.
MSTest uses inconclusive, which the runner reports as skipped.

```powershell
# PowerShell 7, all four frameworks, full/filtered/failure/skip runs:
./scripts/verify-framework-examples.ps1
# One framework, after building it:
./scripts/verify-framework-examples.ps1 -Framework NUnit -NoBuild
# Consume freshly packed packages outside the repository with a fresh package cache:
dotnet pack -c Release -o artifacts
./scripts/verify-framework-examples.ps1 -UsePackages
```

The script checks exit codes, exact test counts, failure context and lifecycle audit files. The environment
checks that its native database client is disposed and that its managed database endpoint is unreachable
after teardown, including after failed and skipped tests. These extra checks are harness verification;
ordinary adopter fixtures only need to dispose Stove. Logs default to `artifacts/framework-tests/<run-id>`.
Package consumers are retained in a printed temporary directory for inspection. `-FeedPath` and
`-PackageVersion` select an explicit local package version; otherwise the newest core package in `artifacts`
is selected. The `StovePackageVersion` build property is specific to this repository's verification harness.

CI and release run each framework separately, then repeat the checks against local NuGet packages before
publishing. These examples exercise Microsoft.Testing.Platform on .NET 10; they do not verify every IDE or
VSTest adapter combination.
