param(
    [string]$Label = "latest"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dateTag = Get-Date -Format "yyyyMMdd"
$timeTag = Get-Date -Format "yyyyMMdd-HHmm"
$safeLabel = if ([string]::IsNullOrWhiteSpace($Label)) { "latest" } else { $Label.Trim() }

$packageRoot = Join-Path $root ("package-{0}-{1}" -f $dateTag, $safeLabel)
$stage = Join-Path $packageRoot ("AudioOutputSwitcher-stage-{0}" -f $timeTag)
$stageExe = Join-Path $stage "AudioOutputSwitcher.exe"
$stageIni = Join-Path $stage "AudioOutputSwitcher.ini"
$stageReadme = Join-Path $stage "README.md"
$zipPath = Join-Path $packageRoot "AudioOutputSwitcher-package.zip"
$defaultConfig = Join-Path $root "packaging\AudioOutputSwitcher.default.ini"

if (-not (Test-Path -LiteralPath $defaultConfig)) {
    throw "Missing neutral release config: $defaultConfig"
}

$activeConfigLines = Get-Content -LiteralPath $defaultConfig | Where-Object {
    $line = $_.Trim()
    $line -and -not $line.StartsWith('#') -and -not $line.StartsWith(';')
}

if ($activeConfigLines -contains 'RunAsAdministrator=true') {
    throw "The release config must not enable administrator mode by default."
}

$activeHotkeys = $activeConfigLines | Where-Object {
    $_ -match '^(Next|Previous|Device\d+|Name:.+)='
}
if ($activeHotkeys) {
    throw "The neutral release config must not contain active personal hotkeys."
}

New-Item -ItemType Directory -Force -Path $stage | Out-Null

& (Join-Path $root "build.ps1") -OutputPath $stageExe
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Copy-Item -LiteralPath $defaultConfig -Destination $stageIni -Force
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination $stageReadme -Force

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Stage: $stage"
Write-Host "Zip:   $zipPath"
