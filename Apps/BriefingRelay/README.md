# BriefingRelay — DSPilot 브리핑 중앙 발송 API

고객사 DSPilot 설치본이 이 API를 호출하면, 이 서버가 **회사 O365 계정으로 대신 메일을 발송**합니다.
메일 자격증명(비밀번호)은 **이 서버에만** 존재하고 고객 PC에는 없습니다. 고객 PC에는 저위험 **API 키**만 둡니다.

```
[고객사 DSPilot] --HTTPS + X-Api-Key--> [BriefingRelay (회사 Linux 서버)] --O365--> 수신자
```

## 엔드포인트

| 메서드 | 경로 | 설명 |
|---|---|---|
| GET | `/healthz` | 상태 확인(인증 불요) |
| POST | `/api/briefing/send` | 발송. 헤더 `X-Api-Key` 필수. 본문 `{ subject, html, recipients[] }` |

응답: `{ "sent": bool, "recipientCount": int, "message": string }`
상태코드: 200 성공 / 400 입력오류 / 401 인증실패 / 403 도메인차단 / 429 쿼터초과 / 502 발송실패 / 503 서버 미구성.

## v1 보안 (구현됨 — 오픈 릴레이 방지)

- **API 키 인증**: `X-Api-Key` 헤더, 상수시간 비교. 키 없거나 무효 → 401. 서버에 키 0개면 전부 거부(fail-closed).
- **키별 일일 쿼터**: 초과 시 429(인메모리 카운터 — 재시작 시 리셋).
- **입력 검증**: 수신자 수 상한, 본문 크기 상한, 주소 형식 검증.
- **수신 도메인 화이트리스트**(키별 선택): `AllowedDomains` 설정 시 그 도메인만 허용.
- **키 비활성화**: `Disabled: true` 로 재발급 없이 즉시 차단.
- **From 고정**: 발신 주소는 서버 설정값 — 호출자가 임의 From 을 지정할 수 없음.

## O365 인증 모드 (`Relay:AuthMode`)

- **`oauth`(권장)**: Azure AD 앱 전용(client credentials) → Graph `sendMail`. **보안 기본값/MFA 환경에서도 동작**(basic 이 막힌 테넌트의 유일한 길). 필요: 앱 등록 + `Mail.Send` 앱권한 + 관리자 동의 + 클라이언트 비밀. 발송 주체(`OAuth:Sender`)는 **비관리자 사서함**(가능하면 라이선스 불필요한 **공유 사서함**) 권장 + `ApplicationAccessPolicy` 로 그 사서함만 허용.
- **`basic`(비권장)**: SMTP 계정/비번. 테넌트에 보안 기본값이 켜져 있으면 `535 ... security defaults policy` 로 막힘.

Azure AD 앱 등록(관리자 1회):
1. portal.azure.com → Microsoft Entra ID → 앱 등록 → 새 등록 → **클라이언트/테넌트 ID** 기록
2. 인증서 및 비밀 → 새 클라이언트 비밀 → **값 복사**
3. API 권한 → Microsoft Graph → **애플리케이션 권한 → `Mail.Send`** → **관리자 동의 부여**
4. (권장) Exchange PowerShell 로 발송 사서함 제한:
   `New-ApplicationAccessPolicy -AppId <ClientId> -PolicyScopeGroupId <발송사서함/그룹> -AccessRight RestrictAccess`

## 자격증명·키 주입 (git 미포함)

`appsettings.Secrets.sample.json` 을 `appsettings.Secrets.json` 으로 복사해 값 채운 뒤 배포 폴더에 배치.
또는 환경변수: `Relay__Smtp__Password`, `Relay__ApiKeys__0__Key` 등.
API 키 생성 예: `openssl rand -hex 32`.

## Linux(systemd) 배포

```bash
dotnet publish Apps/BriefingRelay/BriefingRelay.csproj -c Release -o /opt/briefingrelay
# /opt/briefingrelay 에 appsettings.Secrets.json 배치(권한 600, 서비스 계정 소유)
sudo chmod 600 /opt/briefingrelay/appsettings.Secrets.json
```

`/etc/systemd/system/briefingrelay.service`:
```ini
[Unit]
Description=DSPilot Briefing Relay
After=network.target

[Service]
Type=notify
WorkingDirectory=/opt/briefingrelay
ExecStart=/usr/bin/dotnet /opt/briefingrelay/BriefingRelay.dll
Restart=on-failure
User=briefingrelay
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload && sudo systemctl enable --now briefingrelay
```

### TLS
앱은 기본 `http://0.0.0.0:8088` 로만 수신합니다(`appsettings.json`). **반드시 앞단에 TLS 종단**을 두세요
(nginx/Caddy 리버스 프록시 권장). 인터넷 노출 시 HTTPS 없이 API 키를 그대로 전송하면 안 됩니다.

nginx 예:
```nginx
location /api/briefing/ {
    proxy_pass http://127.0.0.1:8088;
    proxy_set_header X-Forwarded-For $remote_addr;
}
```

## 향후 보안 강화(구상 대상)

키 저장소/쿼터의 영속화(DB/Redis), 키 로테이션·만료, 요청 HMAC 서명·재전송 방지(nonce),
IP 허용목록/mTLS, 감사 로그, 비밀 관리자(Key Vault 등), 남용 알림, 스케일아웃 시 분산 카운터.
