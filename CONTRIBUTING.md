# Contributing

## Build and test

```shell
dotnet build -c Release                                          # warnings are errors
dotnet test --project tests/StoveDotnet.UnitTests                # core only
dotnet test --project tests/StoveDotnet.Hosting.AcceptanceTests  # no containers
dotnet test --project tests/StoveDotnet.Postgres.AcceptanceTests # Docker or Podman
dotnet test --project tests/StoveDotnet.SqlServer.AcceptanceTests
dotnet test --project tests/StoveDotnet.MongoDb.AcceptanceTests
dotnet test --project tests/StoveDotnet.MySql.AcceptanceTests
dotnet test --project tests/StoveDotnet.Redis.AcceptanceTests
dotnet test --project tests/StoveDotnet.Kafka.AcceptanceTests
dotnet test --project tests/StoveDotnet.RabbitMq.AcceptanceTests
dotnet test --project examples/OrderService.E2ETests.XunitV3     # Docker or Podman
```

Each module suite references only its own provider. Database suites share provider-neutral contracts in
`tests/StoveDotnet.Testing` and exercise native-client applications under `tests/TestApps`. Keep core tests free of
hosting/module dependencies and keep OrderService focused on composition. See [module conventions](docs/modules.md)
for the coverage expected of new modules. Add each suite to `.github/workflows/test-suites.yml`; CI and release both
run that matrix before packaging.

## Agent skill

- **Canonical copy:** `.agents/skills/stove-dotnet/`. Edit it there.
- **Plugin copy:** the Claude Code plugin in `plugins/stove-dotnet/` carries its own copy, because the plugin installer
  only copies the plugin folder. After editing the skill, sync it:

  ```shell
  rm -rf plugins/stove-dotnet/skills/stove-dotnet && cp -r .agents/skills/stove-dotnet plugins/stove-dotnet/skills/
  ```

  CI fails when the two copies differ.
- **Keep the skill honest:**
  - API signatures in the guides must match `src/`.
  - The fake template in `openapi-fakes.md` mirrors `examples/OrderService.E2ETests.XunitV3/Fakes/`, which runs in CI.
- **Validate:** `claude plugin validate . --strict` (marketplace) and `claude plugin validate plugins/stove-dotnet --strict`.
- **Plugin version:** bump `version` in `plugins/stove-dotnet/.claude-plugin/plugin.json` when the skill changes in a
  release.

## Packaging

Packages are built from the `src/` projects. Versions come from git tags through
[MinVer](https://github.com/adamralph/minver):

- tag `v0.1.0-preview.1` produces version `0.1.0-preview.1`;
- untagged commits produce `x.y.z-preview.0.<height>`.

`scripts/package-smoke-test.sh <artifacts>` compiles a throwaway project against the packed packages, using an isolated
package cache. CI runs it after `dotnet pack`.

## Releasing to nuget.org

Releases are published by `.github/workflows/release.yml` when a `v*` tag is pushed. It authenticates with
[NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), so no API key is stored.

**One-time setup:**

1. Sign in to nuget.org with the account that will own the packages.
2. Under **Trusted Publishing**, add a policy: repository owner `cokceken`, repository `stove-dotnet`, workflow file
   `release.yml`, no environment.
3. In the GitHub repository settings, go to **Secrets and variables → Actions → Variables** and add `NUGET_USER` with
   that nuget.org account name.
4. Optional: request ID prefix reservation for `StoveDotnet` on nuget.org, so only your account can publish
   `StoveDotnet.*`.

**Each release:**

```shell
git switch main && git pull
git tag v0.1.0-preview.1
git push origin v0.1.0-preview.1
```

The workflow builds, runs all tests, packs, checks that the package version matches the tag, runs the package smoke
test, pushes the packages and symbols to nuget.org, and creates a GitHub release. Tags with a `-suffix` become
pre-releases. Packages on nuget.org cannot be deleted, only unlisted, so tag deliberately.

## Framework adoption examples

`examples/Frameworks` contains separate xUnit v3, NUnit, MSTest and TUnit projects, with framework-neutral
application/environment projects. Keep the framework lifecycle and assertions visible in each example.
Run `pwsh -File scripts/verify-framework-examples.ps1` with Docker or Podman running to verify full and filtered
runs, intentional failure/skip outcomes, failure evidence and cleanup. Add `-NoBuild` after a Release build.
After packing, run with `-UsePackages` to repeat the checks in a fresh consumer directory and package cache.
CI/release run both forms and retain verification logs. See [the adopter guide](docs/test-frameworks.md).

`pwsh -File scripts/tests/framework-output.Tests.ps1` checks the runner summary parser without containers.
It covers plain and ANSI-colored output with Linux and Windows line endings, and rejects incorrect or missing
counts. Verification preserves raw logs while removing terminal color codes from the text it validates.
