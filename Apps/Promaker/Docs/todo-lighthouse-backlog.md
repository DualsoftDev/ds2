# LightHouse Plan B 잔여 backlog

본 문서는 `--transfer` 산출 — 새 Claude Code session 이 진입 시 본 문서 1개만 read 후 작업 시작.
처리 완료 항목은 별 git log (commit `abe0956b` / `862216a1`) + 코드 주석으로 추적.

## 0. 진입 절차

```bash
git log --oneline abe0956b 862216a1   # Plan B + 메타리뷰 채택 history
git show 862216a1 -- Apps/Promaker/Promaker/LlmAgent/Tools/LightHouseTools.cs    # 핵심 wrapper
```

추가 read 권장:
- `Apps/Promaker/Promaker/LlmAgent/Tools/LightHouseTools.cs` (LightHouse mcp wrapper, fan-out)
- `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs` (mcp JSON-RPC + session lifecycle)
- `Apps/Promaker/Promaker/Knowledge/LightHouseClientHolder.cs` (active client pool)

## 1. 배경 / 맥락 (최소)

- Anthropic Claude CLI 의 mcp HTTP/SSE transport 가 OAuth 2.1 강제 (`_authThenStart`) — 단순 Bearer PSK 와 fundamental mismatch.
- 우회 = **Plan B**: Promaker 자체 MCP host (`mcp__promaker__*`) 가 lighthouse 의 attachment_* 6종을 fan-out wrapper 로 노출. Claude CLI 는 promaker MCP 1개만 직접 통신.
- 본 base 는 commit `abe0956b` + `862216a1` 에 완성. 본 backlog 는 그 위의 polish / Test / 성능 / 보안 4축.

## 2. 잔여 backlog (4건 — 우선순위 순)

### B1. **session pooling** (이전 turn Major-1, 우선순위 ★★★)

#### 현상
`LightHouseClient.ExecuteWithSessionRetryAsync` 가 매 op 마다 첫 단계에서 `RecoverSessionAsync` (POST /sessions) **신규** 발급. fan-out N×op 호출 시 service 측 session storm.

#### 위치
- `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs:533-571` (ExecuteWithSessionRetryAsync)
- `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs:543` (`var sess = await RecoverSessionAsync(ct)` — 항상 신규)
- service 측 `Solutions/Tools/Ds2.LightHouseService/SessionEndpoints.fs` 의 session 발급 / TTL

#### 권장 path
1. `LightHouseClient` 에 `_cachedAppSessionToken: string?` field + `SemaphoreSlim _sessionLock` 추가
2. `ExecuteWithSessionRetryAsync` 의 첫 단계가 cached token 있으면 그대로 사용, 없으면 신규 발급 후 캐시
3. 401/403 retry 분기에서 cached token 무효화 + 신규 발급 → 그 token 도 캐시
4. service 의 `sessionIdleTtlMinutes` (default 240) 와 정합 — caller 측 idle 시간 추적은 불요 (server 가 410 등으로 reject 시 자동 재발급).
5. `Mcp-Session-Id` (`_mcpSessionId`) 와 app session 두 lifecycle 분리 유지 — app session 재발급 시 mcp session 도 reset (LightHouseClient 의 `ResetMcpSession()` 호출).

#### test 의무 (m11 의 일부)
- cached token 재사용 검증
- 401 시 cache 무효화 + 신규 발급 검증
- concurrent op N개 일제 진입 시 신규 발급 1회만 (lock 정합) 검증

---

### B2. **m11 — LighthouseToolsTests 신설** (메타리뷰 m11, 우선순위 ★★★)

#### 현상
LightHouseTools 의 wrapper 6종 / fan-out / fileId routing / mcp session retry 등 **test 0건**. CR4 폐기로 사라진 `LightHouseServerNamingTests` 의 빈 자리.

#### 위치
- 신설: `Solutions/Tests/Promaker.Tests/LightHouseToolsTests.cs`
- 대상: `Apps/Promaker/Promaker/LlmAgent/Tools/LightHouseTools.cs`

#### Test 의무 (최소)
- `TryParseFileId` — 정상 / `:` 없음 / 첫 segment 길이 mismatch / 빈 값
- `MakePrefix` — Guid v4 첫 8자 / 8자 미만
- `FindClientByPrefix` — 매칭 / 미매칭 (mock `LightHouseClientHolder` 또는 `EnsureCreated` 우회 path)
- `MergeListResults` — N service concat + perServiceCount 정합
- `MergeSearchResultsRoundRobin` — interleave 순서 (각 server 1건씩 → 2건씩 → 잔여)
- `PrefixFileIdsInListResponse` / `PrefixFileIdsInSearchResponse` — fileId prefix 박제 + serviceName 추가 + 인자 순서 일관 (메타리뷰 M1 회귀 가드)
- `LighthouseSummary` — fileId 의 `:` 첫 segment 추출 후 collectionId 인자 변환

#### 의존성 처리
LightHouseClientHolder 가 static + LlmConfig.EnsureCreated 의존 — test 에서 mock 어려움.
권장 path:
- (a) LightHouseClientHolder 의 internal `_clients` dict 에 InternalsVisibleTo 박제 후 직접 박제 (단순)
- (b) LightHouseTools 의 fan-out / routing 로직만 별 internal helper 추출 + 그 helper test (의존성 분리)

(b) 가 정공이나 (a) 빠름. 결정 시점에 비교.

---

### B3. **M10 — JsonNode refactor** (메타리뷰 M10, 우선순위 ★★)

#### 현상
`LightHouseTools.cs:228-290` 의 `MergeListResults` / `MergeSearchResultsRoundRobin` 이 `JsonDocument.Parse → Clone → GetRawText → JsonSerializer.Deserialize<object> → Serialize` 4회 round-trip. 결과 정합 OK, 단 비효율 + 가독성 낮음.

#### 위치
- `Apps/Promaker/Promaker/LlmAgent/Tools/LightHouseTools.cs`
  - `MergeListResults` (~218-247)
  - `MergeSearchResultsRoundRobin` (~250-292)
  - `PrefixFileIdsInListResponse` (~296-322)
  - `PrefixFileIdsInSearchResponse` (~328-365)

#### 권장 path
1. `using System.Text.Json.Nodes;` 추가
2. `JsonNode.Parse` 로 일관 처리 — Clone 무요 (mutable tree)
3. 각 helper 내부에서 `JsonArray` / `JsonObject` 만 사용, raw text 변환은 마지막 1회 (`.ToJsonString(JsonOptions)`)
4. m11 test 가 이미 박제된 후 진행 → 회귀 가드 통과

#### 영향
- 빠른 단순화 (~50 line 감소 예상)
- 성능 4x round-trip 제거 (대용량 list/search 응답 시 의미)
- 단 logic 동일성 검증 — m11 test 가 SSOT

---

### B4. **SecureString 전체 chain** (메타리뷰 M4 정공, 우선순위 ★)

#### 현상
PSK / Cert PFX password 가 UI dialog → installer → ps1 temp file 전 chain 에서 **managed string** 으로 박제. heap 평문 잔존 risk. 현 commit 의 M4 처리 = 주석 격하만 (정공은 별 phase 로 박제).

#### 위치
- `Apps/Promaker/Promaker/Dialogs/EnableLocalServiceDialog.xaml.cs` (PasswordBox.Password → 변경 SecurePassword)
- `Apps/Promaker/Promaker/Services/LightHouseLocalInstaller.cs:46` (EnableAsync 시그니처 — string → SecureString 또는 byte[])
- `Apps/Promaker/Promaker/Dialogs/Settings/ApplicationSettingsDialog.xaml.cs:LhEnableLocal_Click` (caller)
- `Solutions/Tools/Ds2.LightHouseService/scripts/enable-ai.ps1` (temp file read — 그대로 유지 가능)

#### 권장 path
1. `EnableLocalServiceDialog` 의 `PskResult` / `CertPwdResult` 를 `SecureString?` 으로 변경 (`PasswordBox.SecurePassword` 사용)
2. `LightHouseLocalInstaller.EnableAsync(SecureString psk, SecureString certPwd, CancellationToken ct)` overload 추가 — 기존 string overload `[Obsolete]` 마킹 또는 폐기
3. Installer 안에서 `Marshal.SecureStringToGlobalAllocUnicode` → UTF-8 byte[] → File.WriteAllBytes (Owner-only ACL temp) → finally `Array.Clear` + `Marshal.ZeroFreeGlobalAllocUnicode`
4. caller (ApplicationSettingsDialog) 도 SecureString 으로 전달
5. ps1 측은 변경 0 (temp file read 그대로)

#### 주의
- SecureString 은 Windows-only (Linux/macOS 미지원, Promaker 는 WPF 라 무관)
- 평문 시연 path (test / debug log) 없는지 확인 — fail-fast assertion 박제
- 메모리 dump 시 평문 노출 차단은 Windows DPAPI 한계상 완전 보장 어려움 (best-effort)

---

## 3. 주의 사항 (모든 backlog 공통)

### W1 — 사용자 의도 verbatim
사용자가 `--transfer` 산출의 backlog 4건 모두 처리 의도. 진입 시 어느 항목 먼저 진행할지 사용자 결정 의뢰.

### 자가검열 trigger
B1 / B2 / B3 / B4 각각 다음 충족 시 sub-agent 위임 의무:
- 단일 파일 100+ line 변경
- 시그니처 변경 (B1 의 ExecuteWithSessionRetryAsync, B4 의 EnableAsync)
- 신규 type / 함수 3+ 신설
- public API 갱신

### build 검증
모든 변경 후 `dotnet build Apps/Promaker/Promaker.sln -c Debug --nologo -v q` 의무 — Promaker.exe 실행 중이면 file lock 으로 fail (사용자에게 종료 안내).

### chat E2E 검증
B1 / B3 변경 후 사용자 측 chat 1회 시도 권장 — log 의 service 측 RequestTrace 가 정상 정합 보임.

### Plan B 원칙 (변경 금지)
- Claude CLI 의 `.mcp-config` 에는 `promaker` 1개만 박제 (lighthouse 직접 박제 금지 — OAuth 사고 재발)
- LightHouseService 의 `attachment_*` 6종은 Promaker wrapper 의 fan-out 으로만 호출
- fileId prefix `<sid8>:<orig>` SSOT 유지 (LightHouseTools.cs:38 `ServiceIdPrefixLen`)

## 4. 진행 순서 권장

1. **B2 (test 신설)** 우선 — B1 / B3 의 회귀 가드 의무
2. **B1 (session pooling)** — 성능 + service storm 차단
3. **B3 (JsonNode refactor)** — pure cleanup, B2 의 test 가 SSOT
4. **B4 (SecureString)** — 보안 보강, 다른 backlog 와 독립

## 5. 처리 완료 history (참고)

- `abe0956b` — Plan B 진입 + cert/OAuth 진단 + 3 사고 fix (list prefix / search fileId / summary collectionId)
- `862216a1` — 메타리뷰 N=3 채택 (Critical 4 + Major 8 + Minor 10)

위 두 commit 의 message + 각 위치의 `**Plan B (2026-05-27)**` / `**메타리뷰 ... (2026-05-27)**` 주석으로 모든 결정 사유 추적 가능.
