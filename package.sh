#!/bin/sh
# Builds the Thunderstore zip: package/SmartSave-<version>.zip
set -e
cd "$(dirname "$0")"
dotnet build -c Release
V=$(sed -n 's/.*"version_number": "\(.*\)".*/\1/p' package/SmartSave/manifest.json)
cp bin/Release/SmartSave.dll package/SmartSave/plugins/
cp README.md CHANGELOG.md package/SmartSave/
(cd package/SmartSave && zip -qr "../SmartSave-$V.zip" .)
echo "package/SmartSave-$V.zip"
