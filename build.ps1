param([switch]$TestsOnly, [switch]$NoPublish, [string]$OutputDirectory = 'build')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Force -Path $out | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$refs = @('/r:System.dll','/r:System.Core.dll','/r:System.Web.Extensions.dll','/r:System.Net.Http.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll')
$core = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | Where-Object { $_.Name -notin @('Program.cs','PopupForm.cs','TrayContext.cs') } | ForEach-Object { $_.FullName })
& $csc /nologo /utf8output /warn:4 /warnaserror /optimize+ /target:exe "/out:$out\Tests.exe" @refs @core "$root\tests\Tests.cs" "$root\tests\ProviderTests.cs" "$root\tests\GrokWebTests.cs" "$root\tests\WidgetTests.cs" "$root\tests\AlertTests.cs" "$root\tests\SnapshotTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test compile failed' }
& "$out\Tests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
if (!$TestsOnly) {
    $icon = Join-Path $root 'assets\app.ico'
    if (!(Test-Path -LiteralPath $icon)) { throw 'Missing assets/app.ico. Run tools/generate-icon.ps1 first.' }
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
    & $csc /nologo /utf8output /warn:4 /warnaserror /optimize+ /target:winexe /platform:anycpu "/win32manifest:$root\app.manifest" "/win32icon:$icon" "/resource:$icon,AIUsageManager.App.ico" "/out:$out\AIUsageManager.exe" @refs @sources
    if ($LASTEXITCODE -ne 0) { throw 'Application compile failed' }
    Copy-Item -LiteralPath "$root\app.config" -Destination "$out\AIUsageManager.exe.config" -Force
    & $csc /nologo /utf8output /warn:4 /warnaserror /optimize+ /target:exe /main:UiTests "/win32manifest:$root\app.manifest" "/resource:$icon,AIUsageManager.App.ico" "/out:$out\UiTests.exe" @refs @sources "$root\tests\UiTests.cs"
    if ($LASTEXITCODE -ne 0) { throw 'UI test compile failed' }
    Copy-Item -LiteralPath "$root\app.config" -Destination "$out\UiTests.exe.config" -Force
    & "$out\UiTests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'UI tests failed' }
    & $csc /nologo /utf8output /warn:4 /warnaserror /optimize+ /target:exe "/out:$out\Diagnostics.exe" "/r:$out\AIUsageManager.exe" @refs "$root\tests\Diagnostics.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Diagnostics compile failed' }
    Copy-Item -LiteralPath "$root\app.config" -Destination "$out\Diagnostics.exe.config" -Force
    if (!$NoPublish) {
        Copy-Item -LiteralPath "$out\AIUsageManager.exe" -Destination "$root\AIUsageManager.exe" -Force
        Copy-Item -LiteralPath "$out\AIUsageManager.exe.config" -Destination "$root\AIUsageManager.exe.config" -Force
    }
    Write-Output "Built $out\AIUsageManager.exe"
}
