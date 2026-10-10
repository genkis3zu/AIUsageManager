param([switch]$SkipCoreBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $projectRoot 'build\widgets'
$sdkCache = Join-Path $projectRoot 'build\widget-sdk'
New-Item -ItemType Directory -Force -Path $output,$sdkCache | Out-Null
if (!$SkipCoreBuild) { & (Join-Path $projectRoot 'build.ps1') -NoPublish -OutputDirectory 'build\widget-core' }
Copy-Item -LiteralPath "$projectRoot\build\widget-core\AIUsageManager.exe","$projectRoot\build\widget-core\AIUsageManager.exe.config" -Destination $output -Force
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$visualStudio) { throw 'Install Visual Studio C++ build tools and the Windows SDK to build widgets.' }
$vc = (Get-ChildItem -LiteralPath (Join-Path $visualStudio 'VC\Tools\MSVC') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).FullName
$kitsRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$sdkVersion = (Get-ChildItem -LiteralPath (Join-Path $kitsRoot 'bin') -Directory | Where-Object { $_.Name -match '^10\.0\.' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'x64\cppwinrt.exe')) } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).Name
if (!$sdkVersion) { throw 'A Windows SDK with cppwinrt.exe is required.' }
$archive = Join-Path $sdkCache 'widgets-2.0.5.zip'
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -UseBasicParsing -Uri 'https://api.nuget.org/v3-flatcontainer/microsoft.windowsappsdk.widgets/2.0.5/microsoft.windowsappsdk.widgets.2.0.5.nupkg' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '7F1B585EF8F012784D2CB91B86E809814CECCAB74183A5979EF45DB141779CB5') { throw 'Widgets SDK checksum mismatch.' }
$sdkPackage = Join-Path $sdkCache 'widgets-2.0.5'
Expand-Archive -LiteralPath $archive -DestinationPath $sdkPackage -Force
$generated = Join-Path $sdkCache 'generated'
& (Join-Path $kitsRoot "bin\$sdkVersion\x64\cppwinrt.exe") -input "$sdkPackage\metadata\Microsoft.Windows.Widgets.winmd" -reference "$kitsRoot\UnionMetadata\$sdkVersion\Windows.winmd" -output $generated
if ($LASTEXITCODE -ne 0) { throw 'C++/WinRT header generation failed.' }
$originalInclude=$env:INCLUDE; $originalLib=$env:LIB
try {
    $env:INCLUDE="$vc\include;$kitsRoot\Include\$sdkVersion\ucrt;$kitsRoot\Include\$sdkVersion\shared;$kitsRoot\Include\$sdkVersion\um;$kitsRoot\Include\$sdkVersion\winrt;$kitsRoot\Include\$sdkVersion\cppwinrt;$generated"
    $env:LIB="$vc\lib\x64;$kitsRoot\Lib\$sdkVersion\ucrt\x64;$kitsRoot\Lib\$sdkVersion\um\x64"
    $compiler=Join-Path $vc 'bin\Hostx64\x64\cl.exe'
    & $compiler /nologo /EHsc /std:c++20 /MT /W4 /WX /utf-8 /O2 "/Fe:$output\NativeTests.exe" "/Fo:$output\NativeTests.obj" "$projectRoot\widgets\NativeTests.cpp" /link windowsapp.lib
    if ($LASTEXITCODE -ne 0) { throw 'Widget native tests compile failed.' }
    & "$output\NativeTests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Widget native tests failed.' }
    & $compiler /nologo /EHsc /std:c++20 /MT /W4 /WX /utf-8 /O2 /DNDEBUG /DWINRT_NO_SOURCE_LOCATION "/Fe:$output\AIUsageWidgets.exe" "/Fo:$output\Provider.obj" "$projectRoot\widgets\Provider.cpp" /link /SUBSYSTEM:WINDOWS windowsapp.lib ole32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Widget provider compile failed.' }
} finally { $env:INCLUDE=$originalInclude; $env:LIB=$originalLib }
Copy-Item -LiteralPath "$projectRoot\build\widget-core\AIUsageManager.exe","$projectRoot\build\widget-core\AIUsageManager.exe.config" -Destination $output -Force
Copy-Item -LiteralPath "$projectRoot\widgets\AppxManifest.xml" -Destination $output -Force
New-Item -ItemType Directory -Force -Path "$output\Assets","$output\Public" | Out-Null
Copy-Item -LiteralPath "$projectRoot\assets\app.png" -Destination "$output\Assets\App.png" -Force
Copy-Item -LiteralPath "$projectRoot\widgets\Assets\Preview.png" -Destination "$output\Assets\Preview.png" -Force
Write-Output "Built native Windows widget provider: $output"
