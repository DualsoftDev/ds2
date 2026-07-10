#!/usr/bin/env bash
# ============================================================================
#  BriefingRelay Linux 배포 패키지 빌더
#  - BriefingRelay 를 linux-x64 self-contained 로 publish (런타임 포함, .NET 설치 불필요)
#  - 설치 스크립트/유닛/설정과 함께 tarball 로 묶음
#
#  실행: 어디서나(경로 자동 계산). dotnet SDK 9 + tar 필요. (Windows 는 Git Bash 에서 실행 가능)
#  산출물: Output/BriefingRelay_linux-x64_<version>.tar.gz
# ============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RELAY_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"     # Apps/BriefingRelay
PROJECT="$RELAY_ROOT/BriefingRelay.csproj"
RID="linux-x64"
STAGE="$SCRIPT_DIR/.stage"
OUTPUT_DIR="$RELAY_ROOT/Output"

command -v dotnet >/dev/null || { echo "오류: dotnet SDK 가 필요합니다." >&2; exit 1; }

echo "[1/3] 이전 산출물 정리..."
rm -rf "$STAGE"
mkdir -p "$STAGE/app" "$STAGE/systemd" "$OUTPUT_DIR"

echo "[2/3] BriefingRelay publish ($RID, self-contained)..."
# 단일 파일 대신 폴더 배포(single-file self-extract 의 시스템 계정/임시경로 이슈 회피 — DSPilot 와 동일 방침).
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -o "$STAGE/app" --nologo
chmod +x "$STAGE/app/BriefingRelay" 2>/dev/null || true
# 실제 자격증명 파일이 dev 폴더에 있어도 패키지에 포함하지 않는다(설치 시 주입/보존).
rm -f "$STAGE/app/appsettings.Secrets.json" 2>/dev/null || true

VERSION="$(grep -oE '<Version>[^<]+' "$PROJECT" | head -n1 | sed 's/<Version>//')"
[[ -n "$VERSION" ]] || VERSION="1.0.0"

echo "[3/3] 패키지 구성 및 압축..."
cp "$SCRIPT_DIR/briefingrelay.service" "$STAGE/systemd/"
cp "$SCRIPT_DIR/install.sh" "$SCRIPT_DIR/uninstall.sh" "$STAGE/"
cp "$RELAY_ROOT/SETUP-O365.md" "$STAGE/" 2>/dev/null || true
cp "$RELAY_ROOT/README.md" "$STAGE/" 2>/dev/null || true
chmod +x "$STAGE/install.sh" "$STAGE/uninstall.sh"

# CRLF→LF 정규화 — Windows(Git autocrlf)에서 빌드 시 .sh/.service 가 CRLF 면 리눅스에서 shebang·유닛이 깨진다.
find "$STAGE" -type f \( -name '*.sh' -o -name '*.service' \) -exec sed -i 's/\r$//' {} +

PKG_NAME="BriefingRelay_${RID}_${VERSION}"
TARBALL="$OUTPUT_DIR/${PKG_NAME}.tar.gz"
rm -rf "$OUTPUT_DIR/$PKG_NAME"
cp -a "$STAGE" "$OUTPUT_DIR/$PKG_NAME"
tar -czf "$TARBALL" -C "$OUTPUT_DIR" "$PKG_NAME"
rm -rf "$OUTPUT_DIR/$PKG_NAME" "$STAGE"

echo ""
echo "완료: $TARBALL"
echo "설치(타깃 Linux 에서):"
echo "    tar -xzf ${PKG_NAME}.tar.gz && cd ${PKG_NAME} && sudo ./install.sh"
