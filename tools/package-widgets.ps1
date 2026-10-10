param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'build-widgets.ps1') }
$source = Join-Path $projectRoot 'build\widgets'
. (Join-Path $PSScriptRoot 'assert-release-clean.ps1')
Assert-ReleaseBinaryClean (Join-Path $source 'AIUsageWidgets.exe') $projectRoot
Assert-ReleaseBinaryClean (Join-Path $source 'AIUsageManager.exe') $projectRoot
[xml]$manifest = Get-Content -LiteralPath (Join-Path $source 'AppxManifest.xml') -Raw -Encoding UTF8
$version = [version]$manifest.Package.Identity.Version
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $source 'AIUsageManager.exe')).FileVersion -ne $version.ToString()) { throw 'Widget manifest and app versions differ.' }
$stage = Join-Path $projectRoot ('build\widget-packages\' + [Guid]::NewGuid().ToString('N'))
$distribution = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Force -Path $stage,(Join-Path $stage 'Package\Assets'),(Join-Path $stage 'Package\Public'),$distribution | Out-Null
$files = [ordered]@{}
foreach ($file in @('AppxManifest.xml','AIUsageWidgets.exe','AIUsageManager.exe','AIUsageManager.exe.config','Assets\App.png','Assets\Preview.png')) { $files['Package\' + $file] = Join-Path $source $file }
$files['install-widgets.ps1'] = Join-Path $PSScriptRoot 'install-widgets.ps1'
$files['uninstall-widgets.ps1'] = Join-Path $PSScriptRoot 'uninstall-widgets.ps1'
$files['README.md'] = Join-Path $projectRoot 'docs\widgets-guide.md'
$files['LICENSE'] = Join-Path $projectRoot 'LICENSE'
$files['THIRD-PARTY-NOTICES.md'] = Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md'
$files['privacy.md'] = Join-Path $projectRoot 'docs\privacy.md'
foreach ($name in $files.Keys) { Copy-Item -LiteralPath $files[$name] -Destination (Join-Path $stage $name) }
$zipName = 'AIUsageManager-{0}.{1}.{2}-widgets-x64.zip' -f $version.Major,$version.Minor,$version.Build
$zipPath = Join-Path $distribution $zipName
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $actual = @($archive.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName.Replace('\','/') } | Sort-Object)
    $expected = @($files.Keys | ForEach-Object { $_.Replace('\','/') } | Sort-Object)
    if (Compare-Object $actual $expected) { throw 'Unexpected widget archive contents.' }
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $distribution ($zipName + '.sha256')), "$hash  $zipName`n", [Text.Encoding]::ASCII)
Write-Output "Packaged $zipName ($($files.Count) allowlisted files)"
