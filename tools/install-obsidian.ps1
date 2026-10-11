param([Parameter(Mandatory=$true)][string]$VaultPath,[Parameter(Mandatory=$true)][string]$AppPath,[string]$PluginDirectory)
$ErrorActionPreference = 'Stop'
$vaultRoot = (Resolve-Path -LiteralPath $VaultPath).Path
$appFile = (Resolve-Path -LiteralPath $AppPath).Path
if ($appFile -notmatch '^[A-Za-z]:\\') { throw 'Select a local, drive-qualified app path.' }
if ([IO.Path]::GetFileName($appFile) -ine 'AIUsageManager.exe') { throw 'Select AIUsageManager.exe.' }
$configRoot = Join-Path $vaultRoot '.obsidian'
if (!(Test-Path -LiteralPath $configRoot -PathType Container)) { throw 'Choose an existing Obsidian vault with a .obsidian directory.' }
if (!$PluginDirectory) {
    $PluginDirectory = Join-Path $PSScriptRoot 'ai-usage-manager'
    if (!(Test-Path -LiteralPath $PluginDirectory)) { $PluginDirectory = Join-Path $PSScriptRoot '..\build\obsidian\ai-usage-manager' }
}
$source = (Resolve-Path -LiteralPath $PluginDirectory).Path
$files = @('main.js','styles.css','manifest.json')
foreach ($name in $files) { if (!(Test-Path -LiteralPath (Join-Path $source $name) -PathType Leaf)) { throw "Missing plugin file: $name" } }
$manifest = Get-Content -LiteralPath (Join-Path $source 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.id -ne 'ai-usage-manager') { throw 'Unexpected plugin identity.' }
$destination = Join-Path $configRoot 'plugins\ai-usage-manager'
if (Test-Path -LiteralPath $destination) {
    if ((Get-Item -LiteralPath $destination).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Plugin directory must not redirect to another location.' }
    $existing = Get-Content -LiteralPath (Join-Path $destination 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($existing.id -ne 'ai-usage-manager') { throw 'Destination belongs to another plugin.' }
}
New-Item -ItemType Directory -Force -Path $destination | Out-Null
foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $destination $name) -Force }
$settingsPath = Join-Path $destination 'data.json'
$settings = if(Test-Path -LiteralPath $settingsPath) { Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { [PSCustomObject]@{mode='standard'} }
$settings | Add-Member -MemberType NoteProperty -Name appPath -Value $appFile -Force
[IO.File]::WriteAllText($settingsPath,($settings | ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
Write-Output 'Installed AI Usage Manager plugin. Enable it in Obsidian Community plugins, or reload it if already enabled. Notes and templates were not changed.'
