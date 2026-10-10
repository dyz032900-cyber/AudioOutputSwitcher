param(
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if (-not (Test-Path $csc)) {
    throw "Could not find the .NET Framework C# compiler."
}

$source = Join-Path $root "src\Program.cs"
$output = if ([string]::IsNullOrWhiteSpace($OutputPath)) { Join-Path $root "AudioOutputSwitcher.exe" } else { $OutputPath }
$icon = Join-Path $root "assets\speaker.ico"
$manifest = Join-Path $root "app.manifest"
$assemblyInfo = Join-Path $root "obj\GeneratedAssemblyInfo.cs"
$buildIdentity = Join-Path $root "obj\build-identity.json"
$generateAssemblyInfo = Join-Path $root "tools\GenerateAssemblyInfo.ps1"
$iconGeneratorSource = Join-Path $root "tools\GenerateSpeakerIcon.cs"
$iconGenerator = Join-Path $root "tools\GenerateSpeakerIcon.exe"

$outputDirectory = Split-Path -Parent $output
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$shouldGenerateIcon = -not (Test-Path $icon)

if ($shouldGenerateIcon) {
    & $csc `
        /nologo `
        /target:exe `
        /platform:anycpu `
        /out:$iconGenerator `
        /reference:System.Drawing.dll `
        $iconGeneratorSource

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    & $iconGenerator $icon --variant orbit
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

& $generateAssemblyInfo -Root $root -OutputPath $assemblyInfo -IdentityPath $buildIdentity

& $csc `
    /nologo `
    /target:winexe `
    /platform:anycpu `
    /optimize+ `
    /warn:4 `
    /out:$output `
    /win32icon:$icon `
    /win32manifest:$manifest `
    /reference:System.Windows.Forms.dll `
    /reference:System.Drawing.dll `
    $source `
    $assemblyInfo

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
$identity = Get-Content -LiteralPath $buildIdentity -Raw | ConvertFrom-Json
$sidecar = [ordered]@{
    product = "Audio Output Switcher"
    version = $identity.version
    commit = $identity.commit
    dirty = [bool]$identity.dirty
    sha256 = $hash
    file = [IO.Path]::GetFileName($output)
}
$sidecarPath = $output + ".build.json"
$hashPath = $output + ".sha256"
[IO.File]::WriteAllText($sidecarPath, ($sidecar | ConvertTo-Json) + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText($hashPath, $hash + "  " + [IO.Path]::GetFileName($output) + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))

Write-Host "Built $output"
Write-Host "Version $($identity.informationalVersion)"
Write-Host "SHA256 $hash"
