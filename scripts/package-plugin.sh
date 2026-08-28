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
(cd dist && rm -f "Anemone_${VERSION}.zip" && zip -qr "Anemone_${VERSION}.zip" "Anemone_${VERSION}")
echo "packaged: $OUT and dist/Anemone_${VERSION}.zip"
ls -la "$OUT"
