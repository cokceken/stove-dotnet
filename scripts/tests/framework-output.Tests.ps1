#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../framework-output.ps1')

$cases = @(
    @{ total=5; failed=0; succeeded=5; skipped=0 },
    @{ total=1; failed=0; succeeded=1; skipped=0 },
    @{ total=1; failed=1; succeeded=0; skipped=0 },
    @{ total=1; failed=0; succeeded=0; skipped=1 }
)
$checks = 0
foreach ($expected in $cases) {
    foreach ($newline in @("`n", "`r`n")) {
        # Match the actual Linux CI layout: reset before total/skipped, green before succeeded.
        $lines = @('Test run summary:', "`e[m  total: $($expected.total)",
            "  failed: $($expected.failed)", "`e[32m  succeeded: $($expected.succeeded)",
            "`e[m  skipped: $($expected.skipped)", '  duration: 11s 369ms')
        $colored = $lines -join $newline
        foreach ($output in @($colored, (ConvertTo-PlainFrameworkOutput $colored))) {
            Assert-FrameworkSummary -Output $output -Expected $expected
            $checks++
        }
    }
}
# Normalization must not conceal incorrect counts, a missing field, or a zero-test run.
foreach ($invalid in @(
    "`e[m  total: 50`n  failed: 0`n  succeeded: 5`n  skipped: 0",
    "  total: 5`n  failed: 0`n  skipped: 0",
    "  total: 0`n  failed: 0`n  succeeded: 0`n  skipped: 0"
)) {
    $rejected = $false
    try { Assert-FrameworkSummary -Output $invalid -Expected $cases[0] }
    catch { $rejected = $true }
    if (!$rejected) { throw "Accepted an invalid summary: $invalid" }
    $checks++
}
Write-Host "$checks framework output regression checks passed."
