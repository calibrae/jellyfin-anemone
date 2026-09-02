#!/usr/bin/env bash
# Package the plugin the way Jellyfin expects it: plugins/<Name>_<version>/{DLL,meta.json}
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH"
PROJ=plugin/Jellyfin.Plugin.Anemone
VERSION=$(sed -n 's/.*<AssemblyVersion>\(.*\)<\/AssemblyVersion>.*/\1/p' "$PROJ/Jellyfin.Plugin.Anemone.csproj")
OUT="dist/Anemone_${VERSION}"
dotnet build "$PROJ" -c Release --nologo -v q
rm -rf "$OUT" && mkdir -p "$OUT"
cp "$PROJ/bin/Release/net9.0/Jellyfin.Plugin.Anemone.dll" "$OUT/"
cp "$PROJ/meta.json" "$OUT/"
# refuse to ship anything the host already loads
extra=$(ls "$PROJ/bin/Release/net9.0/" | grep -vE '^Jellyfin\.Plugin\.Anemone\.(dll|pdb|deps\.json)$' || true)
if [ -n "$extra" ]; then echo "ERROR: unexpected files in plugin output (would shadow host assemblies):"; echo "$extra"; exit 1; fi
# The zip is what a Jellyfin plugin repository serves, and Jellyfin unpacks it with
# ZipFile.ExtractToDirectory(stream, targetDir) straight into plugins/<Name>_<version>/ --
# so its members must sit at the ARCHIVE ROOT. Nesting them under a folder installs the DLL one
# level too deep and leaves meta.json where the loader cannot see it, which fails silently:
# without a manifest Jellyfin whitelists no assemblies and simply loads nothing.
(cd "$OUT" && rm -f "../Anemone_${VERSION}.zip" && zip -qr "../Anemone_${VERSION}.zip" .)
CHECKSUM=$(md5 -q "dist/Anemone_${VERSION}.zip" 2>/dev/null || md5sum "dist/Anemone_${VERSION}.zip" | cut -d" " -f1)
echo "packaged: $OUT and dist/Anemone_${VERSION}.zip"
echo "zip contents:"; unzip -l "dist/Anemone_${VERSION}.zip" | sed -n "4,\$p" | head -4
echo "md5 (for the repository manifest): $CHECKSUM"
ls -la "$OUT"
