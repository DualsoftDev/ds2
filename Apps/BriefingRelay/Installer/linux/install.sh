#!/usr/bin/env bash
# ============================================================================
#  BriefingRelay Linux 설치 스크립트 (systemd)
#  - DSPilot 브리핑 중앙 발송 API 서버를 배포·서비스 등록·기동까지 한 번에.
#  - 멱등(idempotent): 재실행 시 업그레이드로 동작하며 자격증명(appsettings.Secrets.json)·포트를 보존한다.
#
#  사용법:
#    sudo ./install.sh [--port N] [--no-firewall]
#
#  기본값: 포트 8088, 방화벽 규칙 자동 추가.
# ============================================================================
set -euo pipefail

# ── 기본 설정 ──────────────────────────────────────────────────────────────
APP_NAME="briefingrelay"
APP_USER="briefingrelay"
INSTALL_DIR="/opt/briefingrelay"
WEB_PORT="8088"
PORT_EXPLICIT=0
ENABLE_FIREWALL=1
SVC="${APP_NAME}.service"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROD_JSON="$INSTALL_DIR/appsettings.Production.json"
SECRETS_JSON="$INSTALL_DIR/appsettings.Secrets.json"
SECRETS_SAMPLE="$INSTALL_DIR/appsettings.Secrets.sample.json"

usage() { sed -n '2,13p' "${BASH_SOURCE[0]}" | sed 's/^#\s\?//'; }

# ── 인자 파싱 ──────────────────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
  case "$1" in
    --port)        WEB_PORT="${2:?--port 값 누락}"; PORT_EXPLICIT=1; shift 2 ;;
    --port=*)      WEB_PORT="${1#*=}"; PORT_EXPLICIT=1; shift ;;
    --no-firewall) ENABLE_FIREWALL=0; shift ;;
    -h|--help)     usage; exit 0 ;;
    *) echo "알 수 없는 인자: $1" >&2; usage; exit 1 ;;
  esac
done

# ── 사전 점검 ──────────────────────────────────────────────────────────────
[[ $EUID -eq 0 ]] || { echo "오류: root 권한이 필요합니다 (sudo ./install.sh)" >&2; exit 1; }
# 존재만 확인(-f). Windows 에서 만든 tarball 은 실행 비트가 안 붙을 수 있어 아래서 chmod +x 로 부여.
[[ -f "$SCRIPT_DIR/app/BriefingRelay" ]] || { echo "오류: $SCRIPT_DIR/app/BriefingRelay 가 없습니다. build-linux.sh 로 만든 패키지에서 실행하세요." >&2; exit 1; }
command -v systemctl >/dev/null || { echo "오류: systemd(systemctl) 가 필요합니다." >&2; exit 1; }
if [[ ! "$WEB_PORT" =~ ^[0-9]+$ ]] || (( WEB_PORT < 1 || WEB_PORT > 65535 )); then
  echo "오류: 포트는 1~65535 범위의 숫자여야 합니다 (입력: $WEB_PORT)" >&2; exit 1
fi

# ── 업그레이드 시 기존 포트 보존(--port 미지정 시) ───────────────────────────
if [[ $PORT_EXPLICIT -eq 0 && -f "$PROD_JSON" ]]; then
  EXIST_PORT="$(grep -oE 'http://[^"]*:[0-9]+' "$PROD_JSON" | grep -oE '[0-9]+$' | head -n1 || true)"
  if [[ -n "${EXIST_PORT:-}" ]]; then
    WEB_PORT="$EXIST_PORT"
    echo "==> 기존 포트 보존: $WEB_PORT (변경하려면 --port)"
  fi
fi

echo "==> BriefingRelay 설치 시작 (포트=$WEB_PORT)"

# ── 1) 서비스 계정 ─────────────────────────────────────────────────────────
if ! id -u "$APP_USER" >/dev/null 2>&1; then
  echo "==> 시스템 사용자 생성: $APP_USER"
  useradd --system --no-create-home --home-dir "$INSTALL_DIR" --shell /usr/sbin/nologin "$APP_USER"
fi

# ── 2) 기존 서비스 중지(업그레이드 시 파일 잠금/포트 해제) ────────────────────
if systemctl list-unit-files "$SVC" >/dev/null 2>&1 && systemctl is-active --quiet "$SVC"; then
  echo "==> 기존 서비스 중지: $SVC"
  systemctl stop "$SVC" || true
fi

# ── 3) 앱 파일 배치 ──────────────────────────────────────────────────────────
echo "==> 앱 파일 복사: $INSTALL_DIR"
mkdir -p "$INSTALL_DIR"
# 자격증명(appsettings.Secrets.json)은 업그레이드 시 보존해야 하므로 페이로드에 없다(패키지엔 .sample 만).
cp -a "$SCRIPT_DIR/app/." "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/BriefingRelay"

# ── 4) 포트 오버라이드 (appsettings.Production.json) ─────────────────────────
# ASPNETCORE_ENVIRONMENT=Production 이라 이 파일이 자동 로드되어 appsettings.json 의 Kestrel 포트를 덮는다.
cat > "$PROD_JSON" <<EOF
{
  "Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:$WEB_PORT" } } }
}
EOF

# ── 5) 자격증명 파일 처리 (없으면 sample 에서 생성, 있으면 보존) ─────────────
SECRETS_PLACEHOLDER=0
if [[ ! -f "$SECRETS_JSON" ]]; then
  if [[ -f "$SECRETS_SAMPLE" ]]; then
    echo "==> appsettings.Secrets.json 생성 (sample 복사 — 값 입력 필요)"
    cp -a "$SECRETS_SAMPLE" "$SECRETS_JSON"
    SECRETS_PLACEHOLDER=1
  fi
else
  echo "==> 기존 appsettings.Secrets.json 보존"
fi
# 미입력(플레이스홀더 '<...>') 감지
if [[ -f "$SECRETS_JSON" ]] && grep -q '<' "$SECRETS_JSON"; then
  SECRETS_PLACEHOLDER=1
fi

# ── 6) 소유권/권한 (전용 서비스 계정, 시크릿 600) ────────────────────────────
chown -R "$APP_USER:$APP_USER" "$INSTALL_DIR"
[[ -f "$SECRETS_JSON" ]] && chmod 600 "$SECRETS_JSON"

# ── 7) systemd 유닛 설치 (템플릿 치환) ───────────────────────────────────────
# 1024 미만 포트는 비-root 바인딩에 CAP_NET_BIND_SERVICE 필요.
if (( WEB_PORT < 1024 )); then
  CAP_BLOCK=$'AmbientCapabilities=CAP_NET_BIND_SERVICE\nCapabilityBoundingSet=CAP_NET_BIND_SERVICE'
else
  CAP_BLOCK=""
fi
echo "==> systemd 유닛 설치: /etc/systemd/system/$SVC"
sed -e "s|@USER@|$APP_USER|g" \
    -e "s|@WORKDIR@|$INSTALL_DIR|g" \
    -e "s|@EXEC@|$INSTALL_DIR/BriefingRelay|g" \
    -e "s|@AMBIENT_CAP@|$CAP_BLOCK|g" \
    "$SCRIPT_DIR/systemd/briefingrelay.service" > "/etc/systemd/system/$SVC"

# ── 8) 방화벽 (ufw / firewalld 자동 감지, best-effort) ───────────────────────
if [[ $ENABLE_FIREWALL -eq 1 ]]; then
  if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -qi active; then
    echo "==> ufw 방화벽 규칙 추가: ${WEB_PORT}/tcp"
    ufw allow "${WEB_PORT}/tcp" >/dev/null || true
  elif command -v firewall-cmd >/dev/null && firewall-cmd --state >/dev/null 2>&1; then
    echo "==> firewalld 방화벽 규칙 추가: ${WEB_PORT}/tcp"
    firewall-cmd --permanent --add-port="${WEB_PORT}/tcp" >/dev/null || true
    firewall-cmd --reload >/dev/null || true
  else
    echo "==> 활성 방화벽 미감지 — 규칙 건너뜀(필요 시 수동 개방: ${WEB_PORT}/tcp)"
  fi
fi

# ── 9) 서비스 등록 + 기동 ────────────────────────────────────────────────────
echo "==> systemd 재로딩 및 서비스 기동"
systemctl daemon-reload
systemctl enable --now "$SVC"

sleep 2
echo ""
echo "============================================================"
systemctl --no-pager --lines=0 status "$SVC" || true
echo "============================================================"

# ── 10) 헬스체크 ─────────────────────────────────────────────────────────────
HEALTH_OK=0
if command -v curl >/dev/null; then
  for _ in $(seq 1 15); do
    if curl -fsS "http://127.0.0.1:$WEB_PORT/healthz" >/dev/null 2>&1; then HEALTH_OK=1; break; fi
    sleep 1
  done
fi

IP_HINT="$(hostname -I 2>/dev/null | awk '{print $1}')"
echo ""
if [[ $HEALTH_OK -eq 1 ]]; then
  echo "✅ 서비스 기동 완료 (healthz OK)"
else
  echo "⚠️  healthz 응답 없음 — 로그 확인: journalctl -u $SVC -e"
fi
echo "엔드포인트:"
echo "    http://localhost:$WEB_PORT/healthz"
[[ -n "${IP_HINT:-}" ]] && echo "    http://$IP_HINT:$WEB_PORT/healthz"
echo "    POST /api/briefing/send  (헤더 X-Api-Key)"
echo ""
if [[ $SECRETS_PLACEHOLDER -eq 1 ]]; then
  echo "‼️  자격증명 미입력: $SECRETS_JSON 을 편집(OAuth 값)한 뒤 재시작하세요:"
  echo "      sudo nano $SECRETS_JSON"
  echo "      sudo systemctl restart $SVC"
  echo "    (값 형식은 SETUP-O365.md 의 STEP 6 참고)"
fi
echo "로그:   journalctl -u $SVC -f"
echo "제거:   sudo ./uninstall.sh"
