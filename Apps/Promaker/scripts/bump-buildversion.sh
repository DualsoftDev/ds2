#!/bin/bash
# Bump the last segment of BuildVersion.txt by +1.
# Supports both 3-segment (X.Y.Z -> X.Y.(Z+1)) and 4-segment
# (X.Y.Z.W -> X.Y.Z.(W+1)) formats; Promaker historically uses the
# 4-segment .NET AssemblyVersion form so the last slot is the
# Revision number.
#
# Resolves symlinks so the master file is updated regardless of which
# symlink is passed in.
#
# Usage:
#   bump-buildversion.sh <BuildVersion.txt>
set -e

FILE="${1:?BuildVersion.txt path required}"

REAL="$(readlink -f "$FILE" 2>/dev/null || echo "$FILE")"

if [ ! -f "$REAL" ]; then
    echo "[ERROR] BuildVersion file not found: $FILE -> $REAL" >&2
    exit 1
fi

VER=$(tr -d '\r\n ' < "$REAL")

# Split by '.' into HEAD (all but last) + LAST
LAST="${VER##*.}"
HEAD="${VER%.*}"

# Defensive: LAST must be a non-negative integer; HEAD must not equal VER
# (i.e. there must be at least one dot in VER)
case "$LAST" in
    ''|*[!0-9]*)
        echo "[ERROR] Cannot parse last segment from version '$VER' (file: $REAL)" >&2
        exit 1
        ;;
esac

if [ "$HEAD" = "$VER" ]; then
    echo "[ERROR] Version '$VER' must contain at least one dot (file: $REAL)" >&2
    exit 1
fi

NEW_LAST=$((LAST + 1))
NEW_VER="$HEAD.$NEW_LAST"
printf '%s\n' "$NEW_VER" > "$REAL"

echo "Version bumped: $VER -> $NEW_VER  ($REAL)"
