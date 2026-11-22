#Requires -Version 7.0
<#
.SYNOPSIS
Fails unless the release version is a valid semantic version that is higher than everything already published.

.DESCRIPTION
A version on nuget.org can be unlisted but never replaced or removed. This check runs before anything is built or
pushed and compares the version from the release tag with the published versions of every package in
eng/package-manifest.txt. A package that has never been published is skipped.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [string] $Source = 'https://api.nuget.org/v3-flatcontainer'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-version.ps1')

if (-not (Test-SemVer $Version)) {
    throw "Release version '$Version' is not a Semantic Versioning 2.0.0 version."
}

$manifestPath = Join-Path $PSScriptRoot '../eng/package-manifest.txt'
$packageIds = @(Get-Content $manifestPath |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -and -not $_.StartsWith('#') })
if ($packageIds.Count -eq 0) {
    throw "Package manifest '$manifestPath' is empty."
}

foreach ($packageId in $packageIds) {
    $url = "$Source/$($packageId.ToLowerInvariant())/index.json"
    try {
        $published = @((Invoke-RestMethod -Uri $url -MaximumRetryCount 3 -RetryIntervalSec 2).versions)
    } catch {
        # A package that does not exist yet has no index. Anything else must not be mistaken for "nothing published".
        if ($_.Exception.Response.StatusCode.value__ -eq 404) {
            Write-Host "$packageId has not been published yet."
            continue
        }
        throw "Could not read the published versions of $packageId from ${url}: $($_.Exception.Message)"
    }
    Assert-ReleaseVersion -Version $Version -PackageId $packageId -Published $published
}

Write-Host "Release version $Version is higher than every published version of $($packageIds.Count) packages."
