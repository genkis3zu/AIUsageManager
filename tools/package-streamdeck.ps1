param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pluginFolder = 'io.github.genkis3zu.aiusagemanager.sdPlugin'
if (!$SkipBuild) {
    & node (Join-Path $PSScriptRoot 'build-streamdeck.cjs')
    if ($LASTEXITCODE -ne 0) { throw 'Stream Deck build failed.' }
}
$source = Join-Path $projectRoot ('build\streamdeck\' + $pluginFolder)
$manifest = Get-Content -LiteralPath (Join-Path $source 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.UUID -ne 'io.github.genkis3zu.aiusagemanager' -or $manifest.Version -notmatch '^(\d+\.\d+\.\d+)\.\d+$') { throw 'Unexpected plugin identity or version.' }
$version = $Matches[1]
# Explicit runtime allowlist. No usage data, settings, logs or credentials.
$allowed = @('manifest.json','bin/plugin.cjs','ui/settings.html','ui/settings.js','imgs/plugin.svg','imgs/category.svg','imgs/action.svg','imgs/key.svg','LICENSE')
$actual = @(Get-ChildItem -LiteralPath $source -Recurse -File | ForEach-Object { $_.FullName.Substring($source.Length + 1).Replace('\','/') } | Sort-Object)
$expected = @($allowed | Sort-Object)
if (Compare-Object $actual $expected) { throw ('Build folder does not match the allowlist: ' + ($actual -join ', ')) }
$distribution = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Force -Path $distribution | Out-Null
$packagePath = Join-Path $distribution ('AIUsageManager-' + $version + '-streamdeck.streamDeckPlugin')
if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# Entries are written one by one so every name uses '/' (Compress-Archive in PowerShell 5.1 writes '\').
$archive = [System.IO.Compression.ZipFile]::Open($packagePath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $allowed) {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $source ($name.Replace('/','\'))), ($pluginFolder + '/' + $name), [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }
$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName } | Sort-Object)
    $wanted = @($allowed | ForEach-Object { $pluginFolder + '/' + $_ } | Sort-Object)
    if (Compare-Object $entries $wanted) { throw 'Package contents do not match the allowlist.' }
} finally { $archive.Dispose() }
Write-Output "Packaged $packagePath"
