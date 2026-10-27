$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.build-tools'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet publish (Join-Path $projectRoot 'src\LiquidMusic\LiquidMusic.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $projectRoot 'native')
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать C# приложение' }
Write-Output (Join-Path $projectRoot 'native\LiquidMusic.exe')
