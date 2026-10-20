#!/usr/bin/env bash
# Run the in-game Living World autotest headless and print a summary.
# Usage: tools/autotest.sh [days] [plan] [extra game args...]     (default 10 days, plan "normal")
# Env: RW_TIMEOUT (default 1800 s), RW_RUNNER (default ../colony-scale/tools/headless/run-game.sh: the headless
#      harness of ColonyScale, github.com/UrekD/ColonyScale, checked out next to this repo as colony-scale),
#      RW_GAME_DIR (game install to run; default the Windows build under Wine), RW_SAVEDATA, RW_LOG
# Output: .work/run/autotest.log (full log), .work/run/autotest.summary (story + metrics + result).
# Exit: 0 PASS, 1 FAIL, 2 no result (crash/timeout).
set -uo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
days=${1:-10}; shift || true
plan=${1:-normal}; shift || true
runner=${RW_RUNNER:-$root/../colony-scale/tools/headless/run-game.sh}
log=${RW_LOG:-$root/.work/run/autotest.log}
summary=${log%.log}.summary
mkdir -p "$root/.work/run"
# setsid: the game signals its whole process group when it goes back to the main menu (the save/reload step),
# which would kill this script and the harness's log pipe.
# Shared game slots (see tools/rw-slot.sh): wait for a slot and memory before starting the game.
source "$root/tools/rw-slot.sh"
rw_slot_acquire
RIMWORLD_DIR=${RIMWORLD_DIR:-$("$root/tools/rimworld-dir.sh")} RW_SAVEDATA=${RW_SAVEDATA:-$root/.work/savedata} RW_LOG=$log \
  RW_TIMEOUT=${RW_TIMEOUT:-1800} RW_WAIT_FOR='LivingWorld:test\] (PASS|FAIL)' \
  setsid --wait "$runner" "-livingworld-test=$days" "-livingworld-plan=$plan" "$@" < /dev/null
{
  grep -E "^[0-9:]+ +(INFO|WARN|ERROR)" "$log" | grep -vE "Resolution too small|Steamworks|textures have no pixels|Stub! You may not" \
    | grep -E "LivingWorld|ERROR|Exception" | cut -c1-600
  grep -E "Config error|XML error|Could not resolve" "$log" | sort -u | sed 's/^/[defs] /'
} > "$summary"
tail -n 80 "$summary"
if grep -qE "Config error|XML error|Could not resolve cross-reference" "$log"; then echo "[autotest] def errors at load" >&2; exit 1; fi
grep -q "LivingWorld:test\] PASS" "$log" && exit 0
grep -q "LivingWorld:test\] FAIL" "$log" && exit 1
exit 2
