#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Binaries/Heron'),
    [string]$Feed,
    [switch]$List,
    [switch]$ServerOnly,
    [ValidateSet('linux-amd64', 'windows-amd64')]
    [string]$ServerRuntime = 'linux-amd64',
    [string]$GoExecutable
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

if ($ServerOnly) {
    & (Join-Path $PSScriptRoot 'Pack.Server.ps1') -Output $Output -Runtime $ServerRuntime -GoExecutable $GoExecutable -List:$List
    return
}

function Invoke-Dotnet([string[]]$Arguments) {
    Push-Location $PSScriptRoot
    try {
        & dotnet @Arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
    }
    finally { Pop-Location }
}

function Read-ProjectProperties([string]$Project) {
    $json = Invoke-Dotnet -Arguments @('msbuild', $Project, '-nologo', '-p:Configuration=Release', '-getProperty:IsPackable,PackageId,PackageVersion')
    ($json -join "`n" | ConvertFrom-Json).Properties
}

[xml]$versionFile = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Version.props') -Raw
$version = [string]$versionFile.Project.PropertyGroup.Version
$packages = @(foreach ($project in (Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Source') -Filter '*.csproj' -Recurse -File | Sort-Object FullName)) {
    $properties = Read-ProjectProperties $project.FullName
    if ($properties.IsPackable -ne 'true') { continue }
    if ($properties.PackageId -notmatch '\AHeron\.[A-Za-z0-9_.-]+\z') {
        throw "Package ID must start with Heron.: $($project.FullName)"
    }
    if ($properties.PackageVersion -ne $version) {
        throw "Package $($properties.PackageId) must use version $version from Version.props."
    }
    [pscustomobject]@{ Project = $project.FullName; Id = $properties.PackageId; Version = $properties.PackageVersion }
})
if (@($packages.Id | Sort-Object -Unique).Count -ne $packages.Count) {
    throw 'Multiple projects declare the same package ID.'
}
$outputDirectory = [System.IO.Path]::GetFullPath($Output, $PSScriptRoot)
if ($List) {
    [pscustomobject]@{ Version = $version; Output = $outputDirectory; Feed = $Feed; Packages = $packages;
        Server = "Heron.AutomationTenants.Server.$version.$ServerRuntime.tar.gz" } | ConvertTo-Json -Depth 4
    return
}
if ($packages.Count -eq 0) {
    throw 'No packable libraries under Source. Add a real library with IsPackable=true before packing.'
}

$dirty = & git -C $PSScriptRoot status --porcelain
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to inspect Heron git working tree.'
}
if (@($dirty).Count -gt 0) {
    throw "Refusing to pack with uncommitted changes in Heron. Commit first so package metadata records the exact commit."
}

# Only package archives are staged. Compilation uses each project's normal bin/obj directories.
$stagingRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'Binaries/Heron/.pack-staging'
$staging = Join-Path $stagingRoot ([guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($staging) | Out-Null
foreach ($package in $packages) {
    try { Invoke-Dotnet -Arguments @('pack', $package.Project, '-c', 'Release', '--nologo', '-o', $staging) }
    catch { throw "Packing failed: $($package.Id). Archives retained at $staging. $_" }
}
$archives = @(foreach ($package in $packages) {
    $name = "$($package.Id).$($package.Version).nupkg"
    $source = Join-Path $staging $name
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Expected package was not produced: $name" }
    [pscustomobject]@{ Name = $name; Source = $source; Sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash }
})

# Inspect final bytes after compilation and assembly rewriting.
foreach ($archive in $archives) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive.Source)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -notmatch '(^|/)Heron\.[^/]+\.dll$') { continue }
            $stream = [System.IO.MemoryStream]::new()
            $inputStream = $entry.Open()
            try { $inputStream.CopyTo($stream) } finally { $inputStream.Dispose() }
            $stream.Position = 0
            $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
            try {
                foreach ($debug in $pe.ReadDebugDirectory()) {
                    if ($debug.Type -ne [System.Reflection.PortableExecutable.DebugDirectoryEntryType]::CodeView) { continue }
                    $pdbPath = $pe.ReadCodeViewDebugDirectoryData($debug).Path
                    if ($pdbPath -match '^[A-Za-z]:[\\/]|^\\\\' -or ($pdbPath.StartsWith('/') -and !$pdbPath.StartsWith('/_/'))) {
                        throw "Physical PDB path in $($archive.Name): $pdbPath"
                    }
                }
            }
            finally { $pe.Dispose(); $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}

function Copy-Archives([string]$Directory) {
    [System.IO.Directory]::CreateDirectory($Directory) | Out-Null
    $lock = [System.IO.File]::Open((Join-Path $Directory '.heron-pack.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    try {
        # Check the entire batch before adding anything to this destination.
        foreach ($archive in $archives) {
            $destination = Join-Path $Directory $archive.Name
            if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $archive.Sha256) {
                throw "Package version already contains different bytes: $destination. Use a new version. Archives retained at $staging"
            }
        }
        foreach ($archive in $archives) {
            $destination = Join-Path $Directory $archive.Name
            if (!(Test-Path -LiteralPath $destination)) { [System.IO.File]::Copy($archive.Source, $destination, $false) }
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $archive.Sha256) {
                throw "Package copy failed verification: $destination"
            }
            Write-Host "$($archive.Name) $($archive.Sha256)"
        }
    }
    finally { $lock.Dispose() }
}

Copy-Archives $outputDirectory
if (![string]::IsNullOrWhiteSpace($Feed)) {
    Copy-Archives ([System.IO.Path]::GetFullPath($Feed, $PSScriptRoot))
}

$resolvedStaging = [System.IO.Path]::GetFullPath($staging)
$safeRoot = [System.IO.Path]::GetFullPath($stagingRoot) + [System.IO.Path]::DirectorySeparatorChar
if (!$resolvedStaging.StartsWith($safeRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean staging outside $safeRoot"
}
Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
Write-Host "Packages ready: $outputDirectory"
& (Join-Path $PSScriptRoot 'Pack.Server.ps1') -Output $outputDirectory -Runtime $ServerRuntime -GoExecutable $GoExecutable
