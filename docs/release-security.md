# Release security

StoveDotnet publishes with a short-lived credential from NuGet Trusted Publishing. No NuGet API key is stored in
GitHub. The trusted identity is intentionally narrow: repository `cokceken/stove-dotnet`, workflow `release.yml`, and
GitHub environment `nuget.org` must all match the nuget.org policy.

## Release path

1. A protected `v*` tag points to a commit already contained in `origin/main`.
2. The reusable test matrix runs with read-only repository access.
3. The `prepare` job first rejects a version that is not valid SemVer or not higher than every published version of
   every package: a version on nuget.org can be unlisted but never replaced. It then builds, packs, verifies
   `eng/package-manifest.txt`, and runs package-consumer checks with read-only repository access. Its package artifact
   expires after one day.
4. The `publish` job downloads only that verified artifact and waits for approval on the protected `nuget.org`
   environment. Only this job receives `contents: write`, `id-token: write` and `attestations: write`.
5. The job attests the build provenance of every package, creates a draft GitHub release containing every package and
   symbol package, exchanges its environment-bound OIDC token for a one-hour NuGet key, pushes the packages one at a
   time with dependencies first and without `--skip-duplicate`, and then publishes the GitHub release.
6. GitHub release immutability locks the tag and release assets after publication.

The packages pin each other to exact versions. Pushing dependencies first means that, if a release stops partway,
every package that reached nuget.org can still be restored.

## Verifying a package

Each `.nupkg` attached to a GitHub release has a signed provenance attestation: it was built by `release.yml` in this
repository from the tagged commit.

```shell
gh attestation verify StoveDotnet.0.2.0.nupkg --repo cokceken/stove-dotnet
```

Verify the file from the GitHub release. nuget.org adds its own repository signature to every package it accepts,
which changes the file, so a copy downloaded from nuget.org does not match the attested digest.

GitHub Actions are pinned to full commit SHAs and repository settings require SHA pinning. Repository Actions policy
allows GitHub-owned actions (including `actions/attest-build-provenance`) plus the explicitly approved `NuGet/login`
action. Default workflow permissions are
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
manifest contains every intended package ID and no others, CI is green on `main`, and the release notes in
`release-notes/<tag>.md` (required for a stable version) are complete and contain no sensitive information. With one
maintainer the environment approval is a pause, not an independent review: the GitHub and nuget.org accounts are the
trust root, so both need phishing-resistant sign-in and offline recovery codes. Request and maintain the `StoveDotnet` package ID prefix reservation separately on nuget.org.

If publication stops after any package reaches nuget.org, do not reuse or move the tag and do not conceal the failure
with `--skip-duplicate`. Inspect the immutable package state, retain the draft GitHub release as evidence while deciding
recovery, and publish a new version when necessary.
