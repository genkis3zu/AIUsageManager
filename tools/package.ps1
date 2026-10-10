param([string]$Version = '1.0.0', [string]$BuildDirectory = 'build\release')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.patch' }
$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $projectRoot 'build.ps1') -NoPublish -OutputDirectory $BuildDirectory
$binaryDirectory = Join-Path $projectRoot $BuildDirectory
$binary = Join-Path $binaryDirectory 'AIUsageManager.exe'
$fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($binary).FileVersion
if ($fileVersion -ne "$Version.0") { throw 'Assembly file version and release version differ' }
$stage = Join-Path $projectRoot ('build\packages\' + [Guid]::NewGuid().ToString('N'))
$distribution = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Force -Path $stage,$distribution | Out-Null

# Explicit allowlist: no settings, provider files, fixtures, diagnostics or worktree archive.
$files = [ordered]@{
    'AIUsageManager.exe' = $binary
    'AIUsageManager.exe.config' = (Join-Path $binaryDirectory 'AIUsageManager.exe.config')
    'README.md' = (Join-Path $projectRoot 'docs\portable-readme.md')
    'LICENSE' = (Join-Path $projectRoot 'LICENSE')
    'PRIVACY.md' = (Join-Path $projectRoot 'docs\privacy.md')
}
foreach ($name in $files.Keys) { Copy-Item -LiteralPath $files[$name] -Destination (Join-Path $stage $name) }
$zipName = "AIUsageManager-$Version-windows.zip"
$zipPath = Join-Path $distribution $zipName
Compress-Archive -LiteralPath @($files.Keys | ForEach-Object { Join-Path $stage $_ }) -DestinationPath $zipPath -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $actual = @($archive.Entries | ForEach-Object { $_.FullName } | Sort-Object)
    $expected = @($files.Keys | Sort-Object)
    if (Compare-Object $actual $expected) { throw 'Unexpected release archive contents' }
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText((Join-Path $distribution 'SHA256SUMS.txt'), "$hash  $zipName`n", [System.Text.Encoding]::ASCII)
Write-Output "Packaged $zipName (5 allowlisted files)"
