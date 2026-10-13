param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$SkipBuild) {
    & node (Join-Path $PSScriptRoot 'build-obsidian.cjs')
    if ($LASTEXITCODE -ne 0) { throw 'Obsidian build failed.' }
}
$source = Join-Path $projectRoot 'build\obsidian\ai-usage-manager'
$manifest = Get-Content -LiteralPath (Join-Path $source 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.id -ne 'ai-usage-manager' -or $manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Unexpected plugin identity or version.' }
$stage = Join-Path $projectRoot ('build\obsidian-packages\' + [Guid]::NewGuid().ToString('N'))
$distribution = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Force -Path $stage,(Join-Path $stage 'ai-usage-manager'),(Join-Path $stage 'images'),$distribution | Out-Null
# Explicit runtime/documentation allowlist. No vault, settings, usage or credentials.
$files = [ordered]@{}
foreach ($file in @('main.js','styles.css','manifest.json')) { $files['ai-usage-manager\' + $file] = Join-Path $source $file }
$files['install-obsidian.ps1'] = Join-Path $PSScriptRoot 'install-obsidian.ps1'
$files['README.md'] = Join-Path $projectRoot 'docs\obsidian-guide.md'
$files['LICENSE'] = Join-Path $projectRoot 'LICENSE'
$files['PRIVACY.md'] = Join-Path $projectRoot 'docs\privacy.md'
foreach ($image in @('obsidian-standard.png','obsidian-compact.png','obsidian-forecast.png')) { $files['images\' + $image] = Join-Path $projectRoot ('docs\images\' + $image) }
foreach ($name in $files.Keys) { Copy-Item -LiteralPath $files[$name] -Destination (Join-Path $stage $name) }
$zipName = 'AIUsageManager-' + $manifest.version + '-obsidian.zip'
$zipPath = Join-Path $distribution $zipName
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $actual = @($archive.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName.Replace('\','/') } | Sort-Object)
    $expected = @($files.Keys | ForEach-Object { $_.Replace('\','/') } | Sort-Object)
    if (Compare-Object $actual $expected) { throw 'Unexpected Obsidian archive contents.' }
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $distribution ($zipName + '.sha256')), "$hash  $zipName`n", [Text.Encoding]::ASCII)
Write-Output "Packaged $zipName ($($files.Count) allowlisted files)"
