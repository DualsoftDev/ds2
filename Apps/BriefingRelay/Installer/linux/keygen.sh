#!/usr/bin/env bash
# ============================================================================
#  BriefingRelay API 키 생성/등록 (릴레이 서버에서 실행)
#  - 강한 랜덤 키를 생성해 appsettings.Secrets.json 의 Relay.ApiKeys 에 등록하고 서비스 재시작.
#  - 출력된 키를 DSPilot 빌드머신의 Installer/briefing-apikey.txt 에 넣고 재빌드하면 설치본이 이 키로 연결.
#  - install.sh(배포)와 분리 — 키가 필요할 때만 이 스크립트를 실행한다.
#
#  사용법:
#    sudo ./keygen.sh [이름] [일일쿼터]        # 새 키를 '추가'(기존 키 유지 — 무중단 로테이션)
#    sudo REPLACE=1 ./keygen.sh [이름] [쿼터]  # 기존 키 전부 지우고 이 키 하나로 교체
#    sudo ./keygen.sh --list                   # 등록된 키 목록(이름/쿼터/상태) 보기
#
#  기본: 이름=dspilot-<날짜>, 쿼터=200/일. 플레이스홀더/빈 키는 자동 정리.
# ============================================================================
set -euo pipefail

SECRETS="/opt/briefingrelay/appsettings.Secrets.json"
SVC="briefingrelay.service"
APP_USER="briefingrelay"

[[ $EUID -eq 0 ]] || { echo "오류: root 권한 필요 (sudo ./keygen.sh)" >&2; exit 1; }
[[ -f "$SECRETS" ]] || { echo "오류: $SECRETS 없음 — 먼저 install.sh 실행 + OAuth 값 입력." >&2; exit 1; }

# JSON 편집기 선택(python3 우선, 없으면 jq).
JSON_TOOL=""
command -v python3 >/dev/null && JSON_TOOL="python3"
[[ -z "$JSON_TOOL" ]] && command -v jq >/dev/null && JSON_TOOL="jq"
[[ -n "$JSON_TOOL" ]] || { echo "오류: python3 또는 jq 필요." >&2; exit 1; }

# ── --list: 등록된 키 요약 ──
if [[ "${1:-}" == "--list" ]]; then
  if [[ "$JSON_TOOL" == "python3" ]]; then
    python3 - "$SECRETS" <<'PY'
import json,sys
d=json.load(open(sys.argv[1],encoding='utf-8'))
ks=d.get("Relay",{}).get("ApiKeys",[])
if not ks: print("(등록된 키 없음)")
for e in ks:
    k=e.get("Key",""); mask=(k[:6]+"…"+k[-4:]) if len(k)>12 else k
    print(f"- name={e.get('Name','')!r} quota={e.get('DailyQuota')} disabled={e.get('Disabled',False)} key={mask}")
PY
  else
    jq -r '.Relay.ApiKeys[]? | "- name=\(.Name) quota=\(.DailyQuota) disabled=\(.Disabled) key=\(.Key[0:6])…"' "$SECRETS"
  fi
  exit 0
fi

command -v openssl >/dev/null || { echo "오류: openssl 필요." >&2; exit 1; }

NAME="${1:-dspilot-$(date +%Y%m%d)}"
QUOTA="${2:-200}"
REPLACE="${REPLACE:-0}"
KEY="$(openssl rand -hex 32)"

# 백업 후 편집
cp -a "$SECRETS" "$SECRETS.bak.$(date +%Y%m%d%H%M%S)"

if [[ "$JSON_TOOL" == "python3" ]]; then
  python3 - "$SECRETS" "$KEY" "$NAME" "$QUOTA" "$REPLACE" <<'PY'
import json,sys
p,k,n,q,repl=sys.argv[1:6]
d=json.load(open(p,encoding='utf-8'))
r=d.setdefault("Relay",{})
keys=r.get("ApiKeys",[]) if repl!="1" else []
# 플레이스홀더('<...>')·빈 키 제거
keys=[e for e in keys if isinstance(e,dict) and e.get("Key") and "<" not in e.get("Key","")]
keys.append({"Key":k,"Name":n,"DailyQuota":int(q),"AllowedDomains":[],"Disabled":False})
r["ApiKeys"]=keys
json.dump(d,open(p,"w",encoding='utf-8'),indent=2,ensure_ascii=False)
PY
else
  tmp="$(mktemp)"
  base='.Relay.ApiKeys'
  if [[ "$REPLACE" == "1" ]]; then
    jq --arg k "$KEY" --arg n "$NAME" --argjson q "$QUOTA" \
       '.Relay.ApiKeys = [{"Key":$k,"Name":$n,"DailyQuota":$q,"AllowedDomains":[],"Disabled":false}]' "$SECRETS" > "$tmp"
  else
    jq --arg k "$KEY" --arg n "$NAME" --argjson q "$QUOTA" \
       '.Relay.ApiKeys = ((.Relay.ApiKeys // []) | map(select(.Key and (.Key|contains("<")|not)))) + [{"Key":$k,"Name":$n,"DailyQuota":$q,"AllowedDomains":[],"Disabled":false}]' "$SECRETS" > "$tmp"
  fi
  mv "$tmp" "$SECRETS"
fi

chmod 600 "$SECRETS"
chown "$APP_USER:$APP_USER" "$SECRETS" 2>/dev/null || true
systemctl restart "$SVC"

echo "============================================================"
echo " 새 API 키 등록·서비스 재시작 완료"
echo "   이름   : $NAME"
echo "   쿼터   : $QUOTA 건/일"
echo "   모드   : $([[ "$REPLACE" == "1" ]] && echo '교체(기존 키 삭제)' || echo '추가(기존 키 유지)')"
echo "============================================================"
echo ""
echo "  API KEY ▶  $KEY"
echo ""
echo " 다음: 이 키를 DSPilot 빌드머신에 넣고 재빌드"
echo "   1) Apps/DSPilot/Installer/briefing-apikey.txt  = 위 키 (한 줄, 공백/개행 없이)"
echo "   2) build-installer.bat 재실행 → 이 설치본부터 새 키로 연결"
echo " (디버그 테스트하려면 dev 의 DSPilot/appsettings.Secrets.json 의 ApiKey 도 위 키로)"
echo ""
echo " ⚠ 이 키는 지금만 표시됩니다. 안전한 곳에 보관하세요."
