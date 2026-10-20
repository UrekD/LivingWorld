# Machine-wide RimWorld game slots, shared with other projects' runs on this box (each headless game takes ~4.5 GB):
# source this, then `rw_slot_acquire` blocks until one of RW_SLOTS lock files (/tmp/rimworld-slot-N.lock) is free and
# at least RW_MIN_FREE_GB of memory is available. The flock is held by this shell until it exits (no stale slots).
RW_SLOTS=${RW_SLOTS:-5}
RW_MIN_FREE_GB=${RW_MIN_FREE_GB:-6}
rw_slot_acquire() {
  local i fd announced=
  while :; do
    if (( $(awk '/MemAvailable/ {print int($2/1048576)}' /proc/meminfo) >= RW_MIN_FREE_GB )); then
      for (( i = 0; i < RW_SLOTS; i++ )); do
        exec {fd}>"/tmp/rimworld-slot-$i.lock"
        if flock -n "$fd"; then RW_SLOT=$i; RW_SLOT_FD=$fd; return 0; fi
        exec {fd}>&-
      done
    fi
    [[ -n $announced ]] || { echo "[rw-slot] waiting for a free game slot / memory" >&2; announced=1; }
    sleep 30
  done
}
