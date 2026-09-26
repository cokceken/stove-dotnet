[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ArtifactsDirectory,

    [string] $Version
)

$ErrorActionPreference = 'Stop'

$manifestPath = Join-Path $PSScriptRoot '../eng/package-manifest.txt'
$packageIds = @(Get-Content $manifestPath |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -and -not $_.StartsWith('#') })

if ($packageIds.Count -eq 0) {
    throw "Package manifest '$manifestPath' is empty."
}

$duplicates = @($packageIds | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
if ($duplicates.Count -gt 0) {
    throw "Package manifest contains duplicate IDs: $($duplicates -join ', ')"
}

if (-not $Version) {
    $corePackage = @(Get-ChildItem -LiteralPath $ArtifactsDirectory -Filter 'StoveDotnet.*.nupkg' -File |
        Where-Object Name -Match '^StoveDotnet\.(?<version>\d.*)\.nupkg$')
    if ($corePackage.Count -ne 1) {
        throw "Expected exactly one core StoveDotnet package to determine the version; found $($corePackage.Count)."
    }

    if ($corePackage[0].Name -notmatch '^StoveDotnet\.(?<version>\d.*)\.nupkg$') {
        throw "Could not determine the package version from '$($corePackage[0].Name)'."
    }

    $Version = $Matches.version
}

function Assert-ExactFileSet {
    param(
        [Parameter(Mandatory)]
        [string] $Extension
    )

    $expected = @($packageIds | ForEach-Object { "$($_).$Version.$Extension" } | Sort-Object)
    $actual = @(Get-ChildItem -LiteralPath $ArtifactsDirectory -Filter "*.$Extension" -File |
        ForEach-Object Name |
        Sort-Object)
    $difference = @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual)

    if ($difference.Count -gt 0) {
        $details = $difference | ForEach-Object {
            if ($_.SideIndicator -eq '<=') { "missing: $($_.InputObject)" } else { "unexpected: $($_.InputObject)" }
        }
        throw "Release package manifest mismatch for .$Extension files:`n$($details -join "`n")"
    }
}

Assert-ExactFileSet -Extension 'nupkg'
Assert-ExactFileSet -Extension 'snupkg'

Write-Host "Verified $($packageIds.Count) packages and symbol packages for version $Version."
