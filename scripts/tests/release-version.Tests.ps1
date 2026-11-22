#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../release-version.ps1')

$checks = 0

foreach ($valid in @('0.2.0', '1.0.0', '0.2.0-preview.0.1', '1.0.0-rc.1', '1.0.0-alpha-1', '1.0.0+build.5', '10.20.30-x.7.z.92')) {
    if (-not (Test-SemVer $valid)) { throw "'$valid' should be a valid version." }
    $checks++
}

# A leading v, missing parts, leading zeros and empty identifiers are the typical tag mistakes.
foreach ($invalid in @('', 'v0.2.0', '0.2', '0.2.0.1', '01.2.0', '0.2.0-', '0.2.0-preview..1', '0.2.0-preview.01', '0.2.0 ', 'latest')) {
    if (Test-SemVer $invalid) { throw "'$invalid' should not be a valid version." }
    $checks++
}

# Each version is lower than the one after it (the ordering example of the specification, plus the cases that a
# string comparison of the pre-release part gets wrong).
$ascending = @(
    '0.1.6', '0.2.0-alpha', '0.2.0-alpha.1', '0.2.0-alpha.beta', '0.2.0-beta', '0.2.0-beta.2', '0.2.0-beta.11',
    '0.2.0-preview.0.1', '0.2.0-preview.0.9', '0.2.0-preview.0.10', '0.2.0-rc.1', '0.2.0', '0.2.1', '0.10.0', '1.0.0')
for ($i = 0; $i -lt $ascending.Count - 1; $i++) {
    $lower = $ascending[$i]
    $higher = $ascending[$i + 1]
    if ((Compare-SemVer $lower $higher) -ge 0) { throw "'$lower' should be lower than '$higher'." }
    if ((Compare-SemVer $higher $lower) -le 0) { throw "'$higher' should be higher than '$lower'." }
    $checks += 2
}
if ((Compare-SemVer '1.0.0+build.1' '1.0.0+build.2') -ne 0) { throw 'Build metadata must not affect ordering.' }
if ((Compare-SemVer '0.2.0-preview.0.1' '0.2.0-preview.0.1') -ne 0) { throw 'A version must equal itself.' }
$checks += 2

# Accepted: higher than everything published, or nothing published yet.
Assert-ReleaseVersion -Version '0.2.0-preview.0.1' -PackageId 'StoveDotnet' -Published @('0.1.5', '0.1.6', '0.1.0-preview.2')
Assert-ReleaseVersion -Version '0.2.0' -PackageId 'StoveDotnet' -Published @('0.2.0-preview.0.1', '0.1.6')
Assert-ReleaseVersion -Version '0.1.0' -PackageId 'StoveDotnet.New' -Published @()
$checks += 3

# Rejected: a republish, a lower version, a pre-release of an already released version, and a malformed version.
foreach ($case in @(
    @{ Version = '0.1.6'; Published = @('0.1.5', '0.1.6') },
    @{ Version = '0.1.5'; Published = @('0.1.6') },
    @{ Version = '0.2.0-preview.0.2'; Published = @('0.2.0') },
    @{ Version = '0.2.0-preview.0.9'; Published = @('0.2.0-preview.0.10') },
    @{ Version = 'v0.3.0'; Published = @('0.2.0') }
)) {
    $rejected = $false
    try { Assert-ReleaseVersion -Version $case.Version -PackageId 'StoveDotnet' -Published $case.Published } catch { $rejected = $true }
    if (-not $rejected) { throw "Version '$($case.Version)' should be rejected against $($case.Published -join ', ')." }
    $checks++
}

Write-Host "$checks release version checks passed."
