#!/usr/bin/env bash
# Build a RimWorld 1.7 copy of Rimsential Spaceports (Continued) from its upstream source (MIT).
# Usage: tools/compat/build-spaceports-1.7.sh <spaceports checkout> <output mod dir> [RimWorld 1.7 install]
# The output is a complete mod folder: drop it into RimWorld/Mods (it keeps packageId zal.spaceports).
# Changes against upstream (see docs/compat-spaceports-1.7.md): v1.7 load folders + supportedVersions 1.7,
# Waylith.dll reference, HarmonyLib.MethodInvoker alias. Code otherwise unchanged.
set -euo pipefail
src=$(cd "$1" && pwd); out=$2; rw=${3:-${RIMWORLD_DIR:-$(cd "$(dirname "$0")/../.." && tools/rimworld-dir.sh)}}
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
build=$(mktemp -d); trap 'rm -rf "$build"' EXIT
cp "$here/spaceports/Spaceports.csproj" "$here/spaceports/MethodInvokerAlias.cs" "$build/"
dotnet build "$build/Spaceports.csproj" -c Release -nologo -v quiet -p:Src="$src/Source" -p:RimWorldDir="$rw"
rm -rf "$out"; mkdir -p "$out/1.7/Assemblies"
cp -r "$src/About" "$src/Languages" "$src/Sounds" "$src/Textures" "$out/"
cp -r "$src/1.6/Defs" "$src/1.6/Patches" "$src/1.6/Compat" "$out/1.7/"
cp "$build/bin/Spaceports.dll" "$out/1.7/Assemblies/"
cat > "$out/LoadFolders.xml" <<'X'
<?xml version="1.0" encoding="utf-8"?>
<loadFolders>
	<v1.7>
		<li>/</li>
		<li>1.7</li>
		<li IfModActive="Ludeon.Rimworld.Biotech">1.7/Compat/Biotech</li>
		<li IfModActive="Ludeon.Rimworld.Odyssey">1.7/Compat/Odyssey</li>
		<li IfModActive="Ludeon.Rimworld.Anomaly,Ludeon.Rimworld.Ideology">1.7/Compat/DLC</li>
		<li IfModActive="VanillaExpanded.VFEPropsandDecor">1.7/Compat/VEProps</li>
		<li IfModActive="kentington.saveourship2">1.7/Compat/SOS2</li>
	</v1.7>
</loadFolders>
X
grep -q "<li>1.7</li>" "$out/About/About.xml" || sed -i 's#<li>1.6</li>#<li>1.6</li>\n\t<li>1.7</li>#' "$out/About/About.xml"
echo "built $out"
