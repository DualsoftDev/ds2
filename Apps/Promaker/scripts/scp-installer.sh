#!/bin/bash
# Upload a built installer artifact to the public download server.
# Used by Promaker `make dist` and the /dist skill.
#
# Usage:
#   scp-installer.sh <artifact> [scp-dest]
#
# Default destination: download@dualsoft.co.kr:/media/download/Dualsoft Software/Setup ProMaker(DS2_ProMaker) - Windows x64
set -e

EXE="${1:?artifact path required}"
DEST="${2:-download@dualsoft.co.kr:/media/download/Dualsoft Software/Setup ProMaker(DS2_ProMaker) - Windows x64}"

if [ ! -f "$EXE" ]; then
    echo "[ERROR] Artifact not found: $EXE" >&2
    exit 1
fi

NAME="$(basename "$EXE")"
echo "Uploading $NAME to $DEST ..."
scp "$EXE" "$DEST"
echo "Upload completed."
