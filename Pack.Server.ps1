#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Binaries/Heron'),
    [ValidateSet('linux-amd64', 'windows-amd64')][string]$Runtime = 'linux-amd64',
    [string]$GoExecutable,
    [switch]$List
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
[xml]$versions = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Version.props') -Raw
$version = [string]$versions.Project.PropertyGroup.Version
$outputDirectory = [IO.Path]::GetFullPath($Output, $PSScriptRoot)
$name = "Heron.AutomationTenants.Server.$version.$Runtime.tar.gz"
if ($List) {
    [pscustomobject]@{ Version = $version; Runtime = $Runtime; Output = (Join-Path $outputDirectory $name) } | ConvertTo-Json
    return
}
$dirty = & git -C $PSScriptRoot status --porcelain
if ($LASTEXITCODE -ne 0 -or @($dirty).Count -gt 0) { throw 'Commit Heron before packing a versioned server.' }
$commit = (& git -C $PSScriptRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify server source commit.' }
if (!$GoExecutable) { $GoExecutable = $env:HERON_GO }
if (!$GoExecutable) { $GoExecutable = (Get-Command go -ErrorAction SilentlyContinue).Source }
if (!$GoExecutable) {
    $sdkRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'sdk'
    $GoExecutable = @(Get-ChildItem -LiteralPath $sdkRoot -Directory -Filter 'go*' -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName $(if ($IsWindows) { 'bin/go.exe' } else { 'bin/go' }) } |
        Where-Object { Test-Path -LiteralPath $_ } | Sort-Object -Descending | Select-Object -First 1)[0]
}
if (!$GoExecutable) { throw 'Go toolchain missing: set HERON_GO or put go on PATH.' }
$source = Join-Path $PSScriptRoot 'Source/AutomationTenants.Server'
$stagingRoot = Join-Path $outputDirectory '.server-staging'
$staging = Join-Path $stagingRoot ([guid]::NewGuid().ToString('N'))
$payload = Join-Path $staging 'payload'
[IO.Directory]::CreateDirectory($payload) | Out-Null
$binary = 'heron-automation-tenants' + $(if ($Runtime.StartsWith('windows-')) { '.exe' } else { '' })
$settings = @{}
foreach ($key in 'GOOS', 'GOARCH', 'CGO_ENABLED') { $settings[$key] = [Environment]::GetEnvironmentVariable($key) }
Push-Location $source
try {
    $env:GOOS = $Runtime.Split('-')[0]; $env:GOARCH = 'amd64'; $env:CGO_ENABLED = '0'
    & $GoExecutable build -trimpath -buildvcs=false -ldflags "-s -w -X main.version=$version -X main.commit=$commit" -o (Join-Path $payload $binary) ./cmd/heron-automation-tenants
    if ($LASTEXITCODE -ne 0) { throw 'AutomationTenants server build failed.' }
    $goVersion = (& $GoExecutable env GOVERSION | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify Go toolchain.' }
}
finally {
    Pop-Location
    foreach ($key in $settings.Keys) { [Environment]::SetEnvironmentVariable($key, $settings[$key]) }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination (Join-Path $payload 'LICENSE')
$files = @(foreach ($file in Get-ChildItem -LiteralPath $payload -File | Sort-Object Name) {
    [ordered]@{ path = $file.Name; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{ product = 'Heron.AutomationTenants.Server'; version = $version; source_commit = $commit;
    runtime = $Runtime; entrypoint = $binary; go_version = $goVersion; files = $files }
[IO.File]::WriteAllText((Join-Path $payload 'manifest.json'), (($manifest | ConvertTo-Json -Depth 5) -replace "`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
$archivePath = Join-Path $staging $name
$archive = [IO.File]::Create($archivePath)
$gzip = [IO.Compression.GZipStream]::new($archive, [IO.Compression.CompressionLevel]::Optimal, $true)
$tar = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Ustar, $true)
try {
    foreach ($file in Get-ChildItem -LiteralPath $payload -File | Sort-Object Name) {
        $entry = [System.Formats.Tar.UstarTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $file.Name)
        $entry.ModificationTime = [DateTimeOffset]::UnixEpoch
        $entry.Uid = 0; $entry.Gid = 0; $entry.UserName = ''; $entry.GroupName = ''
        $entry.Mode = [IO.UnixFileMode]$(if ($file.Name -eq $binary) { 493 } else { 420 })
        $entry.DataStream = [IO.File]::OpenRead($file.FullName)
        try { $tar.WriteEntry($entry) } finally { $entry.DataStream.Dispose() }
    }
}
finally { $tar.Dispose(); $gzip.Dispose(); $archive.Dispose() }
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$lock = [IO.File]::Open((Join-Path $outputDirectory '.heron-pack.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    $destination = Join-Path $outputDirectory $name
    $sha = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha) {
        throw "Server version already contains different bytes: $destination"
    }
    if (!(Test-Path -LiteralPath $destination)) { [IO.File]::Copy($archivePath, $destination) }
    [IO.File]::WriteAllText($destination + '.sha256', "$sha  $name`n", [Text.UTF8Encoding]::new($false))
}
finally { $lock.Dispose() }
$resolvedStaging = [IO.Path]::GetFullPath($staging)
if (![IO.Path]::GetDirectoryName($resolvedStaging).Equals([IO.Path]::GetFullPath($stagingRoot), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected staging path.' }
Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
Write-Host "Server ready: $destination"
Write-Host "SHA256: $sha"
