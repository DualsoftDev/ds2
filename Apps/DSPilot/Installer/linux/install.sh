#!/usr/bin/env bash
# ============================================================================
#  DSPilot Linux 설치 스크립트 (systemd)
#  - Windows 의 Inno Setup(DSPilot.iss) + sc.exe + WinSW + netsh 대응물.
#  - 멱등(idempotent): 재실행 시 업그레이드로 동작하며 사용자 설정/데이터를 보존한다.
#
#  사용법:
#    sudo ./install.sh [--port N] [--shared-dir PATH] [--no-cctv] [--no-firewall] [--no-agent|--with-agent]
#
#  기본값: 포트 8080, 공유 디렉터리 /var/lib/dualsoft/Shared, CCTV/방화벽/Agent 활성.
#  --no-agent 로 Promaker.Agent(PLC 스캔 백엔드) 제외, --with-agent 로 명시 포함(기본값).
# ============================================================================
set -euo pipefail

# ── 기본 설정 ──────────────────────────────────────────────────────────────
APP_NAME="dspilot"
APP_USER="dspilot"
INSTALL_DIR="/opt/dspilot"
# 공유 디렉터리 기본값(대문자 Shared) — DSPilot/Promaker 양쪽 SharedPaths 의 Linux 기본값과
# 대소문자까지 동일해야 한다. (Linux 는 경로 대소문자 구분 → 한 글자라도 다르면 다른 폴더가 되어 공유가 깨진다.)
SHARED_DIR="/var/lib/dualsoft/Shared"
SHARED_DIR_EXPLICIT=0
# 공유 디렉터리 단일 출처(SSOT). DSPilot·Promaker.Agent 의 systemd 유닛이 이 파일을 EnvironmentFile 로
# 함께 읽어 항상 같은 폴더를 본다 — 경로를 바꾸려면 이 값 한 줄만 고치고 install.sh 를 재실행한다.
ENV_FILE="/etc/dualsoft/dualsoft.env"
WEB_PORT="8080"
PORT_EXPLICIT=0
ENABLE_CCTV=1
ENABLE_FIREWALL=1
# Agent = DSPilot 의 데이터 공급원(PLC 스캔 → 5051 SignalR Hub, DSPilot 가 구독). Linux 는 기본 ON.
ENABLE_AGENT=1
AGENT_EXPLICIT=0
# CCTV WebRTC 포트 (mediamtx.yml 과 일치해야 함): 8889/tcp=WHEP·시그널링, 8189/udp=ICE 미디어, 8189/tcp=UDP 차단망 폴백.
WEBRTC_TCP_PORT=8889
WEBRTC_UDP_PORT=8189
AGENT_PORT=5051          # Promaker.Agent SignalR Hub (모니터링 active 시, DSPilot 구독)
AGENT_UPLOAD_PORT=5050   # 모델 업로드 수신 (항상 listen — Promaker '네트워크 업로드' 대상)

SVC_DSPILOT="${APP_NAME}.service"
SVC_MEDIAMTX="${APP_NAME}-mediamtx.service"
SVC_AGENT="promaker-agent.service"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() {
  sed -n '2,13p' "${BASH_SOURCE[0]}" | sed 's/^#\s\?//'
}

# ── 인자 파싱 ──────────────────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
  case "$1" in
    --port)        WEB_PORT="${2:?--port 값 누락}"; PORT_EXPLICIT=1; shift 2 ;;
    --port=*)      WEB_PORT="${1#*=}"; PORT_EXPLICIT=1; shift ;;
    --shared-dir)  SHARED_DIR="${2:?--shared-dir 값 누락}"; SHARED_DIR_EXPLICIT=1; shift 2 ;;
    --shared-dir=*) SHARED_DIR="${1#*=}"; SHARED_DIR_EXPLICIT=1; shift ;;
    --no-cctv)     ENABLE_CCTV=0; shift ;;
    --no-firewall) ENABLE_FIREWALL=0; shift ;;
    --no-agent)    ENABLE_AGENT=0; AGENT_EXPLICIT=1; shift ;;
    --with-agent)  ENABLE_AGENT=1; AGENT_EXPLICIT=1; shift ;;
    -h|--help)     usage; exit 0 ;;
    *) echo "알 수 없는 인자: $1" >&2; usage; exit 1 ;;
  esac
done

# ── 사전 점검 ──────────────────────────────────────────────────────────────
[[ $EUID -eq 0 ]] || { echo "오류: root 권한이 필요합니다 (sudo ./install.sh)" >&2; exit 1; }
# 존재만 확인(-f). Windows 에서 만든 tarball 은 실행 비트(+x)가 보존되지 않을 수 있으므로 -x 로 보지 않는다.
# 설치 단계에서 chmod +x 로 권한을 직접 부여한다.
[[ -f "$SCRIPT_DIR/app/DSPilot" ]] || { echo "오류: $SCRIPT_DIR/app/DSPilot 파일이 없습니다. build-linux.sh 로 만든 패키지에서 실행하세요." >&2; exit 1; }
command -v systemctl >/dev/null || { echo "오류: systemd(systemctl) 가 필요합니다." >&2; exit 1; }

if [[ ! "$WEB_PORT" =~ ^[0-9]+$ ]] || (( WEB_PORT < 1 || WEB_PORT > 65535 )); then
  echo "오류: 포트는 1~65535 범위의 숫자여야 합니다 (입력: $WEB_PORT)" >&2; exit 1
fi

# ── 업그레이드 시 기존 공유 디렉터리 보존(--shared-dir 미지정 시) ────────────
# SSOT(env 파일)에 이전 설치가 기록한 경로가 있으면 그대로 사용 — 기본값(대소문자)이 바뀌어도
# 기존 데이터 폴더를 그대로 가리켜 plc.db/oee.db/project.aasx 가 고아가 되지 않게 한다.
if [[ $SHARED_DIR_EXPLICIT -eq 0 && -f "$ENV_FILE" ]]; then
  EXIST_SHARED="$(grep -oE '^DUALSOFT_SHARED_DIR=.*' "$ENV_FILE" | head -n1 | cut -d= -f2- || true)"
  if [[ -n "${EXIST_SHARED:-}" ]]; then
    SHARED_DIR="$EXIST_SHARED"
    echo "==> 기존 공유 디렉터리 보존: $SHARED_DIR (변경하려면 --shared-dir)"
  fi
fi

# Agent 옵션 정리 — 켜져 있으나 패키지에 Agent 바이너리가 없으면 자동 스킵(빌드 시 미동봉).
if [[ $ENABLE_AGENT -eq 1 && ! -f "$SCRIPT_DIR/agent/Promaker.Agent" ]]; then
  [[ $AGENT_EXPLICIT -eq 1 ]] && echo "경고: --with-agent 지정됐으나 패키지에 agent/Promaker.Agent 가 없어 Agent 를 건너뜁니다."
  ENABLE_AGENT=0
fi

echo "==> DSPilot 설치 시작 (포트=$WEB_PORT, 공유=$SHARED_DIR, CCTV=$ENABLE_CCTV, Agent=$ENABLE_AGENT)"

# ── libicu 안내(한글/문화권 정렬에 필요. self-contained 라도 ICU 자체는 시스템 의존) ──
if ! ldconfig -p 2>/dev/null | grep -qi 'libicu'; then
  echo "경고: libicu 가 감지되지 않았습니다. 한글 정렬/문화권 처리를 위해 설치를 권장합니다."
  echo "      (Debian/Ubuntu: apt-get install -y libicu) (RHEL/Rocky: dnf install -y libicu)"
fi

# ── ffmpeg(CCTV 스냅샷 API 가 PATH 의 ffmpeg 로 1프레임 그랩. 패키지에 미동봉) ──────
# 온라인 환경 가정으로 패키지 매니저 자동 설치를 시도하고, 실패(오프라인/프록시/저장소 정책)해도
# 경고만 남기고 나머지 설치는 계속한다 — ffmpeg 부재 시 스냅샷 API 만 실패, 스트리밍은 무관.
if [[ $ENABLE_CCTV -eq 1 ]] && ! command -v ffmpeg >/dev/null 2>&1; then
  echo "==> ffmpeg 미감지 — 자동 설치 시도 (CCTV 스냅샷 API 용)"
  FFMPEG_OK=0
  if command -v apt-get >/dev/null 2>&1; then
    { apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq ffmpeg; } && FFMPEG_OK=1 || true
  elif command -v dnf >/dev/null 2>&1; then
    dnf install -y -q ffmpeg && FFMPEG_OK=1 || true
  elif command -v yum >/dev/null 2>&1; then
    yum install -y -q ffmpeg && FFMPEG_OK=1 || true
  fi
  if [[ $FFMPEG_OK -eq 1 ]] && command -v ffmpeg >/dev/null 2>&1; then
    echo "    ffmpeg 설치 완료: $(command -v ffmpeg)"
  else
    echo "경고: ffmpeg 자동 설치에 실패했습니다. CCTV 스냅샷 API 가 동작하지 않습니다(스트리밍은 무관)."
    echo "      수동 설치 후 재기동 불필요 — (Debian/Ubuntu: apt-get install -y ffmpeg) (RHEL/Rocky: dnf install -y ffmpeg)"
  fi
fi

# ── 1) 서비스 계정 ─────────────────────────────────────────────────────────
if ! id -u "$APP_USER" >/dev/null 2>&1; then
  echo "==> 시스템 사용자 생성: $APP_USER"
  useradd --system --no-create-home --home-dir "$INSTALL_DIR" --shell /usr/sbin/nologin "$APP_USER"
fi

# ── 2) 기존 서비스 중지(업그레이드 시 파일 잠금/포트 해제) ────────────────────
for svc in "$SVC_DSPILOT" "$SVC_MEDIAMTX" "$SVC_AGENT"; do
  if systemctl list-unit-files "$svc" >/dev/null 2>&1 && systemctl is-active --quiet "$svc"; then
    echo "==> 기존 서비스 중지: $svc"
    systemctl stop "$svc" || true
  fi
done

# ── 3) 업그레이드 시 기존 포트 보존(--port 미지정 시) ────────────────────────
HOSTING_JSON="$INSTALL_DIR/appsettings.Hosting.json"
if [[ $PORT_EXPLICIT -eq 0 && -f "$HOSTING_JSON" ]]; then
  EXIST_PORT="$(grep -oE 'http://[^"]*:[0-9]+' "$HOSTING_JSON" | grep -oE '[0-9]+$' | head -n1 || true)"
  if [[ -n "${EXIST_PORT:-}" ]]; then
    WEB_PORT="$EXIST_PORT"
    echo "==> 기존 설치의 포트 보존: $WEB_PORT (변경하려면 --port 사용)"
  fi
fi

# ── 4) 디렉터리 + 앱 파일 배치 ───────────────────────────────────────────────
echo "==> 앱 파일 복사: $INSTALL_DIR"
# 공유 디렉터리 + Agent 하위(active.flag/session.json)까지 미리 생성 — 아래서 서비스 계정 소유권/권한 부여.
mkdir -p "$INSTALL_DIR" "$SHARED_DIR" "$SHARED_DIR/agent" "$INSTALL_DIR/logs"
# 앱 페이로드(자체 포함 런타임 + wwwroot). 기존 appsettings.Production.json(사용자 설정)·uploads(도면/오버레이)는
# 덮어쓰지 않도록 app/ 에 포함하지 않는다(build-linux.sh 가 publish 산출물만 담음).
cp -a "$SCRIPT_DIR/app/." "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/DSPilot"

# ── 4b) Promaker.Agent 파일 배치 (옵션, Linux 기본 ON) ───────────────────────
# Agent 는 별도 폴더 {INSTALL_DIR}/agent 로 분리(DSPilot 바이너리와 충돌 방지 + 로그 격리).
AGENT_DIR="$INSTALL_DIR/agent"
if [[ $ENABLE_AGENT -eq 1 ]]; then
  echo "==> Promaker.Agent 파일 복사: $AGENT_DIR"
  mkdir -p "$AGENT_DIR"
  cp -a "$SCRIPT_DIR/agent/." "$AGENT_DIR/"
  chmod +x "$AGENT_DIR/Promaker.Agent"
fi

# ── 5) 포트 기록(appsettings.Hosting.json) — Program.cs 가 명시 로드(AddJsonFile, 최우선). ──
#     사용자 설정 저장소(appsettings.Production.json)와 분리해 보존과 포트 갱신 충돌을 막는다.
cat > "$HOSTING_JSON" <<EOF
{
  "Urls": "http://*:$WEB_PORT"
}
EOF

# ── 6) CCTV(MediaMTX) 파일 배치 ──────────────────────────────────────────────
MTX_DIR="$INSTALL_DIR/mediamtx"
if [[ $ENABLE_CCTV -eq 1 ]]; then
  if [[ -f "$SCRIPT_DIR/mediamtx/mediamtx" ]]; then
    echo "==> MediaMTX 파일 복사: $MTX_DIR"
    mkdir -p "$MTX_DIR"
    cp -a "$SCRIPT_DIR/mediamtx/mediamtx" "$MTX_DIR/"
    chmod +x "$MTX_DIR/mediamtx"
    cp -a "$SCRIPT_DIR"/mediamtx/LICENSE* "$MTX_DIR/" 2>/dev/null || true
    # mediamtx.yml: 운영자가 손볼 수 있으므로 이미 있으면 덮어쓰지 않는다(Windows onlyifdoesntexist 와 동일).
    [[ -f "$MTX_DIR/mediamtx.yml" ]] || cp -a "$SCRIPT_DIR/mediamtx/mediamtx.yml" "$MTX_DIR/"
  else
    echo "경고: mediamtx 바이너리가 패키지에 없어 CCTV 를 건너뜁니다. (--no-cctv 로 무시 가능)"
    ENABLE_CCTV=0
  fi
fi

# ── 7) 소유권 + 권한(전용 서비스 계정) ───────────────────────────────────────
# SHARED_DIR 은 Agent·DSPilot(둘 다 $APP_USER 로 실행)이 함께 읽기/쓰기 한다. 소유권을 서비스 계정에
# 주고 소유자 쓰기/디렉터리 traverse 권한을 명시(umask 영향 제거). 부모(/var/lib/dualsoft)는 root 755 라
# 서비스 계정이 traverse 가능. (코드 SharedPaths 기본값과 동일 경로 → 환경변수 없이도 권한 정합.)
chown -R "$APP_USER:$APP_USER" "$INSTALL_DIR" "$SHARED_DIR"
chmod -R u+rwX "$SHARED_DIR"

# ── 7b) 공유 디렉터리 단일 출처(SSOT) 기록 ───────────────────────────────────
# DSPilot·Promaker.Agent 의 systemd 유닛이 EnvironmentFile 로 이 파일을 읽어 동일 폴더로 정합된다.
mkdir -p "$(dirname "$ENV_FILE")"
cat > "$ENV_FILE" <<EOF
# DualSoft 공유 런타임 디렉터리(단일 출처). DSPilot·Promaker.Agent 가 project.aasx / plc.db / oee.db /
# PlcConnection.json / agent/active.flag 를 주고받는 폴더. 두 서비스가 이 파일을 EnvironmentFile 로 읽어
# 항상 같은 경로를 본다 — 경로를 바꾸려면 이 값 한 줄만 고치고 install.sh 를 재실행한다.
DUALSOFT_SHARED_DIR=$SHARED_DIR
EOF
chmod 0644 "$ENV_FILE"

# ── 8) systemd 유닛 생성(템플릿 치환) ────────────────────────────────────────
# 80 등 1024 미만 포트는 비-root 바인딩에 CAP_NET_BIND_SERVICE 가 필요하다.
if (( WEB_PORT < 1024 )); then
  CAP_BLOCK=$'AmbientCapabilities=CAP_NET_BIND_SERVICE\nCapabilityBoundingSet=CAP_NET_BIND_SERVICE'
else
  CAP_BLOCK=""
fi

echo "==> systemd 유닛 설치: /etc/systemd/system/$SVC_DSPILOT"
sed -e "s|@USER@|$APP_USER|g" \
    -e "s|@WORKDIR@|$INSTALL_DIR|g" \
    -e "s|@EXEC@|$INSTALL_DIR/DSPilot|g" \
    -e "s|@AMBIENT_CAP@|$CAP_BLOCK|g" \
    "$SCRIPT_DIR/systemd/dspilot.service" > "/etc/systemd/system/$SVC_DSPILOT"

if [[ $ENABLE_CCTV -eq 1 ]]; then
  echo "==> systemd 유닛 설치: /etc/systemd/system/$SVC_MEDIAMTX"
  sed -e "s|@USER@|$APP_USER|g" \
      -e "s|@MTX_WORKDIR@|$MTX_DIR|g" \
      -e "s|@MTX_EXEC@|$MTX_DIR/mediamtx|g" \
      -e "s|@MTX_CONFIG@|$MTX_DIR/mediamtx.yml|g" \
      "$SCRIPT_DIR/systemd/mediamtx.service" > "/etc/systemd/system/$SVC_MEDIAMTX"
else
  # CCTV 비활성 — DSPilot 유닛의 mediamtx 의존성 제거(없는 서비스 대기 방지).
  sed -i "s| $SVC_MEDIAMTX||" "/etc/systemd/system/$SVC_DSPILOT"
fi

if [[ $ENABLE_AGENT -eq 1 ]]; then
  echo "==> systemd 유닛 설치: /etc/systemd/system/$SVC_AGENT"
  # Agent 유닛도 동일 SSOT(env 파일)를 EnvironmentFile 로 읽는다 → DSPilot 과 같은 공유 폴더 정합.
  sed -e "s|@USER@|$APP_USER|g" \
      -e "s|@AGENT_WORKDIR@|$AGENT_DIR|g" \
      -e "s|@AGENT_EXEC@|$AGENT_DIR/Promaker.Agent|g" \
      "$SCRIPT_DIR/systemd/promaker-agent.service" > "/etc/systemd/system/$SVC_AGENT"
fi

# ── 9) 방화벽(ufw / firewalld 자동 감지, best-effort) ────────────────────────
if [[ $ENABLE_FIREWALL -eq 1 ]]; then
  if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -qi active; then
    echo "==> ufw 방화벽 규칙 추가"
    ufw allow "${WEB_PORT}/tcp" >/dev/null || true
    [[ $ENABLE_AGENT -eq 1 ]] && { ufw allow "${AGENT_PORT}/tcp" >/dev/null || true; ufw allow "${AGENT_UPLOAD_PORT}/tcp" >/dev/null || true; }
    if [[ $ENABLE_CCTV -eq 1 ]]; then
      ufw allow "${WEBRTC_TCP_PORT}/tcp" >/dev/null || true
      ufw allow "${WEBRTC_UDP_PORT}/udp" >/dev/null || true
      ufw allow "${WEBRTC_UDP_PORT}/tcp" >/dev/null || true
    fi
  elif command -v firewall-cmd >/dev/null && firewall-cmd --state >/dev/null 2>&1; then
    echo "==> firewalld 방화벽 규칙 추가"
    firewall-cmd --permanent --add-port="${WEB_PORT}/tcp" >/dev/null || true
    [[ $ENABLE_AGENT -eq 1 ]] && { firewall-cmd --permanent --add-port="${AGENT_PORT}/tcp" >/dev/null || true; firewall-cmd --permanent --add-port="${AGENT_UPLOAD_PORT}/tcp" >/dev/null || true; }
    if [[ $ENABLE_CCTV -eq 1 ]]; then
      firewall-cmd --permanent --add-port="${WEBRTC_TCP_PORT}/tcp" >/dev/null || true
      firewall-cmd --permanent --add-port="${WEBRTC_UDP_PORT}/udp" >/dev/null || true
      firewall-cmd --permanent --add-port="${WEBRTC_UDP_PORT}/tcp" >/dev/null || true
    fi
    firewall-cmd --reload >/dev/null || true
  else
    echo "==> 활성 방화벽(ufw/firewalld) 미감지 — 규칙 추가 건너뜀(필요 시 수동 개방: ${WEB_PORT}/tcp)"
  fi
fi

# ── 10) 서비스 등록 + 기동 ──────────────────────────────────────────────────
echo "==> systemd 재로딩 및 서비스 기동"
systemctl daemon-reload
if [[ $ENABLE_CCTV -eq 1 ]]; then
  systemctl enable --now "$SVC_MEDIAMTX"
fi
if [[ $ENABLE_AGENT -eq 1 ]]; then
  systemctl enable --now "$SVC_AGENT"
fi
systemctl enable --now "$SVC_DSPILOT"

sleep 2
echo ""
echo "============================================================"
systemctl --no-pager --lines=0 status "$SVC_DSPILOT" || true
[[ $ENABLE_AGENT -eq 1 ]] && { echo "------------------------------------------------------------"; systemctl --no-pager --lines=0 status "$SVC_AGENT" || true; }
echo "============================================================"
IP_HINT="$(hostname -I 2>/dev/null | awk '{print $1}')"
echo "설치 완료. 웹 대시보드:"
echo "    http://localhost:$WEB_PORT"
[[ -n "${IP_HINT:-}" ]] && echo "    http://$IP_HINT:$WEB_PORT"
echo ""
echo "로그 보기:   journalctl -u $SVC_DSPILOT -f"
[[ $ENABLE_AGENT -eq 1 ]] && echo "Agent:       업로드 수신 :$AGENT_UPLOAD_PORT (항상) / Hub :$AGENT_PORT (모니터링 중) — 로그 journalctl -u $SVC_AGENT -f"
echo "공유 폴더:   $SHARED_DIR  (project.aasx / plc.db / oee.db / PlcConnection.json / active.flag)"
echo "제거:        sudo ./uninstall.sh"
