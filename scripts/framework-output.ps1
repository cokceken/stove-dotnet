function ConvertTo-PlainFrameworkOutput {
    param([Parameter(Mandatory)][string] $Output)
    # The .NET test runner can emit ANSI SGR color/reset sequences even into redirected CI logs.
    # Keep the original log on disk; normalize only the text used for verification.
    return $Output -replace '\x1B\[[0-9;:]*m', ''
}

function Assert-FrameworkSummary {
    param(
        [Parameter(Mandatory)][string] $Output,
        [Parameter(Mandatory)][hashtable] $Expected,
        [string] $Context = 'Framework run'
    )
    $plain = ConvertTo-PlainFrameworkOutput $Output
    foreach ($entry in $Expected.GetEnumerator()) {
        if ($plain -notmatch "(?m)^\s*$($entry.Key):\s*$($entry.Value)\s*$") {
            throw "$Context unexpected summary ($($entry.Key)). Runner output:`n$plain"
        }
    }
}
