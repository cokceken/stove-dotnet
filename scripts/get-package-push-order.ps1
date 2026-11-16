#Requires -Version 7.0
<#
.SYNOPSIS
Lists the packages of a directory so that every package comes after the packages it depends on.

.DESCRIPTION
StoveDotnet packages depend on each other with exact versions. Pushing them in this order means that, if a release
stops partway, every package that reached nuget.org can still be restored: its dependencies were pushed before it.
Only dependencies on packages in the same directory affect the order.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ArtifactsDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$packages = @{}
foreach ($file in Get-ChildItem -LiteralPath $ArtifactsDirectory -Filter '*.nupkg' -File) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $entry = @($archive.Entries | Where-Object { $_.FullName -notmatch '/' -and $_.FullName -like '*.nuspec' })
        if ($entry.Count -ne 1) { throw "Expected exactly one nuspec in '$($file.Name)'; found $($entry.Count)." }
        $reader = [System.IO.StreamReader]::new($entry[0].Open())
        try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally {
        $archive.Dispose()
    }
    $id = [string] $nuspec.package.metadata.id
    if ($packages.ContainsKey($id)) { throw "Package '$id' appears more than once in '$ArtifactsDirectory'." }
    $dependencies = @($nuspec.SelectNodes("//*[local-name()='dependency']") | ForEach-Object { [string] $_.id } | Sort-Object -Unique)
    $packages[$id] = [pscustomobject]@{ Path = $file.FullName; Dependencies = $dependencies }
}
if ($packages.Count -eq 0) { throw "No packages found in '$ArtifactsDirectory'." }

$ordered = [System.Collections.Generic.List[string]]::new()
$done = [System.Collections.Generic.HashSet[string]]::new()
$visiting = [System.Collections.Generic.HashSet[string]]::new()

function Visit([string] $id) {
    if ($done.Contains($id)) { return }
    if (-not $visiting.Add($id)) { throw "Package dependency cycle at '$id'." }
    foreach ($dependency in $packages[$id].Dependencies) {
        if ($packages.ContainsKey($dependency)) { Visit $dependency }
    }
    [void] $visiting.Remove($id)
    [void] $done.Add($id)
    $ordered.Add($packages[$id].Path)
}

# Sorted input makes the order the same on every run.
foreach ($id in ($packages.Keys | Sort-Object)) { Visit $id }
$ordered
