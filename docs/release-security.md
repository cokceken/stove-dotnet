# Release security

StoveDotnet publishes with a short-lived credential from NuGet Trusted Publishing. No NuGet API key is stored in
GitHub. The trusted identity is intentionally narrow: repository `cokceken/stove-dotnet`, workflow `release.yml`, and
GitHub environment `nuget.org` must all match the nuget.org policy.

## Release path

1. A protected `v*` tag points to a commit already contained in `origin/main`.
2. The reusable test matrix runs with read-only repository access.
3. The `prepare` job builds, packs, verifies `eng/package-manifest.txt`, and runs package-consumer checks with read-only
   repository access. Its package artifact expires after one day.
4. The `publish` job downloads only that verified artifact and waits for approval on the protected `nuget.org`
   environment. Only this job receives `contents: write` and `id-token: write`.
5. The job creates a draft GitHub release containing every package and symbol package, exchanges its environment-bound
   OIDC token for a one-hour NuGet key, publishes without `--skip-duplicate`, and then publishes the GitHub release.
6. GitHub release immutability locks the tag and release assets after publication.

GitHub Actions are pinned to full commit SHAs and repository settings require SHA pinning. Repository Actions policy
allows GitHub-owned actions plus the explicitly approved `NuGet/login` action. Default workflow permissions are
read-only, and workflows cannot approve pull requests.

## Administrative controls

- `main` requires a pull request and the `packages` check. The solo-maintainer configuration requires no approval
  because self-approval is not an independent control; remove any permanent bypass. With a second maintainer, require
  one approval, code-owner review, last-push approval, and prevent self-review on the publishing environment.
- The `v*` tag ruleset rejects deletion and non-fast-forward updates.
- Dependabot security updates, vulnerability alerts, CodeQL default setup, secret scanning with push protection, and
  private vulnerability reporting are enabled.
- `NuGet.Config` clears inherited package sources and audits direct and transitive dependencies against nuget.org.
- `global.json` accepts only patches in the selected SDK feature band.

## Operator checklist

Before publishing, confirm that the nuget.org Trusted Publishing policy includes environment `nuget.org`, the package
manifest contains every intended package ID and no others, CI is green on `main`, and the release notes contain no
sensitive information. Request and maintain the `StoveDotnet` package ID prefix reservation separately on nuget.org.

If publication stops after any package reaches nuget.org, do not reuse or move the tag and do not conceal the failure
with `--skip-duplicate`. Inspect the immutable package state, retain the draft GitHub release as evidence while deciding
recovery, and publish a new version when necessary.
