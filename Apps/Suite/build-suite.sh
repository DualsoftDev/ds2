#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 규약: echo 출력은 ASCII 전용 — 한글은 주석에만 둘 것
#       (Windows cp949 콘솔 <-> Git Bash UTF-8 경계 깨짐 회피, Promaker Makefile 과 동일).
# ---------------------------------------------------------------------------
# Setup Dualsoft - 통합 설치본 빌드 오케스트레이션
#
# 흐름:
#   1) Promaker 설치본 빌드     : make -C ../Promaker dist-installer (MODE=sc)
#   2) DSPilot 설치본 빌드      : ../DSPilot/build-installer.bat (NOPAUSE=1 로 비대화형)
#   3) 각 Output 에서 갓 만든 .exe 경로 확보
#   4) ISCC 로 Installer/Suite.iss 컴파일 (두 .exe + SuiteVersion 주입)
#
# 산출:
#   Installer/Output/Setup_Dualsoft_<SuiteVersion>.exe       (표준: 오프라인 자체완결, 셋 다 동봉)
#   Installer/Output/Setup_Dualsoft_<SuiteVersion>_Lite.exe  (저용량: --lite)
#
# 저용량(--lite | LITE=1) 모드:
#   서브 설치본만 경량 변형으로 바꿔 문다 — DSPilot 은 build-installer-lite.bat(framework-dependent,
#   런타임/ffmpeg/MediaMTX 를 설치 중 다운로드), Promaker 는 make MODE=fd(.NET Desktop Runtime 다운로드).
#   Suite.iss 는 서브 설치본 경로를 /D 로 받으므로 구조 분기가 없다 — 무엇을 물리느냐만 달라진다.
#   ★출력 이름에 _Lite 꼬리표가 붙는다(OutputSuffix). 안 붙이면 같은 폴더의 표준 출고본을 덮어쓴다.
#
# 사용: 저장소 어디서든  bash Apps/Suite/build-suite.sh [--lite]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

LITE="${LITE:-0}"
for arg in "$@"; do
  case "$arg" in
    --lite) LITE=1 ;;
    *) echo "[ERROR] unknown argument: $arg (usage: build-suite.sh [--lite])" >&2; exit 1 ;;
  esac
done

ISCC="${ISCC:-C:/Program Files (x86)/Inno Setup 6/ISCC.exe}"
PROMAKER_DIR="$SCRIPT_DIR/../Promaker"
DSPILOT_DIR="$SCRIPT_DIR/../DSPilot"
PROMAKER_OUT="$PROMAKER_DIR/Installer/Output"
# ── 변형(표준 / 저용량) — 달라지는 것은 "어떤 서브 설치본을 물리느냐" 뿐이다. ──
if [ "$LITE" = "1" ]; then
  VARIANT="lite"
  DSPILOT_OUT="$DSPILOT_DIR/Output-lite"
  DSPILOT_BAT="$DSPILOT_DIR/build-installer-lite.bat"
  DSPILOT_GLOB="DSPilot_Setup_*_Lite.exe"
  PROMAKER_GLOB="Promaker_Setup_*_fd.exe"
  PROMAKER_MAKE_ARGS="MODE=fd"
  ISS_EXTRA_DEFS=(/DLite /DOutputSuffix=_Lite)
else
  VARIANT="standard"
  DSPILOT_OUT="$DSPILOT_DIR/Output"
  DSPILOT_BAT="$DSPILOT_DIR/build-installer.bat"
  DSPILOT_GLOB="DSPilot_Setup_*.exe"
  PROMAKER_GLOB="Promaker_Setup_*_sc.exe"
  PROMAKER_MAKE_ARGS=""
  ISS_EXTRA_DEFS=()
fi
SUITE_ISS="$SCRIPT_DIR/Installer/Suite.iss"
SUITE_VER="$(tr -d '[:space:]' < "$SCRIPT_DIR/SuiteVersion.txt")"

if [ -z "$SUITE_VER" ]; then
  echo "[ERROR] SuiteVersion.txt is empty" >&2
  exit 1
fi
if [ ! -f "$ISCC" ]; then
  echo "[ERROR] Inno Setup compiler not found: $ISCC" >&2
  echo "        Install Inno Setup 6 or override ISCC=..." >&2
  exit 1
fi

echo "==========================================================="
echo " Setup Dualsoft - building unified installer  (v$SUITE_VER, $VARIANT)"
echo "==========================================================="

# ── [1/4] Promaker 설치본 (make, MODE=sc 기본) ──
echo ""
echo "[1/4] Building Promaker installer (make dist-installer)..."
make -C "$PROMAKER_DIR" dist-installer $PROMAKER_MAKE_ARGS

# ── [2/4] DSPilot 설치본 (build-installer.bat, 비대화형) ──
# build-installer.bat 은 %~dp0 기준 절대경로를 쓰므로 절대 Windows 경로로 호출.
# NOPAUSE=1 가 끝의 pause 를 건너뛰게 한다(가드는 .bat 안에 추가됨).
echo ""
echo "[2/4] Building DSPilot installer (build-installer.bat)..."
DSPILOT_BAT_WIN="$(cygpath -w "$DSPILOT_BAT")"
NOPAUSE=1 cmd //c "$DSPILOT_BAT_WIN"

# ── [3/4] 갓 만든 산출물 경로 확보 (mtime 최신) ──
echo ""
echo "[3/4] Locating freshly built sub-installers..."
# command ls = 사용자 프로파일의 `ls -F`(classify, 파일명 끝에 '*' 부착) alias 우회.
PROMAKER_EXE="$(command ls -1t "$PROMAKER_OUT"/$PROMAKER_GLOB 2>/dev/null | head -n1 || true)"
DSPILOT_EXE="$(command ls -1t "$DSPILOT_OUT"/$DSPILOT_GLOB 2>/dev/null | head -n1 || true)"

if [ -z "$PROMAKER_EXE" ] || [ ! -f "$PROMAKER_EXE" ]; then
  echo "[ERROR] Promaker installer not found in $PROMAKER_OUT" >&2
  exit 1
fi
if [ -z "$DSPILOT_EXE" ] || [ ! -f "$DSPILOT_EXE" ]; then
  echo "[ERROR] DSPilot installer not found in $DSPILOT_OUT" >&2
  exit 1
fi
echo "      Promaker: $(basename "$PROMAKER_EXE")"
echo "      DSPilot : $(basename "$DSPILOT_EXE")"

PROMAKER_WIN="$(cygpath -w "$PROMAKER_EXE")"
DSPILOT_WIN="$(cygpath -w "$DSPILOT_EXE")"

# ── [4/4] Suite.iss 컴파일 ──
# MSYS_NO_PATHCONV / MSYS2_ARG_CONV_EXCL: Git Bash 가 native exe 의 /D 인자(Windows 경로)를
# Unix path 로 오인 변환하면 ISCC 가 거부한다. ISCC 호출 한정으로 변환 무력화 (Promaker Makefile 동일).
echo ""
echo "[4/4] Compiling Suite.iss (ISCC)..."
MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' \
"$ISCC" /DSuiteVersion="$SUITE_VER" \
        /DDsPilotSetup="$DSPILOT_WIN" \
        /DPromakerSetup="$PROMAKER_WIN" \
        ${ISS_EXTRA_DEFS[@]+"${ISS_EXTRA_DEFS[@]}"} \
        "$(cygpath -w "$SUITE_ISS")"

echo ""
echo "==========================================================="
echo " Build complete:"
if [ "$LITE" = "1" ]; then
  echo "   $SCRIPT_DIR/Installer/Output/Setup_Dualsoft_${SUITE_VER}_Lite.exe"
  echo "   (lite: .NET runtimes and CCTV binaries are downloaded during install)"
else
  echo "   $SCRIPT_DIR/Installer/Output/Setup_Dualsoft_$SUITE_VER.exe"
fi
echo "==========================================================="
