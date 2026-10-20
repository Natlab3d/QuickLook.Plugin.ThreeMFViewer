$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$releaseDir = Join-Path $root 'bin\Release'
$pluginPath = Join-Path $root 'QuickLook.Plugin.ThreeMFViewer.qlplugin'
$tempZip = Join-Path $root 'QuickLook.Plugin.ThreeMFViewer.zip'
if (!(Test-Path -LiteralPath $releaseDir)) { throw "Release output not found: $releaseDir" }
$files = Get-ChildItem -LiteralPath $releaseDir -File | Where-Object { $_.Extension -notin @('.pdb','.xml') }
if (!$files) { throw "No plugin files found in $releaseDir" }
Remove-Item -LiteralPath $pluginPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path $files.FullName -DestinationPath $tempZip -Force
Move-Item -LiteralPath $tempZip -Destination $pluginPath
Write-Host "Created $pluginPath"