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

$installerScript = Get-Content -LiteralPath (Join-Path $root "installer\AudioOutputSwitcher.iss") -Raw
Assert-True ($installerScript.Contains('DefaultDirName={autopf}\Audio Output Switcher')) "installer does not target a protected application directory"
Assert-True ($installerScript.Contains('PrivilegesRequired=admin')) "installer does not establish a protected per-machine boundary"
Assert-True ($installerScript.Contains('CloseApplications=yes')) "installer does not coordinate a running application during upgrade"
Assert-True ($installerScript.Contains('RestartApplications=no')) "installer can unexpectedly restart the tray application during maintenance"
Assert-True ($installerScript.Contains('--startup-sync disable highest')) "uninstaller does not remove the application's owned startup registration"

$programSource = Get-Content -LiteralPath (Join-Path $root "src\Program.cs") -Raw
Assert-True (-not $programSource.Contains("Application.DoEvents()")) "product source still contains Application.DoEvents"
Assert-True (-not $programSource.Contains("throw firstError")) "audio fallback still discards the second provider error"
Assert-True ($programSource.Contains("RestoreDefaultRoleIds")) "audio role rollback is missing"
Assert-True ($programSource.Contains("FindRolesNotUsingDevice")) "audio role verification is missing"
Assert-True ($programSource.Contains("!settings.ShowBalloon && !isErrorOrWarning")) "warning and error notifications are still suppressed with routine notifications"
Assert-True ($programSource.Contains("ScheduledTaskNamePrefix + GetCurrentUserSid()")) "startup task names are not isolated per Windows user"
Assert-True ($programSource.Contains("IsOwnedScheduledTask")) "startup task ownership validation is missing"
Assert-True ($programSource.Contains("VerifyStartupState")) "startup updates are not verified after mutation"
Assert-True ($programSource.Contains("RestoreStartupState")) "startup update rollback is missing"
Assert-True (-not $programSource.Contains('RunSchtasks("/Delete /TN " + Quote(ScheduledTaskName)')) "startup task deletion still targets an unverified fixed task name"
Assert-True ($programSource.Contains("suspendObserved && elapsed >= WatchdogGapMilliseconds")) "power watchdog can still mistake an ordinary UI stall for resume"
Assert-True ($programSource.Contains("LLKHF_INJECTED") -and $programSource.Contains("LLMHF_INJECTED")) "injected keyboard or mouse input is not filtered"
Assert-True ($programSource.Contains("HotkeyActionKind.DeviceId")) "stable device-ID hotkey actions are missing"
Assert-True ($programSource.Contains("AudioOutputSwitcher.DeviceSwitch")) "audio switching is still executed directly on the UI thread"
Assert-True ($programSource.Contains("AudioOutputSwitcher.MenuRefresh")) "tray-device refresh is still executed directly on the UI thread"
Assert-True (-not $programSource.Contains('sb.AppendLine("Next=Ctrl+Alt+F12");')) "first-run configuration still installs an active personal hotkey"

$releaseConfig = Join-Path $root "packaging\AudioOutputSwitcher.default.ini"
$activeConfigLines = Get-Content -LiteralPath $releaseConfig | Where-Object {
    $line = $_.Trim()
    $line -and -not $line.StartsWith('#') -and -not $line.StartsWith(';')
}
Assert-True (-not ($activeConfigLines -contains 'RunAsAdministrator=true')) "release config enables administrator mode"
$activeHotkeys = @($activeConfigLines | Where-Object { $_ -match '^(Next|Previous|Device\d+|Name:.+|Id:.+)=' })
Assert-True ($activeHotkeys.Count -eq 0) "release config contains active hotkeys"
Assert-True ($activeConfigLines -contains 'AllowInjectedInput=false') "release config must reject injected input by default"

[IO.File]::WriteAllText(
    $configPath,
    "ShowBalloon=false`r`nRunAsAdministrator=false`r`nFutureSetting=keep-me`r`n",
    [Text.Encoding]::UTF8)

$assembly = [Reflection.Assembly]::LoadFrom($output)
$startupType = $assembly.GetType("AudioOutputSwitcher.StartupManager", $true)
$privateStatic = [Reflection.BindingFlags]"NonPublic,Static"
$buildTaskXmlMethod = $startupType.GetMethod("BuildScheduledTaskXml", $privateStatic)
$getTaskNameMethod = $startupType.GetMethod("GetScheduledTaskName", $privateStatic)
$isOwnedTaskMethod = $startupType.GetMethod("IsOwnedScheduledTask", $privateStatic)
$taskXml = [string]$buildTaskXmlMethod.Invoke($null, @())
$taskName = [string]$getTaskNameMethod.Invoke($null, @())
$currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
Assert-True ($taskName -eq ("AudioOutputSwitcher-" + $currentSid)) "scheduled task name is not scoped to the current SID"
Assert-True ($taskXml.Contains("ProductId=AudioOutputSwitcher")) "scheduled task XML has no product marker"
Assert-True ([bool]$isOwnedTaskMethod.Invoke($null, @($taskName, $taskXml))) "new scheduled task XML is not recognized as owned"
$legacyDescription = "Starts Audio Output Switcher when the user signs in."
$markedDescription = "Starts Audio Output Switcher when the user signs in. ProductId=AudioOutputSwitcher; OwnerSid=$currentSid."
$legacyTaskXml = $taskXml.Replace($markedDescription, $legacyDescription)
Assert-True ([bool]$isOwnedTaskMethod.Invoke($null, @("AudioOutputSwitcher", $legacyTaskXml))) "owned legacy startup task is not eligible for migration"
$foreignTaskXml = $taskXml.Replace($currentSid, "S-1-5-21-1-2-3-1001")
Assert-True (-not [bool]$isOwnedTaskMethod.Invoke($null, @($taskName, $foreignTaskXml))) "foreign-user startup task was accepted as owned"

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

$hotkeyActionType = $assembly.GetType("AudioOutputSwitcher.HotkeyAction", $true)
$tryParseAction = $hotkeyActionType.GetMethod("TryParse", [Reflection.BindingFlags]"Public,Static")
$deviceId = "{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}"
$deviceAction = [Activator]::CreateInstance($hotkeyActionType)
$deviceAction.Kind = [Enum]::Parse($assembly.GetType("AudioOutputSwitcher.HotkeyActionKind", $true), "DeviceId")
$deviceAction.DeviceId = $deviceId
$sourceName = [string]$hotkeyActionType.GetMethod("ToSourceName").Invoke($deviceAction, @())
[object[]]$parseArguments = @($sourceName, $null)
Assert-True ([bool]$tryParseAction.Invoke($null, $parseArguments)) "stable device-ID action could not be parsed"
Assert-True ($parseArguments[1].DeviceId -eq $deviceId) "stable device-ID action did not round-trip"

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
