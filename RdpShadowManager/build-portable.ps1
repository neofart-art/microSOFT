[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'))
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$publish = Join-Path $OutputDirectory 'RdpShadowManager-win-x64'
# Refuse unsafe cleanup paths. Only the dedicated portable output is replaced.
if ($OutputDirectory.StartsWith((Join-Path $PSScriptRoot 'App'), [StringComparison]::OrdinalIgnoreCase) -or
    $OutputDirectory.StartsWith((Join-Path $PSScriptRoot 'Core'), [StringComparison]::OrdinalIgnoreCase) -or
    $OutputDirectory.StartsWith((Join-Path $PSScriptRoot 'Tests'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Choose a separate output directory.' }
dotnet run --project "$PSScriptRoot/Tests/Tests.csproj" --configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
& "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoLogo -NoProfile -NonInteractive -File "$PSScriptRoot/Tests/Maintenance.Tests.ps1"
if ($LASTEXITCODE -ne 0) { throw 'Maintenance tests failed' }
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish "$PSScriptRoot/App/RdpShadowManager.csproj" --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false --output $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item "$PSScriptRoot/README.md" $publish
Copy-Item "$PSScriptRoot/MANUAL-TESTS.md" $publish
$archive = Join-Path $OutputDirectory 'RdpShadowManager-win-x64.zip'
Compress-Archive -Path "$publish/*" -DestinationPath $archive -Force
Get-FileHash $archive -Algorithm SHA256
