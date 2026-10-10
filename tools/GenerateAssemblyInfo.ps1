param(
    [Parameter(Mandatory = $true)]
    [string]$Root,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [Parameter(Mandatory = $true)]
    [string]$IdentityPath
)

$ErrorActionPreference = "Stop"

$versionFile = Join-Path $Root "VERSION"
if (-not (Test-Path -LiteralPath $versionFile)) {
    throw "Missing version source: $versionFile"
}

$version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION must use major.minor.patch format."
}

$commit = "unknown"
$dirty = $true
try {
    $resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
    $commitValue = (& git -C $resolvedRoot rev-parse --short=12 HEAD 2>$null).Trim()
    if ($LASTEXITCODE -eq 0 -and $commitValue) {
        $commit = $commitValue
        $status = & git -C $resolvedRoot status --porcelain --untracked-files=normal 2>$null
        $dirty = $LASTEXITCODE -ne 0 -or [bool]$status
    }
}
catch {
    $commit = "unknown"
    $dirty = $true
}

$informationalVersion = $version + "+" + $commit
if ($dirty) {
    $informationalVersion += ".dirty"
}
$assemblyVersion = $version + ".0"

$outputDirectory = Split-Path -Parent $OutputPath
$identityDirectory = Split-Path -Parent $IdentityPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $identityDirectory | Out-Null

$source = @"
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Audio Output Switcher")]
[assembly: AssemblyDescription("Fast Windows audio output switching from the tray and global hotkeys.")]
[assembly: AssemblyCompany("DyzGit")]
[assembly: AssemblyProduct("Audio Output Switcher")]
[assembly: AssemblyCopyright("Copyright (c) 2026")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
[assembly: AssemblyInformationalVersion("$informationalVersion")]
"@

$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($OutputPath, $source, $utf8)
$identity = [ordered]@{
    version = $version
    assemblyVersion = $assemblyVersion
    commit = $commit
    dirty = [bool]$dirty
    informationalVersion = $informationalVersion
}
[IO.File]::WriteAllText($IdentityPath, ($identity | ConvertTo-Json) + [Environment]::NewLine, $utf8)
