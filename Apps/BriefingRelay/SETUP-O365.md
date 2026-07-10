# DSPilot 브리핑 메일 — O365 발송 설정 매뉴얼

DSPilot 일일 브리핑 메일을 회사 Microsoft 365 계정으로 발송하기 위한 **전체 셋업 절차**.
실제 검증된 순서 그대로다. (아래 값은 모두 **플레이스홀더** — `<...>` 부분을 각자 환경 값으로 채운다.)

> **핵심 원리**: Microsoft 365 테넌트에 **보안 기본값(Security Defaults)** 이 켜져 있으면 비밀번호 기반 발송(basic SMTP / ROPC)이 전면 차단된다.
> 따라서 **OAuth 앱 전용(client credentials) + Microsoft Graph `sendMail`** 을 사용한다.

```
[고객사 DSPilot] --HTTPS + X-Api-Key--> [BriefingRelay 서버] --OAuth 토큰 + Graph--> [O365: 공유사서함으로 발송]
   (API 키만 보유)                         (client secret 보유)
```

- 메일 계정 자격증명(client secret)은 **릴레이 서버에만** 존재. 고객 PC에는 저위험 API 키만.
- 발송 주체는 **비관리자 공유 사서함**(관리자 계정 아님).

> ⚠️ **이 문서에는 실제 Tenant/Client ID·비밀·계정 주소를 기록하지 않는다.** 실제 값은 배포 폴더의 `appsettings.Secrets.json`(git 제외) 또는 비밀 관리자에만 둔다.

---

## 사전 조건

- Microsoft 365 **전역 관리자(Global Administrator)** 계정 — 셋업 전용, 발송용 아님.
- Microsoft Entra ID Free 티어면 충분(유료 불필요).
- 아래에서 발송 도메인을 `<회사도메인>`(예: 자사 도메인)으로 표기한다.

---

## STEP 1 — 발송용 공유 사서함 생성

**공유 사서함**을 쓴다(라이선스 불요·비밀번호 없음·로그인 불가 → 안전).

1. https://admin.cloud.microsoft → **Teams 및 그룹 → 공유 사서함**
2. **+ 공유 사서함 추가**
   - 이름: `DSPilot 리포트`
   - 이메일: `report@<회사도메인>`
3. 목록에 생성 확인. (사서함 실제 준비까지 몇 분 소요될 수 있음)

> ⚠️ 일반 "사용자 추가"로 만들면 Exchange Online **라이선스가 필요**하다(없으면 사서함이 안 생겨 발송 불가). 공유 사서함은 라이선스 불요.

---

## STEP 2 — Entra ID 앱 등록

1. https://portal.azure.com → **Microsoft Entra ID**
2. 왼쪽 **Manage → App registrations → + New registration**
   - Name: `DSPilot-BriefingRelay`
   - Supported account types: **이 조직 디렉터리의 계정만 (Single tenant)**
   - Redirect URI: **비워둠**
   - → **Register**
3. 등록 후 **Overview** 화면에서 아래 두 값 복사(각자 값):
   - **Application (client) ID** → 나중에 `ClientId`
   - **Directory (tenant) ID** → 나중에 `TenantId`

---

## STEP 3 — 클라이언트 비밀 생성

1. 앱 → 왼쪽 **Certificates & secrets → Client secrets → + New client secret**
2. 설명 입력, 만료 기간 선택(예: 24개월) → **Add**
3. 생성 직후 표에서 **`Value` 열**의 문자열 복사 → 나중에 `ClientSecret`

> ⚠️ **반드시 `Value` 열**을 복사한다. `Secret ID`(GUID) 아님. (잘못하면 `AADSTS7000215` 오류)
> ⚠️ `Value`는 **이 화면을 벗어나면 다시 볼 수 없다.** 못 봤으면 새 비밀을 만들어 즉시 복사.
> 📅 만료일 캘린더 등록 — 만료 전 새 비밀로 교체(로테이션) 필요.

---

## STEP 4 — API 권한 부여 (Mail.Send) + 관리자 동의

1. 앱 → 왼쪽 **API permissions → + Add a permission**
2. **Microsoft Graph** → **Application permissions** 선택
   - ⚠️ **Delegated permissions 아님. 반드시 Application permissions.**
3. `Mail.Send` 검색 → 체크 → **Add permissions**
4. 그다음 **“Grant admin consent for [테넌트]”** 버튼 클릭 → 확인
5. `Mail.Send` 행의 **Status** 가 **초록 체크 “Granted”** 로 바뀌었는지 확인

> ⚠️ 관리자 동의를 누르지 않으면 발송 시 **403 ErrorAccessDenied**.
> ⏱️ 동의 후 반영까지 1~2분 걸릴 수 있음.

---

## STEP 5 — 발송 사서함 제한 (ApplicationAccessPolicy) 【보안 필수】

STEP 4까지만 하면 이 앱은 **테넌트의 아무 사서함(관리자·대표 포함)** 으로 발송 가능하다.
발송용 공유 사서함 **하나만** 보내도록 잠근다. **GUI 없음 — Exchange Online PowerShell 전용.**

```powershell
# 최초 1회 모듈 설치
Install-Module ExchangeOnlineManagement -Scope CurrentUser

# 접속 — 브라우저 로그인 창(관리자 계정, MFA 승인)
Connect-ExchangeOnline -UserPrincipalName <관리자계정>

# 1) 메일 사용 보안 그룹 + 발송 사서함을 멤버로
New-DistributionGroup -Name "DSPilot-Report-Senders" -Type Security `
  -PrimarySmtpAddress report-senders@<회사도메인> `
  -Members report@<회사도메인>

# 2) 앱을 그 그룹으로만 제한 (AppId = STEP 2 의 ClientId)
New-ApplicationAccessPolicy -AppId <ClientId> `
  -PolicyScopeGroupId report-senders@<회사도메인> `
  -AccessRight RestrictAccess -Description "DSPilot relay: report mailbox only"

# 3) 검증
Test-ApplicationAccessPolicy -Identity report@<회사도메인> -AppId <ClientId>   # AccessCheckResult: Granted
Test-ApplicationAccessPolicy -Identity <관리자계정>       -AppId <ClientId>   # AccessCheckResult: Denied
```

- 기대: 발송 사서함 = **Granted**, 그 외 = **Denied**.
- ⏱️ `Test-...`는 즉시 반영되지만 **실제 발송 차단 적용은 최대 ~30~60분** 지연될 수 있다. 그동안에도 발송용 사서함 발송은 정상.
- 인터넷에 릴레이를 노출(서버 배포)하기 **전에 반드시** 완료할 것.

---

## STEP 6 — 릴레이 서버 설정 (`appsettings.Secrets.json`)

`Apps/BriefingRelay/` 배포 폴더에 `appsettings.Secrets.json` 배치 (git 제외, 템플릿=`appsettings.Secrets.sample.json`).

```json
{
  "Relay": {
    "AuthMode": "oauth",
    "OAuth": {
      "TenantId": "<STEP 2: Directory (tenant) ID>",
      "ClientId": "<STEP 2: Application (client) ID>",
      "ClientSecret": "<STEP 3: 비밀 Value>",
      "Sender": "report@<회사도메인>",
      "FromName": "DSPilot 브리핑"
    },
    "ApiKeys": [
      { "Key": "<설치별 랜덤 키 (예: openssl rand -hex 32)>", "Name": "고객사-현장", "DailyQuota": 200, "AllowedDomains": [] }
    ]
  }
}
```

- 환경변수로도 주입 가능: `Relay__OAuth__ClientSecret`, `Relay__ApiKeys__0__Key` 등.
- ⚠️ 설정은 서버 시작 시 **1회 로드**된다. 값 변경 후 **서버 재시작** 필요.

---

## STEP 7 — DSPilot 연결 (`appsettings.Secrets.json`)

각 DSPilot 설치의 배포 폴더에 배치 (git 제외).

```json
{
  "BriefingRelay": {
    "Mode": "api",
    "ApiUrl": "https://<릴레이 서버 주소>",
    "ApiKey": "<STEP 6 에서 이 설치에 발급한 ApiKey>",
    "Locked": true
  }
}
```

- 로컬 테스트 시 `ApiUrl` = `http://localhost:8088`.
- `Locked: true` 면 DSPilot 설정 UI 에서 SMTP/발신 항목은 숨겨지고, 사용자는 **활성/수신자/스케줄**만 설정.

---

## 리눅스 서버 배포 & 운영 (install.sh)

릴레이는 `Installer/linux/` 의 스크립트로 **패키징 → 서버 설치 → 서비스 기동**까지 한다.

### (A) 배포 패키지 빌드 (dotnet SDK 있는 PC/빌드머신)

```bash
bash Apps/BriefingRelay/Installer/linux/build-linux.sh
# 산출물: Apps/BriefingRelay/Output/BriefingRelay_linux-x64_<version>.tar.gz
```
self-contained(런타임 포함)라 **서버에 .NET 설치 불필요**.

### (B) 서버에 설치 (한 번)

```bash
# 1) 서버로 복사 후 압축 해제
scp BriefingRelay_linux-x64_<version>.tar.gz  사용자@<서버주소>:~
ssh 사용자@<서버주소>
tar -xzf BriefingRelay_linux-x64_<version>.tar.gz
cd BriefingRelay_linux-x64_<version>

# 2) 설치 (실행비트 이슈 회피 위해 bash 로 실행 권장)
sudo bash install.sh                 # 포트 변경: sudo bash install.sh --port 9000
```

install.sh 가 자동으로: **서비스 계정 생성 → `/opt/briefingrelay` 배포 → 포트(기본 8088) → 방화벽 규칙 → systemd 등록·기동 → healthz 확인.** 자격증명 미입력이면 발송은 fail-closed 이고 안내를 출력한다.

### (C) 자격증명 입력 (최초 1회)

편집 대상은 **설치 위치**의 파일이다 (⚠️ 압축 푼 폴더의 `.sample` 아님):
```bash
sudo nano /opt/briefingrelay/appsettings.Secrets.json    # STEP 6 의 OAuth 값
sudo systemctl restart briefingrelay                     # 설정은 시작 시 1회 로드 → 재시작으로 반영
```

### (D) 재실행 vs 재시작 — 구분

| 상황 | 해야 할 것 |
|---|---|
| **자격증명/포트 등 설정 변경** | `sudo systemctl restart briefingrelay` (install.sh 재실행 아님) |
| **새 버전 배포(업그레이드)** | 새 tarball 풀고 `sudo bash install.sh` **재실행** — 멱등, 기존 `appsettings.Secrets.json`·포트 **보존**(덮어쓰지 않음) |
| 새 API 키 추가 / 시크릿 로테이션 | `appsettings.Secrets.json` 편집 + `systemctl restart` |

> 즉 **일상 반영 = 재시작**, **install.sh 재실행 = 업그레이드 때만**. 편지 내용·스케줄·수신자는 릴레이가 아니라 **DSPilot** 에서 바꾼다(릴레이 재배포 불필요).

### (E) 운영 명령어

```bash
sudo systemctl status briefingrelay --no-pager   # 상태
sudo systemctl restart briefingrelay             # 재시작
sudo journalctl -u briefingrelay -f              # 실시간 로그 ("인증모드 oauth ... 발송계정 구성 True" 확인)
sudo bash uninstall.sh                            # 제거(설치본 보존), --purge 는 자격증명 포함 완전삭제
```

### (F) 네트워크 주의

- 기본 **8088 평문 HTTP** → API 키가 평문 전송. 내부/신뢰망이면 허용, **인터넷 노출이면 nginx/Caddy TLS** 앞단 권장(8088 은 `127.0.0.1` 로만).
- 외부 접근하려면 서버 방화벽 + 경계 방화벽/NAT 에서 **해당 포트 개방** 필요.

---

## 검증 (End-to-End)

**① 릴레이 단독**
```bash
curl -i -X POST https://<릴레이주소>/api/briefing/send \
  -H "Content-Type: application/json" -H "X-Api-Key: <ApiKey>" \
  -d '{"subject":"test","html":"<b>hi</b>","recipients":["someone@<회사도메인>"]}'
# 기대: HTTP 200 {"sent":true,...}
```

**② DSPilot에서**: `/settings-email` → 자동 발송 ON → 수신자 추가 → 저장 → **[테스트 발송]** → “발송했습니다”.

---

## 트러블슈팅 (실제 겪은 오류)

| 증상 | 원인 | 해결 |
|---|---|---|
| `535 5.7.139 ... security defaults policy` | 보안 기본값이 basic 인증 차단 | basic/ROPC 포기, **OAuth 사용**(이 문서) |
| `AADSTS7000215 Invalid client secret` | 비밀 **ID**를 넣음 | `Certificates & secrets`의 **Value** 값 사용 |
| `Graph sendMail 403 ErrorAccessDenied` | Mail.Send 미동의 / Delegated로 추가 | **Application** 권한 + **관리자 동의** |
| 발송이 계속 옛 설정으로 동작 | 설정 1회 로드 | 릴레이 **재시작** |
| 동의/정책 걸었는데 반영 안 됨 | 전파 지연 | 1~2분(동의) / ~30~60분(정책) 대기 |
| 사용자 만들었는데 발송 실패 | 일반 사용자에 Exchange 라이선스 없음 | **공유 사서함** 사용(라이선스 불요) |
| direct-to-MX `blocked using Spamhaus` | 발신 IP 평판/SPF 부재 | 릴레이(정식 계정) 경유 — 이 문서 방식 |

---

## 배포 값 관리 (보안)

셋업 후 아래 값이 생긴다. **공개 저장소·문서에 절대 기록하지 않는다.**

| 항목 | 성격 | 보관처 |
|---|---|---|
| Tenant ID / Client ID | 식별자(준공개) | 비밀 관리자 또는 내부 위키(공개 repo 금지) |
| **Client Secret** | **비밀** | `appsettings.Secrets.json`(git 제외) 또는 Key Vault **에만** |
| API Key(설치별) | 비밀 | 릴레이 Secrets + 해당 DSPilot Secrets(양쪽 git 제외) |
| 발송 사서함 / 그룹명 | 내부 정보 | 내부 문서 |

> 이 매뉴얼 자체는 플레이스홀더만 담아 공개 저장소에 커밋해도 안전하다.
