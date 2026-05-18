#!/bin/bash
# ------------------------------------------------------------------
# package-release.sh - installer.exe + ReleaseNote.txt 를 zip 으로 묶음
#
# `/dist` skill 워크플로에서 **git commit 이후** 에 호출된다.
# 이 시점에는 ReleaseNote.txt 가 이미 update-releasenote.sh 를 통해
# 이번 배포 entry 가 prepend 된 상태이며, 그 변경이 방금 만들어진
# commit 에 포함되어 있다.
#
# Usage:
#   package-release.sh <CUR_VER> <INSTALLER_EXE>
#
# Output:
#   - <exe 와 같은 디렉토리>/Promaker_Setup_<CUR_VER>_sc.zip
#   - stdout 마지막 줄: zip 경로 (caller 가 capture)
# ------------------------------------------------------------------
set -e

CUR_VER="${1:?CUR_VER required}"
EXE="${2:?installer .exe path required}"

if [ ! -f "$EXE" ]; then
    echo "[ERROR] Installer not found: $EXE" >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROMAKER_DIR="$(dirname "$SCRIPT_DIR")"
RELNOTE="$PROMAKER_DIR/ReleaseNote.txt"

if [ ! -f "$RELNOTE" ]; then
    echo "[ERROR] ReleaseNote.txt not found: $RELNOTE" >&2
    echo "        Call update-releasenote.sh before package-release.sh." >&2
    exit 1
fi

DIR="$(cd "$(dirname "$EXE")" && pwd)"
EXE_BASE="$(basename "$EXE" .exe)"
ZIP="$DIR/${EXE_BASE}.zip"

if command -v zip >/dev/null 2>&1; then
    rm -f "$ZIP"
    zip -j "$ZIP" "$EXE" "$RELNOTE" >&2
elif command -v powershell.exe >/dev/null 2>&1; then
    if command -v cygpath >/dev/null 2>&1; then
        W_EXE=$(cygpath -w "$EXE")
        W_RELNOTE=$(cygpath -w "$RELNOTE")
        W_ZIP=$(cygpath -w "$ZIP")
    else
        W_EXE="$EXE"; W_RELNOTE="$RELNOTE"; W_ZIP="$ZIP"
    fi
    powershell.exe -NoProfile -NonInteractive -Command \
        "Compress-Archive -Path @('$W_EXE','$W_RELNOTE') -DestinationPath '$W_ZIP' -Force" >&2
else
    echo "[ERROR] Neither 'zip' nor 'powershell.exe' available - cannot create zip" >&2
    exit 1
fi

if [ ! -f "$ZIP" ]; then
    echo "[ERROR] Zip creation failed: $ZIP" >&2
    exit 1
fi

echo "Created: $ZIP" >&2
echo "$ZIP"
