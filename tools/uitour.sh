#!/usr/bin/env bash
# Windowed UI tour with screenshots: runs the game (Xvfb + lavapipe) with plan "uitour" and saves a PNG per UI step to
# .work/shots/. Uses the run-game.sh of ColonyScale (github.com/UrekD/ColonyScale, checked out as ../colony-scale, or
# RW_RUNNER) in windowed mode (RW_WINDOWED=1). Default game: the native Linux build in ~/RimWorld-linux (RW_GAME_DIR).
set -uo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
runner=${RW_RUNNER:-$root/../colony-scale/tools/headless/run-game.sh}
shots=$root/.work/shots
savedata=$root/.work/savedata-ui
log=$root/.work/run/uitour.log
mkdir -p "$shots" "$savedata/Config" "$root/.work/run"
rm -f "$shots"/*.png "$log"
[[ -f $savedata/Config/ModsConfig.xml ]] || cp "$root/.work/savedata/Config/ModsConfig.xml" "$savedata/Config/"
cat > "$savedata/Config/Prefs.xml" <<'P'
<?xml version="1.0" encoding="utf-8"?>
<PrefsData>
  <volumeMaster>0</volumeMaster>
  <screenWidth>1280</screenWidth>
  <screenHeight>720</screenHeight>
  <fullscreen>False</fullscreen>
  <customCursorEnabled>False</customCursorEnabled>
  <runInBackground>True</runInBackground>
  <langFolderName>English</langFolderName>
  <devMode>True</devMode>
</PrefsData>
P
# Shared game slots (see tools/rw-slot.sh).
source "$root/tools/rw-slot.sh"
rw_slot_acquire
RIMWORLD_DIR=${RIMWORLD_DIR:-$("$root/tools/rimworld-dir.sh")} RW_GAME_DIR=${RW_GAME_DIR:-$HOME/RimWorld-linux} RW_WINDOWED=1 RW_RES=1280x720 \
  RW_SAVEDATA=$savedata RW_LOG=$log RW_TIMEOUT=${RW_TIMEOUT:-1500} setsid --wait "$runner" -livingworld-test=99 -livingworld-plan=uitour < /dev/null &
runner_pid=$!
fb=$(dirname "$log")/Xvfb_screen0
taken=""
while kill -0 $runner_pid 2>/dev/null; do
  sleep 3
  [[ -f $log ]] || continue
  while read -r n name; do
    [[ " $taken " == *" $n "* ]] && continue
    sleep 14   # let the window draw a few frames (software Vulkan is slow)
    python3 "$(dirname "$runner")/xwd2png.py" "$fb" "$shots/$(printf %02d "$n")-$name.png" 2>/dev/null \
      && echo "[uitour] shot $n $name" >&2
    taken="$taken $n"
  done < <(grep -oE "\[LivingWorld:ui\] step [0-9]+ [a-z0-9-]+" "$log" | awk '{print $3, $4}')
done
wait $runner_pid
ls "$shots"
