#!/usr/bin/env bash
# ============================================================================
#  DSPilot Linux 제거 스크립트
#    sudo ./uninstall.sh [--purge]
#  기본: 서비스/바이너리 제거, 사용자 데이터(공유 폴더)는 보존.
#  --purge: 공유 폴더(/var/lib/dualsoft/Shared, DB/AASX 포함)와 서비스 계정까지 삭제.
# ============================================================================
set -euo pipefail

APP_NAME="dspilot"
APP_USER="dspilot"
INSTALL_DIR="/opt/dspilot"
SHARED_DIR="/var/lib/dualsoft/Shared"
SVC_DSPILOT="${APP_NAME}.service"
SVC_MEDIAMTX="${APP_NAME}-mediamtx.service"
PURGE=0

[[ "${1:-}" == "--purge" ]] && PURGE=1
[[ $EUID -eq 0 ]] || { echo "오류: root 권한이 필요합니다 (sudo ./uninstall.sh)" >&2; exit 1; }

for svc in "$SVC_DSPILOT" "$SVC_MEDIAMTX"; do
  if systemctl list-unit-files "$svc" >/dev/null 2>&1; then
    echo "==> 서비스 중지/비활성화: $svc"
    systemctl disable --now "$svc" >/dev/null 2>&1 || true
    rm -f "/etc/systemd/system/$svc"
  fi
done
systemctl daemon-reload

echo "==> 앱 파일 삭제: $INSTALL_DIR"
rm -rf "$INSTALL_DIR"

if [[ $PURGE -eq 1 ]]; then
  echo "==> [purge] 공유 데이터 삭제: $SHARED_DIR"
  rm -rf "$SHARED_DIR"
  if id -u "$APP_USER" >/dev/null 2>&1; then
    echo "==> [purge] 서비스 계정 삭제: $APP_USER"
    userdel "$APP_USER" 2>/dev/null || true
  fi
else
  echo "==> 사용자 데이터 보존: $SHARED_DIR (완전 삭제는 --purge)"
fi

echo "제거 완료. (방화벽 규칙은 자동 삭제하지 않음 — 필요 시 수동 제거)"
