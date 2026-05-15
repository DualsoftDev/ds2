#!/bin/bash
# ------------------------------------------------------------------
# update-releasenote.sh - ReleaseNote.txt 에 이번 배포 entry 를 prepend
#
# `/dist` skill 워크플로에서 git commit **직전** 에 호출된다.
# (ReleaseNote.txt 자체가 그 commit 의 일부로 staging 되어야 하므로)
#
# 파일 위치는 scripts/ 의 부모 (= Promaker/ReleaseNote.txt) 로 고정.
# git 으로 관리되며 매 배포마다 맨 위에 새 entry 를 쌓는 누적 방식.
#
# Usage:
#   update-releasenote.sh <CUR_VER> <COMMIT_MSG_FILE>
#
# 인자:
#   CUR_VER         - 이번 배포 버전 (예: 0.0.0.21). bump 이전 값.
#   COMMIT_MSG_FILE - Claude 가 작성한 commit message 전문 파일.
#                     첫 줄 = summary, 이어서 blank line + itemize.
#                     이 파일 내용이 그대로 entry 의 요약 본문이 됨.
#
# 출력:
#   - 파일: <Promaker>/ReleaseNote.txt (prepend 됨)
#   - stdout 마지막 줄: ReleaseNote.txt 절대경로
# ------------------------------------------------------------------
set -e

CUR_VER="${1:?CUR_VER required}"
MSGFILE="${2:?commit msg file required}"

if [ ! -f "$MSGFILE" ]; then
    echo "[ERROR] commit msg file not found: $MSGFILE" >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROMAKER_DIR="$(dirname "$SCRIPT_DIR")"     # scripts 의 부모 = Promaker
RELNOTE="$PROMAKER_DIR/ReleaseNote.txt"

# PREV_TAG - 현 HEAD 기준 최근 vPromaker_* tag. (아직 commit 전이므로 HEAD 기준.)
PREV_TAG=$(git describe --tags --match 'vPromaker_*' --abbrev=0 HEAD 2>/dev/null || true)
BUILD_DATE=$(date '+%Y-%m-%d %H:%M:%S')

# 새 entry 를 임시 파일에 작성
NEW_ENTRY=$(mktemp -t relnote-entry-XXXXXX)
trap 'rm -f "$NEW_ENTRY"' EXIT

# entry 본문은 MSGFILE 의 내용 그대로 사용. git log 자동 첨부는 하지 않음 -
# 사용자 관점에서 사소한 구현 세부사항까지 섞여 ReleaseNote 를 어지럽히므로,
# 선별 책임은 Claude 에게 위임 (/dist skill 의 commit msg 작성 단계 참조).
{
    echo "--------------------------------------------------"
    echo "[v${CUR_VER}] ${BUILD_DATE}"
    if [ -n "$PREV_TAG" ]; then
        echo "Previous: ${PREV_TAG}"
    else
        echo "Previous: (none - initial release)"
    fi
    echo "--------------------------------------------------"
    cat "$MSGFILE"
    if [ -n "$(tail -c 1 "$MSGFILE")" ]; then
        echo ""
    fi
    echo ""
} > "$NEW_ENTRY"

HEADER_LINE1="# Promaker Release Notes"
HEADER_LINE2=""

TMP="${RELNOTE}.tmp"

if [ -f "$RELNOTE" ]; then
    FIRST_LINE=$(head -n 1 "$RELNOTE" 2>/dev/null || true)
    if [ "$FIRST_LINE" = "$HEADER_LINE1" ]; then
        BODY_START=$(awk 'NR==1{next} NF>0{print NR; exit}' "$RELNOTE")
        if [ -z "$BODY_START" ]; then
            BODY_START=$(wc -l < "$RELNOTE")
            BODY_START=$((BODY_START + 1))
        fi
        {
            echo "$HEADER_LINE1"
            echo "$HEADER_LINE2"
            cat "$NEW_ENTRY"
            tail -n +"$BODY_START" "$RELNOTE"
        } > "$TMP"
    else
        {
            echo "$HEADER_LINE1"
            echo "$HEADER_LINE2"
            cat "$NEW_ENTRY"
            cat "$RELNOTE"
        } > "$TMP"
    fi
else
    {
        echo "$HEADER_LINE1"
        echo "$HEADER_LINE2"
        cat "$NEW_ENTRY"
    } > "$TMP"
fi

mv "$TMP" "$RELNOTE"

echo "ReleaseNote prepended: $RELNOTE" >&2
echo "$RELNOTE"
