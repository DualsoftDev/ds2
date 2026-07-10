#!/usr/bin/env bash
# ============================================================================
#  BriefingRelay 제거 스크립트
#    sudo ./uninstall.sh [--purge]
#  기본: 서비스 중지·비활성·유닛 제거. --purge 는 설치 디렉터리(자격증명 포함)와 서비스 계정까지 삭제.
# ============================================================================
set -euo pipefail

APP_NAME="briefingrelay"
APP_USER="briefingrelay"
INSTALL_DIR="/opt/briefingrelay"
SVC="${APP_NAME}.service"
PURGE=0

[[ "${1:-}" == "--purge" ]] && PURGE=1
[[ $EUID -eq 0 ]] || { echo "오류: root 권한이 필요합니다 (sudo ./uninstall.sh)" >&2; exit 1; }

if systemctl list-unit-files "$SVC" >/dev/null 2>&1; then
  echo "==> 서비스 중지/비활성: $SVC"
  systemctl disable --now "$SVC" 2>/dev/null || true
fi
if [[ -f "/etc/systemd/system/$SVC" ]]; then
  rm -f "/etc/systemd/system/$SVC"
  systemctl daemon-reload
  echo "==> 유닛 제거 완료"
fi

if [[ $PURGE -eq 1 ]]; then
  echo "==> --purge: 설치 디렉터리/계정 삭제"
  rm -rf "$INSTALL_DIR"
  id -u "$APP_USER" >/dev/null 2>&1 && userdel "$APP_USER" 2>/dev/null || true
  echo "    삭제됨: $INSTALL_DIR, 사용자 $APP_USER (⚠️ 자격증명 포함 완전 삭제)"
else
  echo "==> 설치 디렉터리는 보존: $INSTALL_DIR (완전 삭제하려면 --purge)"
fi
echo "완료."
