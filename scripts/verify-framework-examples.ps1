#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Xunit', 'NUnit', 'MSTest', 'TUnit')]
    [string[]] $Framework = @('Xunit', 'NUnit', 'MSTest', 'TUnit'),
    [switch] $NoBuild,
    [switch] $UsePackages,
    [string] $PackageVersion,
    [string] $FeedPath,
    [string] $ResultsDirectory
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false # Failure probes intentionally return nonzero.
. (Join-Path $PSScriptRoot 'framework-output.ps1')
$repo = Split-Path $PSScriptRoot -Parent
$root = $repo
if (!$ResultsDirectory) { $ResultsDirectory = Join-Path $repo "artifacts/framework-tests/$([guid]::NewGuid())" }
$results = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $results -Force | Out-Null
$previousProbe = $env:STOVE_FRAMEWORK_PROBE
$previousAudit = $env:STOVE_FRAMEWORK_LIFECYCLE_FILE
$previousPackages = $env:NUGET_PACKAGES
$properties = @()
try {
    if ($UsePackages) {
        if ($NoBuild) { throw '-UsePackages requires a fresh build.' }
        if (!$FeedPath) { $FeedPath = Join-Path $repo 'artifacts' }
        $feed = (Resolve-Path -LiteralPath $FeedPath).Path
        if (!$PackageVersion) {
            $package = Get-ChildItem -LiteralPath $feed -Filter 'StoveDotnet.*.nupkg' |
                Where-Object Name -Match '^StoveDotnet\.\d' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (!$package) { throw "No core package found in $feed. Run dotnet pack first." }
            $PackageVersion = $package.Name -replace '^StoveDotnet\.', '' -replace '\.nupkg$', ''
        }
        if (!(Test-Path -LiteralPath (Join-Path $feed "StoveDotnet.$PackageVersion.nupkg"))) { throw 'Requested package is missing.' }
        $root = Join-Path ([IO.Path]::GetTempPath()) "stove-framework-consumer-$([guid]::NewGuid())"
        New-Item -ItemType Directory -Path $root | Out-Null
        foreach ($name in @('Directory.Build.props', 'Directory.Packages.props', 'global.json', '.editorconfig')) {
            Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $root
        }
        $source = Join-Path $repo 'examples/Frameworks'
        Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object {
            $_.Extension -in '.cs', '.csproj' -and $_.FullName -notmatch '[\\/](bin|obj|TestResults)[\\/]'
        } | ForEach-Object {
            $destination = Join-Path $root "examples/Frameworks/$([IO.Path]::GetRelativePath($source, $_.FullName))"
            New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }
        $escapedFeed = [Security.SecurityElement]::Escape($feed)
        @"
<configuration>
  <packageSources><clear/><add key="local" value="$escapedFeed"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><clear/><packageSource key="local"><package pattern="StoveDotnet*"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $root 'nuget.config')
        $env:NUGET_PACKAGES = Join-Path $root 'packages'
        $properties = @("-p:StovePackageVersion=$PackageVersion")
        Write-Host "Isolated package consumer: $root (version $PackageVersion)"
    }
    Push-Location $root
    try {
        foreach ($name in $Framework) {
            $project = "examples/Frameworks/$name"
            if (!$NoBuild) {
                & dotnet build $project -c Release -m:1 -p:UseSharedCompilation=false @properties *> "$results/$name-build.log"
                if ($LASTEXITCODE -ne 0) { throw "$name build failed: $results/$name-build.log" }
            }
            foreach ($mode in @('full', 'filtered', 'failure', 'skip')) {
                $env:STOVE_FRAMEWORK_PROBE = if ($mode -in 'failure', 'skip') { $mode } else { $null }
                $env:STOVE_FRAMEWORK_LIFECYCLE_FILE = "$results/$name-$mode-lifecycle.json"
                if (Test-Path -LiteralPath $env:STOVE_FRAMEWORK_LIFECYCLE_FILE) {
                    Remove-Item -LiteralPath $env:STOVE_FRAMEWORK_LIFECYCLE_FILE
                }
                $arguments = @('test', '--project', $project, '-c', 'Release', '--no-build') + $properties
                if ($mode -ne 'full') {
                    $method = if ($mode -eq 'filtered') { 'Creates_order_through_real_application' } else { 'Runner_probe' }
                    $arguments += if ($name -eq 'TUnit') { @('--treenode-filter', "/*/*/*/$method") } else { @('--filter', "FullyQualifiedName~$method") }
                }
                $log = "$results/$name-$mode.log"
                & dotnet @arguments *> $log
                $code = $LASTEXITCODE
                $output = ConvertTo-PlainFrameworkOutput (Get-Content -LiteralPath $log -Raw)
                $total = if ($mode -eq 'full') { 5 } else { 1 }
                $failed = if ($mode -eq 'failure') { 1 } else { 0 }
                $skipped = if ($mode -eq 'skip') { 1 } else { 0 }
                $passed = $total - $failed - $skipped
                $codes = if ($mode -eq 'failure') { @(2) } elseif ($mode -eq 'skip') { @(0, 8) } else { @(0) }
                if ($code -notin $codes) { throw "$name/$mode unexpected exit $code. See $log" }
                Assert-FrameworkSummary -Output $output -Expected @{total=$total; failed=$failed; succeeded=$passed; skipped=$skipped} -Context "$name/$mode (log: $log)"
                if ($mode -eq 'failure') {
                    foreach ($marker in @('intentional-framework-failure', 'Stove test:', 'Trace id:', 'wiremock')) {
                        if (!$output.Contains($marker)) { throw "$name failure output missing '$marker'. See $log" }
                    }
                }
                if ($mode -eq 'skip' -and $output.Contains('StoveTestFailedException')) { throw "$name skip was wrapped. See $log" }
                $audit = Get-Content -LiteralPath $env:STOVE_FRAMEWORK_LIFECYCLE_FILE -Raw | ConvertFrom-Json
                if ($audit.Starts -ne 1 -or $audit.Stops -ne 1 -or !$audit.ClientDisposed -or !$audit.EndpointRemoved) {
                    throw "$name/$mode lifecycle verification failed."
                }
                Write-Host "$name/$mode verified (passed=$passed, failed=$failed, skipped=$skipped; cleanup verified)"
            }
        }
    } finally { Pop-Location }
} finally {
    $env:STOVE_FRAMEWORK_PROBE = $previousProbe
    $env:STOVE_FRAMEWORK_LIFECYCLE_FILE = $previousAudit
    $env:NUGET_PACKAGES = $previousPackages
    Write-Host "Framework verification logs: $results"
}
