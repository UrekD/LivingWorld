#!/usr/bin/env bash
# The removal gate for held towns (PLAN §4, specs/agency.md "A3 removal gate"): a town the colony holds must work in a game that
# does not have Living World. Two steps:
#   1. A normal autotest run of the plan agencyremoval makes four saves: a world that held nothing and one that holds a town (taken through the
#      real assault path), each prepared for removing the mod ("LW-baseline", "LW-removal") and, as the isolated fixture for the claim that the
#      preparation is not what makes it load, not prepared ("LW-baseline-raw", "LW-removal-raw"; only the world parties are gone, which cannot
#      load without the mod). A held town's save has a sidecar file naming the town and its client faction.
#   2. A game with Core, the DLCs and the test-only mod tools/removal-probe (no Living World, no Harmony) loads each save from the main menu.
#      The probe checks the held town (a settlement of its client, not hostile; a trade session opens with tradeables; its map can be made,
#      attacked and removed), every pawn's ideo, then saves, reloads its own save and wants no error at all.
# Without Living World the component and every class of the mod "could not be found" in any of its saves, so zero errors cannot be asked of the
# load itself: the errors of a held town's load must all be among the errors of the load of the save that held nothing (numbers ignored).
# Usage: tools/removal-check.sh      Env: RIMWORLD_DIR, RW_GAME_DIR (as the other scripts), RW_RUNNER, RW_TIMEOUT, RW_NO_DLC=1 (Core only)
# Output: .work/removal/*.log; the last line is "[LivingWorld:test] PASS|FAIL plan=removal". Exit 0 PASS, 1 FAIL.
set -uo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
export RIMWORLD_DIR=${RIMWORLD_DIR:-$("$root/tools/rimworld-dir.sh")} || { echo "no RimWorld install found; set RIMWORLD_DIR" >&2; exit 1; }
runner=${RW_RUNNER:-$root/../colony-scale/tools/headless/run-game.sh}
game=${RW_GAME_DIR:-$RIMWORLD_DIR}
work=$root/.work/removal
mkdir -p "$work/make/Config" "$work/probe/Config" "$work/probe/Saves" "$root/.work/run"
fail() { echo "[LivingWorld:test] FAIL plan=removal $*"; exit 1; }

mods() { # extra package ids...
  echo '<?xml version="1.0" encoding="utf-8"?>'
  echo '<ModsConfigData><version>1.7.4980 rev60</version><activeMods>'
  for m in "$@"; do echo "<li>$m</li>"; done
  echo '</activeMods><knownExpansions>'
  echo '<li>ludeon.rimworld.royalty</li><li>ludeon.rimworld.ideology</li><li>ludeon.rimworld.biotech</li><li>ludeon.rimworld.anomaly</li><li>ludeon.rimworld.odyssey</li>'
  echo '</knownExpansions></ModsConfigData>'
}
core=(ludeon.rimworld)
[[ -n ${RW_NO_DLC:-} ]] || core+=(ludeon.rimworld.royalty ludeon.rimworld.ideology ludeon.rimworld.biotech ludeon.rimworld.anomaly ludeon.rimworld.odyssey)

# The probe, built and linked into the install (like tools/link-mod.sh does for the mod).
dotnet build tools/removal-probe/Source/RemovalProbe.csproj -c Release -v quiet -nologo > "$work/build.log" 2>&1 || { tail -n 20 "$work/build.log"; fail "the probe did not build"; }
RIMWORLD_DIR=$game tools/link-mod.sh || fail "the mod is not linked"
dest=$game/Mods/LivingWorldRemovalProbe
if [[ ! -e $dest && ! -L $dest ]]; then ln -s "$root/tools/removal-probe" "$dest"; fi
[[ $(realpath -m "$dest") == "$(realpath "$root/tools/removal-probe")" ]] || fail "$dest is not the probe"

# 1. The saves.
mods brrainz.harmony "${core[@]}" urekd.livingworld > "$work/make/Config/ModsConfig.xml"
rm -rf "$work/make/Saves"
RW_SAVEDATA=$work/make RW_LOG=$work/make.log tools/autotest.sh 1 agencyremoval > "$work/make.out" 2>&1
rc=$?
[[ $rc -eq 0 ]] || { tail -n 30 "$work/make.out"; fail "the run that makes the saves ended with $rc"; }
names=(LW-baseline LW-removal LW-baseline-raw LW-removal-raw)
for n in "${names[@]}"; do [[ -f $work/make/Saves/$n.rws ]] || fail "no save $n"; done
for n in LW-removal LW-removal-raw; do [[ -f $work/make/Saves/$n.info ]] || fail "no sidecar file for $n"; done

# 2. The loads, without the mod.
rm -rf "$work/probe/Saves"
mkdir -p "$work/probe/Saves"
cp "$work/make/Saves/"LW-*.rws "$work/make/Saves/"LW-*.info "$work/probe/Saves/"
mods "${core[@]}" urekd.livingworldremovalprobe > "$work/probe/Config/ModsConfig.xml"
source "$root/tools/rw-slot.sh"
rw_slot_acquire
for n in "${names[@]}"; do
  RW_SAVEDATA=$work/probe RW_LOG=$work/probe-$n.log RW_TIMEOUT=${RW_TIMEOUT:-900} RW_WAIT_FOR='LivingWorld:test\] (PASS|FAIL) plan=removal' \
    setsid --wait "$runner" "-removal-probe=$n" < /dev/null > "$work/probe-$n.out" 2>&1
  line=$(grep -ohE "LivingWorld:test\] (PASS|FAIL) plan=removal.*" "$work/probe-$n.log" | head -1 | cut -c1-400)
  echo "[removal-check] $n: ${line:-no result}"
  [[ $line == *PASS* ]] || fail "the probe did not pass on $n"
done

# The errors of a held town's load are all among those of the load that held nothing.
errors() { grep -ohE "\[LivingWorld:removal\] load error: .*" "$1" | sed 's/^.*load error: //' | sort -u; }
for pair in "LW-baseline LW-removal" "LW-baseline-raw LW-removal-raw"; do
  read -r base held <<< "$pair"
  new=$(comm -13 <(errors "$work/probe-$base.log") <(errors "$work/probe-$held.log"))
  if [[ -n $new ]]; then
    echo "$new" | sed 's/^/[removal-check] new load error: /'
    fail "the load of $held has errors the load of $base does not"
  fi
  echo "[removal-check] $held: $(errors "$work/probe-$held.log" | wc -l) load errors, none beyond those of $base"
done
echo "[LivingWorld:test] PASS plan=removal ${#names[@]} saves"
