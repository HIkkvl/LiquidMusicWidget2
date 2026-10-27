param([switch]$Launch)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.build-tools'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install .NET 10 SDK (Windows x64): https://dotnet.microsoft.com/download/dotnet/10.0 . Then run DEV_RUN.bat again.'
}
$installedSdks = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($installedSdks | Where-Object { $_ -match '^(1[0-9]|[2-9][0-9])\.' })) {
    throw '.NET 10 SDK or newer is required (the Runtime alone is not enough): https://dotnet.microsoft.com/download/dotnet/10.0'
}

$modelRoot = Join-Path $projectRoot 'models'
$modelFolder = Join-Path $modelRoot 'vosk-model-small-ru-0.22'
$requiredModelFiles = @('am\final.mdl', 'conf\model.conf', 'graph\Gr.fst', 'graph\HCLr.fst')
$missingModelFiles = @($requiredModelFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $modelFolder $_) -PathType Leaf) })
if ($missingModelFiles.Count -gt 0) {
    Write-Host 'Downloading the Russian Vosk model (first build only)...'
    New-Item -ItemType Directory -Path $env:DOTNET_CLI_HOME -Force | Out-Null
    $modelArchive = Join-Path $env:DOTNET_CLI_HOME ('vosk-model-' + [Guid]::NewGuid().ToString('N') + '.zip')
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri 'https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip' -OutFile $modelArchive
        New-Item -ItemType Directory -Path $modelRoot -Force | Out-Null
        Expand-Archive -LiteralPath $modelArchive -DestinationPath $modelRoot -Force
    }
    finally {
        if (Test-Path -LiteralPath $modelArchive) { Remove-Item -LiteralPath $modelArchive }
    }
    foreach ($relativePath in $requiredModelFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $modelFolder $relativePath) -PathType Leaf)) {
            throw 'Vosk model download is incomplete. Check the Internet connection and run DEV_RUN.bat again.'
        }
    }
}
dotnet publish (Join-Path $projectRoot 'src\LiquidMusic\LiquidMusic.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $projectRoot 'native')
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Close the running widget from its tray menu, check the error above and retry.' }
$executable = Join-Path $projectRoot 'native\LiquidMusic.exe'
Write-Output $executable
if ($Launch) { Start-Process -FilePath $executable -WorkingDirectory $projectRoot }
