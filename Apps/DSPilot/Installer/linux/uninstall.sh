#!/usr/bin/env bash
# ============================================================================
#  DSPilot Linux 제거 스크립트
#    sudo ./uninstall.sh [--purge]
#  기본: 서비스/바이너리 제거, 사용자 데이터(공유 폴더)는 보존.
#  --purge: 공유 폴더(/var/lib/dualsoft/Shared, DB/AASX 포함)·SSOT env 파일·서비스 계정까지 삭제.
# ============================================================================
set -euo pipefail

APP_NAME="dspilot"
APP_USER="dspilot"
INSTALL_DIR="/opt/dspilot"
ENV_FILE="/etc/dualsoft/dualsoft.env"
# 공유 폴더는 SSOT(env 파일)에 기록된 실제 경로를 우선 사용 — install.sh 가 --shared-dir 로 바꿨을 수 있다.
# env 파일이 없으면 코드 기본값(대문자 Shared)으로 폴백.
SHARED_DIR="/var/lib/dualsoft/Shared"
COLLECTOR_DATA_DIR="/var/lib/dualsoft/collector"
SVC_DSPILOT="${APP_NAME}.service"
SVC_MEDIAMTX="${APP_NAME}-mediamtx.service"
SVC_AGENT="promaker-agent.service"
SVC_COLLECTOR="ds2-collector.service"
PURGE=0

[[ "${1:-}" == "--purge" ]] && PURGE=1
[[ $EUID -eq 0 ]] || { echo "오류: root 권한이 필요합니다 (sudo ./uninstall.sh)" >&2; exit 1; }

if [[ -f "$ENV_FILE" ]]; then
  EXIST_SHARED="$(grep -oE '^DUALSOFT_SHARED_DIR=.*' "$ENV_FILE" | head -n1 | cut -d= -f2- || true)"
  [[ -n "${EXIST_SHARED:-}" ]] && SHARED_DIR="$EXIST_SHARED"
fi

for svc in "$SVC_DSPILOT" "$SVC_MEDIAMTX" "$SVC_COLLECTOR" "$SVC_AGENT"; do
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
  echo "==> [purge] Collector 이력/인증서 삭제: $COLLECTOR_DATA_DIR"
  rm -rf "$COLLECTOR_DATA_DIR"
  echo "==> [purge] 공유 설정(SSOT) 삭제: $ENV_FILE"
  rm -f "$ENV_FILE"
  rmdir "$(dirname "$ENV_FILE")" 2>/dev/null || true   # /etc/dualsoft 가 비면 정리(다른 파일 있으면 보존)
  if id -u "$APP_USER" >/dev/null 2>&1; then
    echo "==> [purge] 서비스 계정 삭제: $APP_USER"
    userdel "$APP_USER" 2>/dev/null || true
  fi
else
  echo "==> 사용자 데이터 보존: $SHARED_DIR, $COLLECTOR_DATA_DIR (완전 삭제는 --purge)"
fi

echo "제거 완료. (방화벽 규칙은 자동 삭제하지 않음 — 필요 시 수동 제거)"
