# LightHouse Service — 설치 & Promaker 연결 가이드

> Phase S5c 기준. dev/PoC 환경의 단계별 setup 가이드. 운영 배포 시 §10 (사내 CA 발급) 정합 권장.
>
> 관련 문서: `done-lighthouse-kb-server.md` (design SSOT) / `todo-lighthouse-handover.md` (다음 세션 이어받기).

---

## 0. 개요

LightHouse Service = 사내 LAN 의 Knowledge Base 색인/검색 host. Promaker 의 LLM Chat 이 첨부 폴더의 PDF/DOCX/XLSX/TXT/MD 를 색인 → 다른 사용자도 검색 가능 (multi-tenant T1 flat).

**구성 요소** (commit `79ee30b` s5c-r0 기준):
- `Ds2.LightHouseService` (F# Windows Service, Kestrel HTTPS + Bearer PSK + MCP host)
- Promaker 측 `LightHouseClient` / `KbManagerDialog` / `AttachmentIngestService`
- in-process `Ds2.LightHouse` lib (Indexer + KnowledgeBase facade)

**보안 모델**:
- TLS 의무 (plain HTTP 거부) — PoC 단계 self-signed, 운영은 사내 CA
- PSK (Pre-Shared Key) Bearer 인증 + `X-User-Identity` 헤더
- 평문 PSK 는 DPAPI 로 양쪽 저장 — server LocalMachine / client CurrentUser

---

## 1. 사전 요구 사항

| 항목 | 버전 | 비고 |
|------|------|------|
| Windows | 10 / 11 / Server 2019+ | DPAPI + sc.exe + EventLog |
| .NET SDK | 9.0.x | service / Promaker 둘 다 |
| PowerShell | 5.1 또는 7.x | install-service.ps1 가 5.1 호환 |
| 관리자 권한 | 필수 | sc.exe register + DPAPI(LocalMachine) + LocalMachine cert store |
| Git Bash (선택) | 최신 | `make` 사용 시 |

저장소 위치: `F:\Git\ds2\light-house` (사용자 환경 기준).

---

## 2. 설치 — `make install` 한 줄

**관리자 PowerShell** 에서 (Git Bash 가 admin 가능하면 그쪽도 OK):

```bash
cd /f/Git/ds2/light-house/Apps/Promaker
make install
```

내부 3단계 chain (`Makefile` 참조):
1. **`generate-dev-cert.ps1`** — self-signed PFX 생성 (`Cert:\LocalMachine\My` 발급 + Export-PfxCertificate). PFX password 대화형 입력. 위치 = `C:\ProgramData\Dualsoft\LightHouseService\service.pfx`.
2. **`dotnet publish`** — `Ds2.LightHouseService` framework-dependent Release 빌드 → `Solutions/Tools/Ds2.LightHouseService/bin/Release/net9.0/publish/`.
3. **`install-service.ps1`** — PSK 평문 입력 + DPAPI(LocalMachine) 암호화 + `config.json` 작성 + `sc.exe create` Windows Service 등록 + EventLog Source 등록.

성공 출력 예시:
```
config.json 작성: C:\ProgramData\Dualsoft\LightHouseService\config.json
EventLog Source 등록: Ds2.LightHouseService
Service 등록 완료: Ds2.LightHouseService
시작: sc.exe start Ds2.LightHouseService
```

**입력 받은 secret 보관 — 분실 시 재install 필요**:
- PFX password (cert decrypt)
- PSK (Promaker 측 설정에 동일하게 입력)

---

## 3. 서비스 시작

```powershell
sc start Ds2.LightHouseService
# 또는
Start-Service Ds2.LightHouseService
```

상태 확인:
```powershell
sc query Ds2.LightHouseService
# STATE = 4 RUNNING 이어야 정상
```

로그 확인 (git bash):
```bash
ls /c/ProgramData/Dualsoft/LightHouseService/Logs/
cat /c/ProgramData/Dualsoft/LightHouseService/Logs/service-YYYYMMDD.log
```

정상 부팅 시 다음 박제 (예시):
```
... Ds2.LightHouseService 시작 — argv=...
... config 경로 = C:\ProgramData\Dualsoft\LightHouseService\config.json
... storage root 초기화 완료
... TLS 인증서 로드 완료 — subject=CN=localhost thumbprint=...
... Kestrel HTTPS listen 시작 — https://127.0.0.1:8443
... Ds2.LightHouseService 시작 완료
... SessionSweepService 시작 — idleAfterMinutes=240 sweepIntervalMs=3600000
```

**listenUrl** default = `https://127.0.0.1:8443` (loopback, 외부 미노출). 사내 LAN 노출 의도면 `install-service.ps1 -ListenUrl https://<lan-ip>:8443` 옵션 명시.

---

## 4. 인증서 신뢰 setup

self-signed cert 이므로 client (Promaker / curl / browser) 가 신뢰 안 함. 두 방법:

### 4.1 권장 — Trusted Root 에 import (정공)

**관리자 PowerShell**:
```powershell
# install 시 service-YYYYMMDD.log 의 thumbprint 또는 cert store 에서 직접 조회
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -eq "CN=localhost"
Export-Certificate -Cert $cert -FilePath "$env:TEMP\ds2-lighthouse-dev.cer"
Import-Certificate -FilePath "$env:TEMP\ds2-lighthouse-dev.cer" -CertStoreLocation Cert:\LocalMachine\Root
```

이후 `.NET HttpClient` (Promaker) / curl (schannel) 자동 신뢰.

### 4.2 우회 (개발 임시) — `-k` (curl 만)

```bash
curl -kv -H "Authorization: Bearer <PSK>" -H "X-User-Identity: $USER" https://localhost:8443/collections
```

`-k` 는 curl 한정. Promaker 측 신뢰 우회는 [s5c-r1 follow-up] 의 `LightHouseClient` dev-only flag 도입 필요 (아직 미구현).

### 4.3 wire 검증 (Promaker 거치지 않고)

```bash
PSK="install 시 입력한 비밀"
curl -v -H "Authorization: Bearer $PSK" -H "X-User-Identity: $USER" https://localhost:8443/collections
```

기대 응답:
```
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8

{"collections":[],"schemaVersion":1}
```

**hostname 주의**: cert 의 CN = `localhost`. `127.0.0.1` 로 접속 시 `SEC_E_WRONG_PRINCIPAL` (cert principal mismatch). 항상 **`localhost`** 사용. 사내 hostname 사용 의도면 cert 재발급 시 `-DnsName "localhost","<lan-ip>","<hostname>"` 명시.

---

## 5. Promaker 연결 설정

1. Promaker 실행
2. 상단 메뉴 **설정** → **LLM** 탭
3. 맨 아래 **LightHouse Service (Knowledge Base)** section:
   - **Base URL (HTTPS)** = `https://localhost:8443`
   - **Pre-Shared Key** = (install 시 입력한 PSK 와 동일)
4. **연결 테스트** 버튼 → 예상 `✅ 연결 성공 — collection 0건` (light blue)
   - 실패 시 §9 트러블슈팅 참조
5. **확인** 버튼으로 다이얼로그 닫기 — PSK 가 DPAPI(CurrentUser) 로 암호화 저장 (`%APPDATA%\Dualsoft\Promaker\Settings\llm-config.json` 의 `lightHouseService.apiKeyEncrypted`).

---

## 6. Knowledge Base 등록 + 사용

### 6.1 KbManagerDialog 진입

LLM Chat 패널 상단 **📚 KB 관리** 버튼.

### 6.2 폴더 등록

1. "새 Collection 등록" 영역에서:
   - **폴더** = `…` 버튼으로 picker, 또는 절대경로 직접 입력
   - **이름** = collection 표시 이름 (모든 사용자에게 노출)
2. **등록 시작** 클릭
3. **Consent dialog** (§6 m2 SSOT — T1 flat PII 위험 안내) → "예" 선택
4. 진행률 표시:
   - 복사 → 색인 → 패키징 → 업로드 → 완료
5. ListView 에 행 1개 추가, **Active** 토글 = ☑ (default)

### 6.3 chat 진입 + KB 검색

1. KbManagerDialog 닫기 → LLM Chat 패널
2. 새 chat 시작 (또는 panel close + reopen 으로 session 재발급)
3. LLM 에게 등록 폴더 내용 질문:
   - "라인A 사양서에서 IO 리스트 알려줘"
4. LLM 이 MCP `attachment_search` / `attachment_read` tool 호출 → citation 포함 응답

**중요** — Active 토글 변경은 **현 chat 영향 0** (§3.8 L1). 다음 chat panel open 시 새 session 으로 반영.

### 6.4 재업로드 / 제거

KbManagerDialog 의 collection 행에 **재업로드** / **제거** 버튼.
- **재업로드** — 새 폴더 선택 → 같은 collection ID 의 zip swap (D5)
- **제거** — server 의 `Collections\<guid>\` purge + LlmConfig 정리

---

## 7. 콘솔 모드 (개발 / 디버그)

코드 수정 후 빠른 재실행에 사용. Windows Service 정지 후 콘솔 실행.

```bash
cd /f/Git/ds2/light-house/Apps/Promaker
# Windows Service 정지 (port 8443 충돌 회피)
sc stop Ds2.LightHouseService

# 콘솔 실행 (dotnet run, Ctrl+C 로 중단)
make light-house
```

`make light-house` 는 `dotnet run --project ../../Solutions/Tools/Ds2.LightHouseService/Ds2.LightHouseService.fsproj -c Debug`. config 는 install 단계에서 작성한 `%PROGRAMDATA%\Dualsoft\LightHouseService\config.json` 그대로 사용.

콘솔 모드 종료 후 Service 재시작:
```powershell
sc start Ds2.LightHouseService
```

---

## 8. 제거 (uninstall)

```bash
# 관리자 PowerShell
cd F:\Git\ds2\light-house\Solutions\Tools\Ds2.LightHouseService\scripts
.\uninstall-service.ps1
```

수동 정리 (선택):
```powershell
# config / cert 제거
Remove-Item -Recurse C:\ProgramData\Dualsoft\LightHouseService

# Trusted Root 의 dev cert 제거
$cert = Get-ChildItem Cert:\LocalMachine\Root | Where-Object Subject -eq "CN=localhost" | Where-Object Issuer -eq "CN=localhost"
$cert | Remove-Item
# LocalMachine\My 에서도 동일 제거
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -eq "CN=localhost"
$cert | Remove-Item
```

Promaker 측 cleanup (선택):
- `%APPDATA%\Dualsoft\Promaker\Settings\llm-config.json` 의 `kbCollections` / `lightHouseService` 항목 삭제, 또는 Promaker 설정에서 BaseUrl/PSK 빈 값으로 저장

---

## 9. 트러블슈팅

### 9.1 `sc start LightHouseService` → "지정된 서비스가 설치된 서비스로는 없습니다"

Service 이름은 `Ds2.LightHouseService` (prefix 포함). 정확히:
```powershell
sc start Ds2.LightHouseService
```

### 9.2 PowerShell 한글 깨짐 (`?몄쬆??` 등)

PowerShell 5.1 (powershell.exe) 가 BOM 없는 UTF-8 .ps1 파일을 cp949 로 해석 → 한글 깨짐. **현재 `.ps1` 3종 모두 UTF-8 BOM 포함** (s5c-r1 fix). 외부 ps1 추가 시 BOM 박제 필수.

### 9.3 curl 의 `SEC_E_UNTRUSTED_ROOT`

cert 가 Trusted Root 에 미import. §4.1 진행. 임시 우회는 `-k`.

### 9.4 curl 의 `SEC_E_WRONG_PRINCIPAL`

cert 의 CN/SAN 이 요청 hostname 과 매칭 안 됨. 본 PoC cert 의 CN = `localhost` 라 **`127.0.0.1` 대신 `localhost`** 사용. 사내 LAN hostname 사용 의도면 cert 재발급 + SAN 에 hostname 추가:
```powershell
New-SelfSignedCertificate -DnsName "localhost","127.0.0.1","<lan-hostname>" `
    -CertStoreLocation "Cert:\LocalMachine\My" -KeyExportPolicy Exportable `
    -KeyAlgorithm RSA -KeyLength 2048 -NotAfter (Get-Date).AddYears(2)
```

### 9.5 Promaker 의 "연결 테스트" 실패 — 401 인증

PSK 가 install 시 입력값과 다름. install 재수행 또는 install-service.ps1 단독 호출 (PSK 만 갱신, sc.exe create 는 idempotent 아니라 `sc.exe delete Ds2.LightHouseService` 후 재실행).

### 9.6 Promaker 의 "연결 테스트" 실패 — connection / TLS

- service RUNNING 인지 (`sc query Ds2.LightHouseService` 의 STATE=4)
- port 8443 listening 인지 (`netstat -ano | findstr :8443`)
- Trusted Root 에 cert import 됐는지 (§4.1)
- BaseUrl 이 `https://localhost:8443` 인지 (`127.0.0.1` 아님)

### 9.7 색인 중 UI freeze

`Indexer.ingest` 가 F# 동기 함수지만 `AttachmentIngestService.RunIndexerAsync` 가 `Task.Run` wrap (review S5b-M4). UI freeze 발생 시 caller 가 직접 호출했는지 확인 — 정합 patch 는 s5b-r0 commit `5c8da12` 박제.

### 9.8 Windows Service install 후 즉시 stop 됨

config.json 의 `tlsCertPath` 가 잘못된 path 또는 PFX password 가 잘못된 경우 service 시작 직후 fail. log 확인:
```bash
cat /c/ProgramData/Dualsoft/LightHouseService/Logs/service-YYYYMMDD.log
```
"TLS 인증서 로드 완료" 박제가 없으면 cert 단계 fail. PFX 재발급 + install 재수행.

---

## 10. 운영 배포 권장

PoC → production 전환 시:

1. **TLS 인증서** — self-signed 폐기, 사내 CA 발급 (`Ds2.LightHouseService.scripts` 의 generate-dev-cert.ps1 미사용)
2. **listenUrl** — `https://<lan-ip>:8443` (사내 LAN 정책 따름) 또는 mTLS Phase S7 진입 시 다른 port
3. **PSK 회전 정책** — 수동, 정기. Phase S7 mTLS 도입 권장 (회전 부담 완화).
4. **paired-release** — `Ds2.LightHouse.dll` AssemblyVersion 비교, dist post-build target 강제 (`make dist` 진입 시).
5. **backup / DR** — `%PROGRAMDATA%\Dualsoft\LightHouseService\` 전체 (VSS / robocopy /mir + flush). 또는 Phase S7 의 `POST /admin/backup` endpoint.
6. **audit log retention** — config.json 의 `auditRetentionDays` (default 365). 보관 정책 검토.
7. **EventLog 모니터링** — `Ds2.LightHouseService` source 의 Warning/Error 알림 hook (SCOM / Zabbix 등).

---

## 11. API surface 요약 (참고)

| API | 인증 | 용도 |
|------|------|------|
| `POST /collections` (multipart) | Bearer + X-User-Identity | 신규 등록 (zip + title) |
| `GET /collections` | Bearer + X-User-Identity | registry list |
| `POST /collections/{id}/payload` | Bearer + X-User-Identity | 재업로드 |
| `DELETE /collections/{id}` | Bearer + X-User-Identity | 제거 |
| `GET /collections/{id}/files/{fileId}` | Bearer + X-User-Identity | citation 원문 stream |
| `POST /sessions` `{collectionIds}` | Bearer + X-User-Identity | active 셋 routing token |
| `DELETE /sessions/{token}` | Bearer + X-User-Identity | session 해제 |
| `/mcp` (HTTP transport) | Bearer + X-LightHouse-Session | MCP tools: `attachment_list/_outline/_search/_read` |
| `/healthz` | (인증 무관) | health probe |

세부 protocol 정합은 `done-lighthouse-kb-server.md` §3.9 참조.

---

## 12. 관련 파일 / 경로

### Service 측
- `Solutions/Tools/Ds2.LightHouseService/Ds2.LightHouseService.fsproj`
- `Solutions/Tools/Ds2.LightHouseService/scripts/install-service.ps1`
- `Solutions/Tools/Ds2.LightHouseService/scripts/uninstall-service.ps1`
- `Solutions/Tools/Ds2.LightHouseService/scripts/generate-dev-cert.ps1`
- `Solutions/Tools/Ds2.LightHouseService/scripts/config.json.template`

### Promaker 측
- `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs` — HTTP wrapper
- `Apps/Promaker/Promaker/Knowledge/LightHouseClientHolder.cs` — process singleton
- `Apps/Promaker/Promaker/Knowledge/CollectionPackager.cs` — folder → zip + meta.json
- `Apps/Promaker/Promaker/Knowledge/AttachmentIngestService.cs` — Indexer + Packager + Upload
- `Apps/Promaker/Promaker/Dialogs/KbManagerDialog.xaml(.cs)` — KB 관리 UI
- `Apps/Promaker/Promaker/Dialogs/ApplicationSettingsDialog.xaml(.cs)` — 연결 설정
- `Apps/Promaker/Promaker/ViewModels/LlmChatViewModel.cs` — session 발급 + .mcp-config + 해제
- `Apps/Promaker/Promaker/App.xaml.cs` — process exit 의 일괄 session DELETE

### Runtime 위치
- `%PROGRAMDATA%\Dualsoft\LightHouseService\` (service)
  - `config.json` (DPAPI 암호화 PSK/PFX password)
  - `service.pfx`
  - `Collections\<guid>-<title>\` (등록된 KB)
  - `Logs\service-YYYYMMDD.log`
  - `Audit\audit-YYYYMMDD.log`
  - `Staging\` (upload 임시)
- `%APPDATA%\Dualsoft\Promaker\Settings\llm-config.json` (client)
  - `kbCollections` (등록한 collection 리스트 + Active flag)
  - `lightHouseService.baseUrl` + `apiKeyEncrypted` (DPAPI CurrentUser)

### 빌드 명령 (Apps/Promaker 폴더 기준)
- `make install` — service 설치 (관리자)
- `make light-house` — 콘솔 모드 실행
- `make help` — 전체 target 목록

---

## 13. 변경 이력

> [revision-history/howto-connect-lighthouse-service.md](revision-history/howto-connect-lighthouse-service.md) 로 분리 보관.
