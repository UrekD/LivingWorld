#!/usr/bin/env bash
# Write a save-data folder's mod list for headless runs: .work/savedata<suffix>/Config/ModsConfig.xml (version 1.7.4980 rev60;
# Harmony, Core, the listed DLCs, Living World), as CLAUDE.md prescribes. The folders are local scratch (.work is not committed).
# Usage: tools/make-savedata.sh <suffix> [Royalty] [Ideology] [Biotech] [Anomaly] [Odyssey]
#   tools/make-savedata.sh -nodlc                       Harmony, Core, LW
#   tools/make-savedata.sh -ideo Ideology               (likewise -bio Biotech, -roy Royalty, -ano Anomaly)
#   tools/make-savedata.sh -ideobio Ideology Biotech    Ideology + Biotech (the classic-mode case)
#   tools/make-savedata.sh -noroy Ideology Biotech Anomaly   (the DLC-drop case: all but Royalty)
#   tools/make-savedata.sh --all                        the standard folder set of specs/dlc.md (D0): -nodlc -ideo -bio -roy -ano -ideobio
# The all-DLC folder .work/savedata (suffix "") already exists and holds saves: its ModsConfig.xml is only rewritten with FORCE=1.
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)

write() { # suffix dlc...
  local suffix=$1; shift
  local dir=$root/.work/savedata$suffix
  local known=(Royalty Ideology Biotech Anomaly Odyssey) d k found
  for d in "$@"; do
    found=
    for k in "${known[@]}"; do [[ $d == "$k" ]] && found=1; done
    [[ -n $found ]] || { echo "unknown DLC '$d' (one of: ${known[*]})" >&2; return 1; }
  done
  mkdir -p "$dir/Config"
  {
    echo '<?xml version="1.0" encoding="utf-8"?>'
    echo '<ModsConfigData>'
    echo '  <version>1.7.4980 rev60</version>'
    echo '  <activeMods>'
    echo '    <li>brrainz.harmony</li>'
    echo '    <li>ludeon.rimworld</li>'
    # Load order is the fixed expansion order, whatever order the arguments came in.
    for k in "${known[@]}"; do
      for d in "$@"; do [[ $d == "$k" ]] && echo "    <li>ludeon.rimworld.${k,,}</li>"; done
    done
    echo '    <li>urekd.livingworld</li>'
    echo '  </activeMods>'
    echo '  <knownExpansions>'
    for k in "${known[@]}"; do echo "    <li>ludeon.rimworld.${k,,}</li>"; done
    echo '  </knownExpansions>'
    echo '</ModsConfigData>'
  } > "$dir/Config/ModsConfig.xml"
  local list=Harmony,\ Core
  for d in "$@"; do list+=", $d"; done
  echo "[make-savedata] .work/savedata$suffix: $list, LW" >&2
}

if [[ ${1:-} == --all ]]; then
  write -nodlc
  write -ideo Ideology
  write -bio Biotech
  write -roy Royalty
  write -ano Anomaly
  write -ideobio Ideology Biotech
  exit 0
fi
if [[ $# -lt 1 ]]; then
  sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | grep '^#' | sed 's/^# \{0,1\}//' >&2
  exit 2
fi
suffix=$1; shift
[[ $suffix =~ ^(-[a-z0-9]+)?$ ]] || { echo "suffix must be empty or -name (lower case), got '$suffix'" >&2; exit 2; }
if [[ -z $suffix && -f $root/.work/savedata/Config/ModsConfig.xml && ${FORCE:-0} != 1 ]]; then
  echo "[make-savedata] .work/savedata exists (it holds saves); FORCE=1 to rewrite its ModsConfig.xml" >&2
  exit 0
fi
write "$suffix" "$@"
