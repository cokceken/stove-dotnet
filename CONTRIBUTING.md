# Contributing

## Build and test

```shell
dotnet build -c Release                                          # warnings are errors
dotnet test --project tests/StoveDotnet.UnitTests                # core only
dotnet test --project tests/StoveDotnet.Hosting.AcceptanceTests  # no containers
dotnet test --project tests/StoveDotnet.Containers.AcceptanceTests # Docker or Podman
dotnet test --project examples/CustomContainer/Tests             # MinIO through a real API
dotnet test --project tests/StoveDotnet.Time.AcceptanceTests     # no containers
dotnet test --project tests/StoveDotnet.Oidc.AcceptanceTests     # real JWT bearer, no containers
dotnet test --project tests/StoveDotnet.Postgres.AcceptanceTests # Docker or Podman
dotnet test --project tests/StoveDotnet.SqlServer.AcceptanceTests
dotnet test --project tests/StoveDotnet.MongoDb.AcceptanceTests
dotnet test --project tests/StoveDotnet.MySql.AcceptanceTests
dotnet test --project tests/StoveDotnet.Redis.AcceptanceTests
dotnet test --project tests/StoveDotnet.Kafka.AcceptanceTests
dotnet test --project tests/StoveDotnet.RabbitMq.AcceptanceTests
dotnet test --project tests/StoveDotnet.Azure.ServiceBus.AcceptanceTests
dotnet test --project examples/Frameworks/OrderService.E2ETests.XunitV3     # Docker or Podman
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
  - The fake template in `openapi-fakes.md` mirrors `examples/Frameworks/OrderService.E2ETests.XunitV3/Fakes/`, which runs in CI.
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
   `release.yml`, environment `nuget.org`. The environment claim is required: a policy without it also trusts release
   jobs that have not passed the protected GitHub environment.
3. In the GitHub repository settings, go to **Secrets and variables → Actions → Variables** and add `NUGET_USER` with
   that nuget.org account name.
4. Configure the GitHub `nuget.org` environment to allow only `v*` tags and require a reviewer. When there is a second
   maintainer, enable **Prevent self-review**.
5. Request ID prefix reservation for `StoveDotnet` on nuget.org, so only the owning account can publish
   `StoveDotnet` and `StoveDotnet.*`.

**Each release:**

```shell
git switch main && git pull
git tag v0.1.0-preview.1
git push origin v0.1.0-preview.1
```

Only tag a commit already contained in `origin/main`. The workflow runs all tests, builds and packs without publishing
permissions, verifies the exact IDs in `eng/package-manifest.txt`, runs isolated package checks, and uploads a one-day
artifact. The separate `publish` job then waits for approval on the `nuget.org` environment, exchanges its OIDC token
for a short-lived NuGet API key, creates a draft GitHub release, pushes the packages, and publishes the now-immutable
release. Tags with a `-suffix` become pre-releases.

There is deliberately no `--skip-duplicate`: a duplicate or partially published version stops the release for manual
investigation. Packages on nuget.org cannot be deleted, only unlisted, so tag deliberately. If a run fails after the
draft release is created, inspect the nuget.org package state and delete the draft only after deciding how to recover.
See `docs/release-security.md` for the trust boundaries and administrative controls.

## Framework adoption examples

`dotnet test --project examples/MultiApplication/Tests -c Release` verifies the separate API/worker flow using
PostgreSQL and RabbitMQ. Core application lifecycle tests and container-free hosting tests cover name resolution,
configuration isolation, readiness, rollback, client targeting and application log attribution.
The isolation scenario starts independent environments concurrently with the same business key. Time tests cover
HTTP delays, worker timers and serialized shared-clock fixtures; OIDC tests exercise the real bearer pipeline.
See `docs/practical-testing.md` and `examples/Isolation` for the supported ownership and cancellation semantics.

`examples/Frameworks` contains separate xUnit v3, NUnit, MSTest and TUnit projects, with framework-neutral
application/environment projects. Keep the framework lifecycle and assertions visible in each example.
Run `pwsh -File scripts/verify-framework-examples.ps1` with Docker or Podman running to verify full and filtered
runs, intentional failure/skip outcomes, failure evidence and cleanup. Add `-NoBuild` after a Release build.
After packing, run with `-UsePackages` to repeat the checks in a fresh consumer directory and package cache.
CI/release run both forms and retain verification logs. See [the adopter guide](docs/test-frameworks.md).

`pwsh -File scripts/tests/framework-output.Tests.ps1` checks the runner summary parser without containers.
It covers plain and ANSI-colored output with Linux and Windows line endings, and rejects incorrect or missing
counts. Verification preserves raw logs while removing terminal color codes from the text it validates.
