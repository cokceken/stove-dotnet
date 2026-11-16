# Semantic Versioning 2.0.0 helpers for the release checks. PowerShell's own SemanticVersion type compares the
# pre-release part as one string, which orders preview.0.10 before preview.0.9.

$script:SemVerPattern = '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)' +
    '(?:-(?<pre>(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?' +
    '(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'

function Test-SemVer {
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Version)
    return $Version -cmatch $script:SemVerPattern
}

function ConvertTo-SemVerParts {
    param([Parameter(Mandatory)][string] $Version)
    if ($Version -cnotmatch $script:SemVerPattern) {
        throw "'$Version' is not a Semantic Versioning 2.0.0 version."
    }
    return [pscustomobject]@{
        Core = @([bigint]$Matches.major, [bigint]$Matches.minor, [bigint]$Matches.patch)
        Pre  = if ($Matches.pre) { @($Matches.pre -split '\.') } else { @() }
    }
}

# Returns a negative number, zero or a positive number when $Left is lower than, equal to or higher than $Right.
# Build metadata is ignored, as the specification requires.
function Compare-SemVer {
    param([Parameter(Mandatory)][string] $Left, [Parameter(Mandatory)][string] $Right)
    $l = ConvertTo-SemVerParts $Left
    $r = ConvertTo-SemVerParts $Right
    for ($i = 0; $i -lt 3; $i++) {
        $order = $l.Core[$i].CompareTo($r.Core[$i])
        if ($order -ne 0) { return $order }
    }
    # A version without a pre-release part is higher than the same version with one.
    if ($l.Pre.Count -eq 0 -and $r.Pre.Count -eq 0) { return 0 }
    if ($l.Pre.Count -eq 0) { return 1 }
    if ($r.Pre.Count -eq 0) { return -1 }
    for ($i = 0; $i -lt [Math]::Min($l.Pre.Count, $r.Pre.Count); $i++) {
        $a = $l.Pre[$i]
        $b = $r.Pre[$i]
        $aNumeric = $a -match '^\d+$'
        $bNumeric = $b -match '^\d+$'
        if ($aNumeric -and $bNumeric) {
            $order = ([bigint]$a).CompareTo([bigint]$b)
        } elseif ($aNumeric) {
            $order = -1   # numeric identifiers are lower than alphanumeric ones
        } elseif ($bNumeric) {
            $order = 1
        } else {
            $order = [string]::CompareOrdinal($a, $b)
        }
        if ($order -ne 0) { return [Math]::Sign($order) }
    }
    return $l.Pre.Count.CompareTo($r.Pre.Count)
}

# Fails unless $Version is valid and higher than every version in $Published. NuGet versions cannot be replaced or
# deleted, so a mistyped tag has to be caught before anything is pushed.
function Assert-ReleaseVersion {
    param(
        [Parameter(Mandatory)][string] $Version,
        [Parameter(Mandatory)][string] $PackageId,
        [string[]] $Published = @()
    )
    if (-not (Test-SemVer $Version)) {
        throw "Release version '$Version' is not a Semantic Versioning 2.0.0 version."
    }
    $valid = @($Published | Where-Object { Test-SemVer $_ })
    if ($valid.Count -eq 0) { return }
    $highest = $valid[0]
    foreach ($candidate in $valid) {
        if ((Compare-SemVer $candidate $highest) -gt 0) { $highest = $candidate }
    }
    if ((Compare-SemVer $Version $highest) -le 0) {
        throw "Release version '$Version' is not higher than '$highest', the highest published version of $PackageId."
    }
}
