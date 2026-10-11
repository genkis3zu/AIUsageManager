$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Run this script with Windows PowerShell (powershell.exe), not pwsh.' }
$packages = @(Get-AppxPackage -Name AIUsageManager.Widgets)
foreach ($package in $packages) {
    if ($package.Name -ne 'AIUsageManager.Widgets' -or $package.Publisher -ne 'CN=AIUsageManager') { throw 'Unexpected widget package identity.' }
    Remove-AppxPackage -Package $package.PackageFullName
}
Write-Output 'AI Usage Manager widget registration removed for the current user. The portable tray app and local preferences were retained.'
