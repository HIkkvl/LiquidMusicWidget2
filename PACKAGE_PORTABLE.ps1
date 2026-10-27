param([string]$OutputRoot = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$nativeFolder = Join-Path $projectRoot 'native'
if (-not (Test-Path -LiteralPath (Join-Path $nativeFolder 'LiquidMusic.exe'))) {
    throw 'Сначала соберите приложение через BUILD_NATIVE.ps1'
}
$packageFolder = Join-Path $OutputRoot 'LiquidMusic-Portable'
$archivePath = Join-Path $OutputRoot 'LiquidMusic-Portable.zip'
if ((Test-Path -LiteralPath $packageFolder) -or (Test-Path -LiteralPath $archivePath)) {
    throw 'Выходная сборка уже существует. Выберите новую папку через -OutputRoot.'
}
New-Item -ItemType Directory -Path $packageFolder -Force | Out-Null
# Only published application files: no source tree, personal settings, logs,
# browser connection state, build caches, or absolute .lnk files.
Get-ChildItem -LiteralPath $nativeFolder -Force | Where-Object {
    $_.Name -notin @('settings.json', '.native-data', 'logs', 'portable.flag') -and $_.Extension -ne '.pdb'
} | Copy-Item -Destination $packageFolder -Recurse -Force
foreach ($file in @('START_WIDGET.vbs', 'START_WIDGET.bat', 'Создать ярлык.vbs')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $packageFolder
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'PORTABLE_README.txt') -Destination (Join-Path $packageFolder 'ПРОЧИТАЙ.txt')
Set-Content -LiteralPath (Join-Path $packageFolder 'settings.json') -Value '{}' -Encoding utf8
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $packageFolder, $archivePath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
Write-Output $archivePath
