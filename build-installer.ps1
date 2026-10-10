param(
    [string]$IsccPath,
    [switch]$AllowDirty
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$version = (Get-Content -LiteralPath (Join-Path $root "VERSION") -Raw).Trim()
$stage = Join-Path $root "artifacts\installer-stage"
$output = Join-Path $root "artifacts\installer-output"
$stageExe = Join-Path $stage "AudioOutputSwitcher.exe"
$script = Join-Path $root "installer\AudioOutputSwitcher.iss"

if (-not $AllowDirty) {
    $dirty = & git -C $root status --porcelain 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Git status could not be read. Use -AllowDirty only for an explicitly non-release installer."
    }
    if ($dirty) {
        throw "Refusing to build an installer from a dirty working tree. Commit the reviewed source or use -AllowDirty for a non-release test installer."
    }
}

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $candidates = @()
    if (${env:ProgramFiles(x86)}) {
        $candidates += Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
    }
    if ($env:ProgramFiles) {
        $candidates += Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"
    }
    $candidates = @($candidates | Where-Object { Test-Path -LiteralPath $_ })
    if ($candidates.Count -gt 0) {
        $IsccPath = $candidates[0]
    }
}

if ([string]::IsNullOrWhiteSpace($IsccPath) -or -not (Test-Path -LiteralPath $IsccPath)) {
    throw "Inno Setup 6 compiler was not found. Install Inno Setup 6 or pass -IsccPath."
}

New-Item -ItemType Directory -Force -Path $stage, $output | Out-Null
& (Join-Path $root "build.ps1") -OutputPath $stageExe
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $IsccPath ("/DAppVersion=" + $version) $script
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$installer = Join-Path $output ("AudioOutputSwitcher-Setup-{0}.exe" -f $version)
if (-not (Test-Path -LiteralPath $installer)) {
    throw "Inno Setup completed without producing the expected installer: $installer"
}

$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    $installer + ".sha256",
    $hash + "  " + [IO.Path]::GetFileName($installer) + [Environment]::NewLine,
    (New-Object Text.UTF8Encoding($false)))

Write-Host "Installer: $installer"
Write-Host "SHA256:   $hash"
