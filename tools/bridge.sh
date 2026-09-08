#!/usr/bin/env bash
# Sends one command to the in-editor ClaudeBridge and prints the result.
set -u
DIR="Temp/ClaudeBridge"
mkdir -p "$DIR"; rm -f "$DIR/result.txt"
printf '%s' "$*" > "$DIR/command.txt"
for _ in $(seq 1 "${BRIDGE_TIMEOUT:-30}"); do
  [ -f "$DIR/result.txt" ] && { cat "$DIR/result.txt"; exit 0; }
  sleep 1
done
echo "TIMEOUT: no result (editor compiling, not ticking, or bridge unloaded)"
exit 1
