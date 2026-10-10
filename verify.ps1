param(
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$verificationRoot = Join-Path $root "artifacts\verification"
$output = Join-Path $verificationRoot "AudioOutputSwitcher.exe"
$configPath = Join-Path $verificationRoot "config-test.ini"
$version = (Get-Content -LiteralPath (Join-Path $root "VERSION") -Raw).Trim()

New-Item -ItemType Directory -Force -Path $verificationRoot | Out-Null

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw "Verification failed: $Message"
    }
}

if (-not $SkipBuild) {
    & (Join-Path $root "build.ps1") -OutputPath $output
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

Assert-True (Test-Path -LiteralPath $output) "verification executable was not produced"
Assert-True (Test-Path -LiteralPath ($output + ".sha256")) "SHA-256 sidecar is missing"
Assert-True (Test-Path -LiteralPath ($output + ".build.json")) "build identity sidecar is missing"

$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($output)
Assert-True ($fileVersion.FileVersion -eq ($version + ".0")) "file version does not match VERSION"

$identity = Get-Content -LiteralPath ($output + ".build.json") -Raw | ConvertFrom-Json
$actualHash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-True ($identity.version -eq $version) "build identity version does not match VERSION"
Assert-True (-not [string]::IsNullOrWhiteSpace([string]$identity.commit)) "build identity has no commit"
Assert-True ($identity.sha256 -eq $actualHash) "build identity hash does not match the executable"

$manifest = Get-Content -LiteralPath (Join-Path $root "app.manifest") -Raw
Assert-True ($manifest.Contains('requestedExecutionLevel level="asInvoker"')) "manifest must use asInvoker"

$programSource = Get-Content -LiteralPath (Join-Path $root "src\Program.cs") -Raw
Assert-True (-not $programSource.Contains("Application.DoEvents()")) "product source still contains Application.DoEvents"
Assert-True (-not $programSource.Contains("throw firstError")) "audio fallback still discards the second provider error"
Assert-True ($programSource.Contains("RestoreDefaultRoleIds")) "audio role rollback is missing"
Assert-True ($programSource.Contains("FindRolesNotUsingDevice")) "audio role verification is missing"
Assert-True ($programSource.Contains("!settings.ShowBalloon && !isErrorOrWarning")) "warning and error notifications are still suppressed with routine notifications"

$releaseConfig = Join-Path $root "packaging\AudioOutputSwitcher.default.ini"
$activeConfigLines = Get-Content -LiteralPath $releaseConfig | Where-Object {
    $line = $_.Trim()
    $line -and -not $line.StartsWith('#') -and -not $line.StartsWith(';')
}
Assert-True (-not ($activeConfigLines -contains 'RunAsAdministrator=true')) "release config enables administrator mode"
$activeHotkeys = @($activeConfigLines | Where-Object { $_ -match '^(Next|Previous|Device\d+|Name:.+)=' })
Assert-True ($activeHotkeys.Count -eq 0) "release config contains active hotkeys"

[IO.File]::WriteAllText(
    $configPath,
    "ShowBalloon=false`r`nRunAsAdministrator=false`r`nFutureSetting=keep-me`r`n",
    [Text.Encoding]::UTF8)

$assembly = [Reflection.Assembly]::LoadFrom($output)
$settingsType = $assembly.GetType("AudioOutputSwitcher.AppSettings", $true)
$loadMethod = $settingsType.GetMethod("Load", [Reflection.BindingFlags]"Public,Static")
$saveMethod = $settingsType.GetMethod("Save", [Reflection.BindingFlags]"Public,Instance")
[object[]]$configArguments = @([string]$configPath)
$settings = $loadMethod.Invoke($null, $configArguments)
$saveMethod.Invoke($settings, $configArguments)
$settings.ShowBalloon = $true
$saveMethod.Invoke($settings, $configArguments)

$savedConfig = [IO.File]::ReadAllText($configPath)
$backupConfig = [IO.File]::ReadAllText($configPath + ".bak")
Assert-True ($savedConfig.Contains("FutureSetting=keep-me")) "unknown configuration setting was lost"
Assert-True ($backupConfig.Contains("ShowBalloon=false")) "configuration backup does not contain the prior value"

$lockedConfig = [IO.File]::Open($configPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $fallbackSettings = $loadMethod.Invoke($null, $configArguments)
    Assert-True (-not $fallbackSettings.ShowBalloon) "backup configuration was not loaded while the primary file was unavailable"
}
finally {
    $lockedConfig.Dispose()
}

$temporaryWrites = @([IO.Directory]::GetFiles($verificationRoot, "*.tmp"))
Assert-True ($temporaryWrites.Count -eq 0) "temporary configuration writes remain"

Write-Host "Verification passed"
Write-Host "Version: $($identity.version)"
Write-Host "Commit:  $($identity.commit)"
Write-Host "Dirty:   $($identity.dirty)"
Write-Host "SHA256:  $actualHash"
