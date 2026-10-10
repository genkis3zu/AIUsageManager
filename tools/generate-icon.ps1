param([string]$OutputDirectory = 'assets')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolOutput = Join-Path $projectRoot 'build\tools'
New-Item -ItemType Directory -Force -Path $toolOutput | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
& $compiler /nologo /warn:4 /warnaserror /optimize+ /r:System.Drawing.dll /r:System.Xml.Linq.dll "/out:$toolOutput\GenerateIcon.exe" "$PSScriptRoot\GenerateIcon.cs"
if ($LASTEXITCODE -ne 0) { throw 'Icon generator compile failed' }
& "$toolOutput\GenerateIcon.exe" "$projectRoot\assets\app.svg" (Join-Path $projectRoot $OutputDirectory)
if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed' }
