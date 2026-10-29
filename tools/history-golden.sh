#!/usr/bin/env bash
# Inert proof for history: runs the `history` plan (1 day after the 10 generated years) through tools/autotest.sh and keeps the
# [LivingWorld:history] lines with the log timestamp prefix and every wall-clock token (\b\w*[Mm]s=<number>) stripped. The same seed
# and the same build must give the same lines, so a feature that is off (or whose DLC is absent) must reproduce the golden exactly.
# Every run sets LIVINGWORLD_DETERMINISM=1, so the game also logs one [LivingWorld:det] line per history day (a checksum of the sim
# state, Verse.Rand's seed and draws so far); they are kept with the history lines, so the first differing line names the first differing day.
# The one [LivingWorld:detids] line (id counters, leaders' thing ids) is logged but not compared.
# Usage: tools/history-golden.sh determinism <suffix>           two runs on one build and folder; the lines must be identical
#        tools/history-golden.sh record <suffix>                write the golden <main checkout>/.work/golden/baseline-history<suffix>.txt
#        tools/history-golden.sh compare <suffix> [off,list]    run (with LIVINGWORLD_SETTINGS_OFF=off,list) and diff against the golden
# <suffix> is the save-data folder: "" (.work/savedata, all DLCs), -nodlc, -ideo, -bio, -roy, -ano, -ideobio (tools/make-savedata.sh).
# The folder is looked up in this checkout's .work, then in the main checkout's; only its ModsConfig.xml is used (each run gets a scratch
# folder .work/savedata-golden<suffix>-<tag>, so saves of other runs are untouched).
# The golden lives in the main checkout (found through git's common dir), so every worktree sees the same files. The merge rule
# (specs/dlc.md, Tests): regenerate a golden only in the merge of a step that intends to change history on that folder.
# Env: RW_GAME_DIR (default ~/RimWorld-linux: the native build of tools/regression.sh), RW_TIMEOUT (default 900 s per game),
#      RW_DAYS (days after history, default 1; a golden is only comparable with the same value), GOLDEN_DIR (override the golden folder).
#      The game loads the Mods/LivingWorld that <RW_GAME_DIR>/Mods links to: make that this checkout (tools/link-mod.sh) before running.
# Exit: 0 clean, 1 lines differ or a game failed, 2 usage or missing input.
set -uo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
G=${RW_GAME_DIR:-$HOME/RimWorld-linux}
days=${RW_DAYS:-1}

common=$(git rev-parse --git-common-dir 2>/dev/null) || common=.git
[[ $common == /* ]] || common=$root/$common
main=$(cd "$common/.." && pwd)
golden_dir=${GOLDEN_DIR:-$main/.work/golden}

usage() { sed -n '2,/^set -uo/p' "${BASH_SOURCE[0]}" | grep '^#' | sed 's/^# \{0,1\}//' >&2; exit 2; }
[[ $# -ge 2 ]] || usage
cmd=$1 suffix=$2 off=${3:-}
[[ $suffix =~ ^(-[a-z0-9]+)?$ ]] || { echo "suffix must be empty or -name (lower case), got '$suffix'" >&2; exit 2; }
[[ $cmd == compare || -z $off ]] || usage
case $cmd in determinism | record | compare) ;; *) usage ;; esac
golden=$golden_dir/baseline-history$suffix.txt
label=${suffix:-"(all DLCs)"}

config=
for d in "$root/.work" "$main/.work"; do
  [[ -f $d/savedata$suffix/Config/ModsConfig.xml ]] && { config=$d/savedata$suffix/Config/ModsConfig.xml; break; }
done
[[ -n $config ]] || { echo "no .work/savedata$suffix/Config/ModsConfig.xml (tools/make-savedata.sh '$suffix' ...)" >&2; exit 2; }
[[ -d $G ]] || { echo "game dir $G not found (RW_GAME_DIR)" >&2; exit 2; }
mkdir -p .work/run

# run <tag> <settings-off-or-empty> <out-file>: one game; the stripped history lines go to <out-file>. Returns 1 when the game or the extraction fails.
run() {
  local tag=$1 so=$2 out=$3
  local sd=$PWD/.work/savedata-golden$suffix-$tag log=$PWD/.work/run/golden$suffix-$tag.log
  rm -rf "$sd"
  mkdir -p "$sd/Config"
  cp "$config" "$sd/Config/ModsConfig.xml"
  rm -f "$log"
  echo "[history-golden] $label $tag: game $G, days $days${so:+, LIVINGWORLD_SETTINGS_OFF=$so}" >&2
  # The off list is always set (empty = off nothing) or unset, so an inherited value never leaks into a record or a determinism run.
  # LIVINGWORLD_DETERMINISM=1: the game logs a [LivingWorld:det] checksum per history day, so a difference shows its first day.
  local rc=0
  if [[ -n $so ]]; then
    LIVINGWORLD_DETERMINISM=1 LIVINGWORLD_SETTINGS_OFF=$so RW_GAME_DIR=$G RW_SAVEDATA=$sd RW_LOG=$log RW_TIMEOUT=${RW_TIMEOUT:-900} tools/autotest.sh "$days" history > "${log%.log}.out" 2>&1 || rc=$?
  else
    env -u LIVINGWORLD_SETTINGS_OFF LIVINGWORLD_DETERMINISM=1 RW_GAME_DIR=$G RW_SAVEDATA=$sd RW_LOG=$log RW_TIMEOUT=${RW_TIMEOUT:-900} tools/autotest.sh "$days" history > "${log%.log}.out" 2>&1 || rc=$?
  fi
  if (( rc != 0 )); then
    echo "[history-golden] $label $tag: autotest exit $rc (see $log and ${log%.log}.summary)" >&2
    return 1
  fi
  extract "$log" > "$out"
  if ! grep -q 'history\] year=9 ' "$out" || ! grep -q 'history\] totals ' "$out"; then
    echo "[history-golden] $label $tag: the log has no complete history (year=9 line and totals line missing): $log" >&2
    return 1
  fi
}

# The history lines and the per-day checksums of a game log, in log order: no timestamp and level, no wall-clock token, blanks collapsed.
extract() {
  grep -E '^[0-9:]+ +(INFO|WARN|ERROR) +\[LivingWorld:(history|det)\]' "$1" \
    | sed -E 's/^[0-9:]+ +[A-Z]+ +//; s/\b\w*[Mm]s=[0-9.]+//g; s/ +/ /g; s/ +$//'
}

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

show_diff() { # a b: the first lines of the diff
  diff -u "$1" "$2" | head -n 60
}

case $cmd in
  determinism)
    run a "" "$tmp/a" & pa=$!
    run b "" "$tmp/b" & pb=$!
    wait $pa || fail=1
    wait $pb || fail=1
    [[ -z ${fail:-} ]] || exit 1
    if diff -q "$tmp/a" "$tmp/b" > /dev/null; then
      echo "[history-golden] determinism $label: identical ($(wc -l < "$tmp/a") lines)"
    else
      echo "[history-golden] determinism $label: the two runs differ:"
      show_diff "$tmp/a" "$tmp/b"
      exit 1
    fi
    ;;
  record)
    run rec "" "$tmp/rec" || exit 1
    mkdir -p "$golden_dir"
    commit=$(git rev-parse --short HEAD 2>/dev/null || echo unknown)
    [[ -z $(git status --porcelain --untracked-files=no 2>/dev/null) ]] || commit+="+dirty"
    {
      echo "# commit=$commit runner=$G folder=${suffix:-all} plan=history days=$days recorded=$(date +%F)"
      cat "$tmp/rec"
    } > "$golden"
    echo "[history-golden] recorded $golden ($(wc -l < "$tmp/rec") lines, commit $commit)"
    ;;
  compare)
    [[ -f $golden ]] || { echo "no golden $golden (record it first)" >&2; exit 2; }
    grep -v '^#' "$golden" > "$tmp/golden"
    run cmp "$off" "$tmp/cmp" || exit 1
    if diff -q "$tmp/golden" "$tmp/cmp" > /dev/null; then
      echo "[history-golden] compare $label${off:+ (off: $off)}: clean against the golden ($(head -n 1 "$golden" | cut -c3-))"
    else
      echo "[history-golden] compare $label${off:+ (off: $off)}: DIFFERS from $golden:"
      show_diff "$tmp/golden" "$tmp/cmp"
      exit 1
    fi
    ;;
esac
