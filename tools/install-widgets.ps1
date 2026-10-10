param([string]$PackageDirectory, [string]$InstallDirectory, [switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
if (!$PackageDirectory) {
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Package')) { $PackageDirectory = Join-Path $PSScriptRoot 'Package' }
    else { $PackageDirectory = Join-Path $PSScriptRoot '..\build\widgets' }
}
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Run this script with Windows PowerShell (powershell.exe), not pwsh.' }
if ([Environment]::OSVersion.Version.Build -lt 22000) { throw 'Windows 11 is required.' }
if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64') { throw 'This widget package currently supports x64 Windows only.' }
$developerMode = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue
if (!$developerMode -or $developerMode.AllowDevelopmentWithoutDevLicense -ne 1) { throw 'Enable Developer Mode in Windows Settings before registering this unsigned local widget. This script does not change that setting.' }
$runtime = @(Get-AppxPackage -Name Microsoft.WindowsAppRuntime.2 | Where-Object { [version]$_.Version -ge [version]'2.5.1.0' -and $_.Architecture -eq 'X64' })
if (!$runtime.Count) { throw 'Install the Microsoft Windows App Runtime 2.5.1 (x64) or later from Microsoft before registering widgets.' }
if (!(Get-AppxPackage -Name MicrosoftWindows.Client.WebExperience)) { throw 'Install or update the Windows Web Experience Pack from Microsoft Store.' }
$source = (Resolve-Path -LiteralPath $PackageDirectory).Path
$files = @('AppxManifest.xml','AIUsageWidgets.exe','AIUsageManager.exe','AIUsageManager.exe.config','Assets\App.png','Assets\Preview.png')
foreach ($file in $files) { if (!(Test-Path -LiteralPath (Join-Path $source $file) -PathType Leaf)) { throw "Missing widget package file: $file" } }
[xml]$manifest = Get-Content -LiteralPath (Join-Path $source 'AppxManifest.xml') -Raw -Encoding UTF8
if ($manifest.Package.Identity.Name -ne 'AIUsageManager.Widgets' -or $manifest.Package.Identity.Publisher -ne 'CN=AIUsageManager' -or $manifest.Package.Identity.ProcessorArchitecture -ne 'x64') { throw 'Unexpected widget package identity.' }
[version]$version = $manifest.Package.Identity.Version
if ($CheckOnly) { Write-Output 'Widget prerequisites and package identity verified; no settings changed.'; return }
if (!$InstallDirectory) { $InstallDirectory = Join-Path $env:LOCALAPPDATA ('AIUsageManager\Widgets\' + $version) }
$destination = [IO.Path]::GetFullPath($InstallDirectory)
if ($destination -eq $source) { throw 'Install directory must differ from the build/package source.' }
$existing = Get-AppxPackage -Name AIUsageManager.Widgets
if ($existing -and $existing.Publisher -ne 'CN=AIUsageManager') { throw 'Unexpected installed package publisher.' }
if ($existing -and [version]$existing.Version -gt $version) { throw 'A newer widget version is already installed.' }
if ($existing -and [version]$existing.Version -eq $version -and $existing.InstallLocation -ne $destination) { throw 'This version is registered at another location. Uninstall its widget registration before moving it.' }
if (Test-Path -LiteralPath $destination) {
    $oldManifest = Join-Path $destination 'AppxManifest.xml'
    if (Test-Path -LiteralPath $oldManifest) {
        [xml]$old = Get-Content -LiteralPath $oldManifest -Raw -Encoding UTF8
        if ($old.Package.Identity.Name -ne 'AIUsageManager.Widgets' -or $old.Package.Identity.Publisher -ne 'CN=AIUsageManager') { throw 'Install directory belongs to another package.' }
    } elseif (@(Get-ChildItem -LiteralPath $destination -Force).Count) { throw 'Install directory must be empty or contain this widget package.' }
}
# Stop only executables from the exact current-user widget installation being replaced.
if ($existing) {
    $owned = @('AIUsageWidgets.exe','AIUsageManager.exe') | ForEach-Object { Join-Path $existing.InstallLocation $_ }
    Get-Process -Name AIUsageWidgets,AIUsageManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -in $owned } | ForEach-Object { Stop-Process -Id $_.Id -ErrorAction Stop; [void]$_.WaitForExit(5000) }
}
New-Item -ItemType Directory -Force -Path $destination,(Join-Path $destination 'Assets'),(Join-Path $destination 'Public') | Out-Null
foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $source $file) -Destination (Join-Path $destination $file) -Force }
Add-AppxPackage -Path (Join-Path $destination 'AppxManifest.xml') -Register -ForceApplicationShutdown
$installed = Get-AppxPackage -Name AIUsageManager.Widgets
if (!$installed -or $installed.InstallLocation -ne $destination) { throw 'Widget registration did not complete at the expected location.' }
Write-Output 'Registered AI Usage Manager. Press Win+W, choose Add widgets (+), and pin AI Usage Manager. Choose Large for full details.'
