#!/usr/bin/env bash
# Regression suite: runs the autotest plans in parallel on the native Linux build (no Wine lock) and prints one
# PASS/FAIL line per plan. Core plans need only Harmony + the game; the compat plans run when their save-data
# folders exist (.work/savedata-hosp with Hospitality enabled, .work/savedata-sp with Hospitality + Spaceports;
# see docs/compat-hospitality-spaceports.md for building those mods). The no-DLC runs (normal-nodlc, history-nodlc) are skipped without
# .work/savedata-nodlc (tools/make-savedata.sh --all).
# Usage: tools/regression.sh     Env: RW_GAME_DIR (default ~/RimWorld-linux), RW_PARALLEL (games at once, default 4;
# each game takes a few GB of memory; every game also takes a shared machine-wide slot, tools/rw-slot.sh)
set -uo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
G=${RW_GAME_DIR:-$HOME/RimWorld-linux}
P=${RW_PARALLEL:-4}
run() { # name days plan savedata
  local sd=$PWD/.work/savedata-reg-$1
  mkdir -p "$sd/Config"
  cp "$4/Config/ModsConfig.xml" "$sd/Config/" 2>/dev/null || return 0
  while (( $(jobs -rp | wc -l) >= P )); do wait -n; done
  rm -f "$PWD/.work/run/reg-$1.log"
  RW_GAME_DIR=$G RW_SAVEDATA=$sd RW_LOG=$PWD/.work/run/reg-$1.log RW_TIMEOUT=900 tools/autotest.sh "$2" "$3" > "$PWD/.work/run/reg-$1.out" 2>&1 &
}
mkdir -p .work/run
run history 1 history .work/savedata
LIVINGWORLD_PERFLOG=file run normal 5 normal .work/savedata
run roadtrade 2 roadtrade .work/savedata
run refugees 2 refugees .work/savedata
run demands 1 demands .work/savedata
run diplomacy 5 diplomacy .work/savedata
run prisoners 1 prisoners .work/savedata
run ages 1 ages .work/savedata
run townmaps 1 townmaps .work/savedata
run agency 3 agency .work/savedata
[[ -d .work/savedata-core ]] && run agencycore 3 agencycore .work/savedata-core
run normal-nodlc 5 normal .work/savedata-nodlc
run history-nodlc 1 history .work/savedata-nodlc
[[ -d .work/savedata-hosp ]] && run hospitality 4 hospitality .work/savedata-hosp
[[ -d .work/savedata-sp ]] && run spaceports 4 spaceports .work/savedata-sp
wait
# The removal gate: two runs of its own (the saves, then the loads without the mod), one after the other.
rm -f .work/run/reg-removal.log
RW_GAME_DIR=$G tools/removal-check.sh > .work/run/reg-removal.out 2>&1
grep -hE "LivingWorld:test\] (PASS|FAIL)" .work/run/reg-removal.out | tail -n 1 > .work/run/reg-removal.log
status=0
for f in .work/run/reg-*.log; do
  name=$(basename "$f" .log); name=${name#reg-}
  line=$(grep -ohE "LivingWorld:test\] (PASS|FAIL).*" "$f" | head -1 | cut -c1-240)
  printf "%-12s %s\n" "$name" "${line:-no result}"
  [[ $line == *PASS* ]] || status=1
done
exit $status
