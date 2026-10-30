# Builds the Thunderstore zip: package\SmartSave-<version>.zip
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
dotnet build -c Release
$v = (Get-Content package\SmartSave\manifest.json | ConvertFrom-Json).version_number
Copy-Item bin\Release\SmartSave.dll package\SmartSave\plugins\ -Force
Copy-Item README.md, CHANGELOG.md package\SmartSave\ -Force
$zip = "package\SmartSave-$v.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path package\SmartSave\* -DestinationPath $zip
Write-Host $zip
