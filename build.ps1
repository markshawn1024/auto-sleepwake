param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Set-Location $PSScriptRoot
$sdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) {
    New-Item -ItemType Directory -Force .tools | Out-Null
    Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile .tools/dotnet-install.ps1
    & .tools/dotnet-install.ps1 -Version 10.0.403 -InstallDir (Join-Path $PSScriptRoot '.tools\dotnet') -NoPath
    if ($LASTEXITCODE -ne 0) { throw 'SDK installation failed.' }
}
& $sdk build WakeGlass.sln -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if (!$SkipTests) { & $sdk run --project tests/WakeGlass.Tests -c Release; if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' } }
$releaseDir = 'artifacts/WakeGlass-1.1.0-win-x64'
& $sdk publish src/WakeGlass.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $releaseDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed; exit WakeGlass before rebuilding.' }
Copy-Item tools/Install.cmd,tools/Install.ps1,tools/Uninstall.cmd,tools/Uninstall.ps1,README.md,TEST-REPORT.md -Destination $releaseDir -Force
if (Test-Path TestResults/results.txt) { Copy-Item TestResults/results.txt (Join-Path $releaseDir 'test-results.txt') -Force }
Compress-Archive -Path $releaseDir -DestinationPath "$releaseDir.zip" -Force
Get-FileHash "$releaseDir.zip" -Algorithm SHA256 | Format-List
