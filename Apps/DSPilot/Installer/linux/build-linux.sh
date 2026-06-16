#!/usr/bin/env bash
# ============================================================================
#  DSPilot Linux 배포 패키지 빌더 (Windows build-installer.bat 의 Linux 대응물)
#  - DSPilot 을 linux-x64 self-contained 로 publish
#  - MediaMTX linux_amd64 바이너리 다운로드
#  - 설치 스크립트/유닛/설정과 함께 tarball 로 묶음
#
#  실행 위치: 어디서나(스크립트가 경로를 자동 계산). dotnet SDK 9 + curl + tar 필요.
#  산출물:   Output/DSPilot_linux-x64_<version>.tar.gz
#  CCTV 제외 빌드: SKIP_MEDIAMTX=1 ./build-linux.sh
# ============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DSPILOT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"     # Apps/DSPilot
PROJECT="$DSPILOT_ROOT/DSPilot/DSPilot.csproj"
RID="linux-x64"
MTX_VERSION="v1.19.1"                                # Windows 설치본과 동일 버전
SKIP_MEDIAMTX="${SKIP_MEDIAMTX:-0}"

STAGE="$SCRIPT_DIR/.stage"
OUTPUT_DIR="$DSPILOT_ROOT/Output"

command -v dotnet >/dev/null || { echo "오류: dotnet SDK 가 필요합니다." >&2; exit 1; }

echo "[1/4] 이전 산출물 정리..."
rm -rf "$STAGE"
mkdir -p "$STAGE/app" "$STAGE/systemd" "$OUTPUT_DIR"

echo "[2/4] DSPilot publish ($RID, self-contained)..."
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -o "$STAGE/app" --nologo
chmod +x "$STAGE/app/DSPilot"

# Promaker.Agent (헤드리스 PLC 모니터링 백엔드) 를 같은 RID 로 self-contained publish 해 패키지에 동봉.
# install.sh 가 --with-agent(Linux 기본 ON) 일 때 배치/등록한다. csproj 미발견 시 경고만(Agent 없이 빌드 계속).
AGENT_PROJECT="$DSPILOT_ROOT/../Promaker/Promaker.Agent/Promaker.Agent.csproj"
if [[ -f "$AGENT_PROJECT" ]]; then
  echo "[2b/4] Promaker.Agent publish ($RID, self-contained)..."
  dotnet publish "$AGENT_PROJECT" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -o "$STAGE/agent" --nologo
  chmod +x "$STAGE/agent/Promaker.Agent"
else
  echo "[2b/4] 경고: Promaker.Agent.csproj 미발견 — Agent 없이 빌드 (install.sh 가 자동 스킵)."
fi

# 사용자 런타임 데이터는 패키지에서 제외(Windows Inno 'Excludes' 와 동일) — 업그레이드 시 타깃의
# 도면/레이아웃/오버레이를 덮어쓰지 않도록. (publish 는 dev 의 wwwroot/uploads 를 끌어올 수 있음)
rm -f "$STAGE"/app/wwwroot/uploads/blueprint.* \
      "$STAGE"/app/wwwroot/uploads/layout-data.json* \
      "$STAGE"/app/wwwroot/uploads/cctv-overlays.json 2>/dev/null || true

VERSION="$(grep -oE '<Version>[^<]+' "$PROJECT" | head -n1 | sed 's/<Version>//')"
[[ -n "$VERSION" ]] || VERSION="0.0.0"

if [[ "$SKIP_MEDIAMTX" != "1" ]]; then
  echo "[3/4] MediaMTX $MTX_VERSION (linux_amd64) 다운로드..."
  mkdir -p "$STAGE/mediamtx"
  MTX_URL="https://github.com/bluenviron/mediamtx/releases/download/${MTX_VERSION}/mediamtx_${MTX_VERSION}_linux_amd64.tar.gz"
  TMP_TGZ="$(mktemp)"
  curl -fsSL "$MTX_URL" -o "$TMP_TGZ"
  tar -xzf "$TMP_TGZ" -C "$STAGE/mediamtx" mediamtx
  rm -f "$TMP_TGZ"
  chmod +x "$STAGE/mediamtx/mediamtx"
  # 커스터마이즈된 mediamtx.yml(Windows 와 공유) + 라이선스 고지 동봉.
  cp "$SCRIPT_DIR/../mediamtx/mediamtx.yml" "$STAGE/mediamtx/"
  cp "$SCRIPT_DIR/../mediamtx/LICENSE" "$STAGE/mediamtx/" 2>/dev/null || true
  cp "$SCRIPT_DIR/../mediamtx/LICENSE-winsw.txt" "$STAGE/mediamtx/" 2>/dev/null || true
else
  echo "[3/4] SKIP_MEDIAMTX=1 — CCTV 제외 빌드"
fi

echo "[4/4] 패키지 구성 및 압축..."
cp "$SCRIPT_DIR/dspilot.service" "$SCRIPT_DIR/mediamtx.service" "$STAGE/systemd/"
# Agent 가 publish 됐을 때만 그 유닛도 동봉 (없으면 install.sh 가 자동 스킵).
[[ -d "$STAGE/agent" ]] && cp "$SCRIPT_DIR/promaker-agent.service" "$STAGE/systemd/"
cp "$SCRIPT_DIR/install.sh" "$SCRIPT_DIR/uninstall.sh" "$STAGE/"
[[ -f "$SCRIPT_DIR/README.md" ]] && cp "$SCRIPT_DIR/README.md" "$STAGE/"
chmod +x "$STAGE/install.sh" "$STAGE/uninstall.sh"

PKG_NAME="DSPilot_${RID}_${VERSION}"
TARBALL="$OUTPUT_DIR/${PKG_NAME}.tar.gz"
# tarball 최상위가 PKG_NAME/ 디렉터리가 되도록 staging 을 rename 후 묶음.
rm -rf "$OUTPUT_DIR/$PKG_NAME"
cp -a "$STAGE" "$OUTPUT_DIR/$PKG_NAME"
tar -czf "$TARBALL" -C "$OUTPUT_DIR" "$PKG_NAME"
rm -rf "$OUTPUT_DIR/$PKG_NAME" "$STAGE"

echo ""
echo "완료: $TARBALL"
echo "설치(타깃 Linux 에서):"
echo "    tar -xzf ${PKG_NAME}.tar.gz && cd ${PKG_NAME} && sudo ./install.sh"
