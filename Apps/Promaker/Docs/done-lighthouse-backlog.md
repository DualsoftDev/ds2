# LightHouse Plan B 잔여 backlog

본 문서는 `--transfer` 산출 — 새 Claude Code session 이 진입 시 본 문서 1개만 read 후 작업 시작.
처리 완료 항목은 별 git log + 코드 주석으로 추적 — §5 참조.

## 0. 진입 절차

```bash
git log --oneline 0eb04eca 2549e597 19a6e536 f720fa12 a6115340 4e4ba44b 862216a1
# 위에서 아래 순: PR1 / B5+B6+B7 / backlog doc / B4 / B1 / B2+B3 / 메타리뷰 N=3
```

핵심 SSOT (read 권장):
- `Apps/Promaker/Promaker/LlmAgent/Tools/LightHouseTools.cs` (LightHouse mcp wrapper, B3 후 JsonNode 단일 path)
- `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs` (mcp JSON-RPC + B1 session pooling + B7 SemaphoreSlim Dispose)
- `Apps/Promaker/Promaker/Services/LightHouseLocalInstaller.cs` (SecureString chain, B4+B5 후 PtrToStringUni 제거)
- `Apps/Promaker/Promaker/Dialogs/Settings/ApplicationSettingsDialog.xaml.cs` (PR2 후 _pskChanges Dictionary<string,SecureString>)
- `Apps/Promaker/Promaker/Dialogs/PskEditDialog.xaml.cs` (PR2 후 SecureString Result)
- `Solutions/Tests/Promaker.Tests/LighthouseToolsTests.cs` / `LightHouseClientTests.cs` / `LightHouseClientHolderTests.cs` / `LightHouseLocalInstallerTests.cs` (회귀 가드 SSOT)

## 1. 배경 / 맥락 (최소)

- Anthropic Claude CLI 의 mcp HTTP/SSE transport 가 OAuth 2.1 강제 (`_authThenStart`) — 단순 Bearer PSK 와 fundamental mismatch.
- 우회 = **Plan B**: Promaker 자체 MCP host (`mcp__promaker__*`) 가 lighthouse 의 attachment_* 6종을 fan-out wrapper 로 노출.
- 본 base 의 핵심 7축 (B1 session pooling / B2 test / B3 JsonNode / B4 SecureString / B5 SetLightHousePsk(SecureString) / B6 SkippableFact / B7 SemaphoreSlim Dispose) + 2 PR (PR1 SkippableFact 확장 / PR2 _pskChanges SecureString chain) 모두 완성.

## 2. 잔여 backlog (1건 — 추가 시 신설)

### B8. **LightHouseClient.PSK provider SecureString migrate** (PR2 잔여, 우선순위 ★)

#### 현상
`LightHouseClient` ctor 의 `pskProvider: Func<string?>` 가 매 요청마다 평문 string 반환. `ApplicationSettingsDialog.LhTestConnection` (line ~822) 의 `LightHouseLocalInstaller.SecureStringToManagedString` 변환이 *마지막 1지점 lifetime 한계* — SecureString → managed string (immutable, GC 잔존).

#### 위치
- `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs` — ctor + `_pskProvider` field + `NewRequest` 의 `pskProvider() → AuthenticationHeaderValue` (B6 보안 sweep 박제 path)
- `Apps/Promaker/Promaker/Dialogs/Settings/ApplicationSettingsDialog.xaml.cs:822` — LhTestConnection 1회 변환
- `Apps/Promaker/Promaker/Knowledge/LightHouseClientHolder.cs:143` — EnsureCreated 의 `() => config.GetLightHousePsk(capturedServiceId)` provider 박제

#### 권장 path
1. `LightHouseClient` ctor 의 `pskProvider` signature 변경: `Func<string?>` → `Func<SecureString?>`
2. `NewRequest` 의 PSK → AuthenticationHeaderValue 박제 path 가 SecureString → byte[] (UTF-8) → base64? → "Bearer " + string. 단 HttpClient `Authorization` 헤더는 string-only — 1회 변환 불가피. 단 lifetime = local var (즉시 GC).
3. `LlmConfig.GetLightHousePsk(serviceId)` overload 추가: `GetLightHousePskSecure(serviceId): SecureString?` 박제 (DPAPI Unprotect → byte[] → SecureString)
4. caller 3건 migrate:
   - `LightHouseClientHolder.EnsureCreated` (line 143)
   - `ApplicationSettingsDialog.LhTestConnection` (1회 변환 폐기)
   - 다른 LightHouseClient 직접 생성 caller (예: test) — 단순 wrapper

#### test 의무
- `GetLightHousePskSecure` round-trip
- `LightHouseClient` ctor SecureString overload — mock handler 가 Bearer 헤더 검증

#### 영향
- 보안: PSK 평문 lifetime 진정한 minimum (HttpClient Authorization 헤더 직전 1회 string 변환만)
- breaking: `LightHouseClient` public API 변경 — 외부 caller 검토
- 분량: ~80-150 LOC (LlmConfig overload + LightHouseClient ctor + LightHouseClientHolder + caller migrate + test)

---

## 3. 주의 사항 (모든 backlog 공통)

### W1 — 사용자 의도 verbatim
사용자가 진입 시 본 backlog 처리 의도. 어느 항목 먼저 진행할지 사용자 결정 의뢰.

### 자가검열 trigger
B8 의 충족 trigger:
- public API 갱신 (`LightHouseClient` ctor 시그니처 변경)
- 시그니처 변경 (`LlmConfig.GetLightHousePskSecure` overload)
- 단일 파일 100+ line 변경 가능성

### build 검증
모든 변경 후 `dotnet build Apps/Promaker/Promaker.sln -c Debug --nologo -v q` 의무. Promaker.exe 실행 중이면 file lock 으로 fail.

### chat E2E 검증
B8 변경 후 chat 1회 시도 권장 — Authorization 헤더 wire 정합.

### Plan B 원칙 (변경 금지)
- Claude CLI 의 `.mcp-config` 에는 `promaker` 1개만 박제 (lighthouse 직접 박제 금지 — OAuth 사고 재발)
- LightHouseService 의 `attachment_*` 6종은 Promaker wrapper 의 fan-out 으로만 호출
- fileId prefix `<sid8>:<orig>` SSOT 유지 (LightHouseTools.cs:38 `ServiceIdPrefixLen`)

### 신설 invariant (B1~B7 + PR1/PR2 결과)
- LightHouseClient 의 app session token 은 cached (`_cachedAppSessionToken`) — fan-out N×op storm 차단. 401/403 시만 무효화 + 재발급.
- LightHouseTools 의 4 helper (Merge*/Prefix*) 는 JsonNode 단일 path. raw text 변환은 마지막 1회.
- LightHouseLocalInstaller 의 `EnableAsync` 는 SecureString overload 만 신규 caller 진입. string overload 는 `[Obsolete]`.
- `LlmConfig.SetLightHousePsk(SecureString)` 가 정공 path. string overload 는 `[Obsolete]` — test 만 `#pragma warning disable CS0618` 박제 보존.
- ApplicationSettings 의 `_pskChanges: Dictionary<string, SecureString>` — Dispose lifecycle SSOT (`SetPendingPsk`/`RemovePendingPsk`/`DisposeAndClearPskChanges`).
- Windows-only test 는 `[SkippableFact]+Skip.IfNot` (Promaker.Tests 전체 — silent skip 0).

## 4. 진행 순서

본 잔여 1건 (B8) 만 — 우선순위 ★ (성능/보안 추가 개선이나 production 사고 risk 없음).

## 5. 처리 완료 history

| commit | 작업 |
|---|---|
| `0eb04eca` | **PR1** — SkippableFact 확장 3 파일 (ChildProcessTrackerTests/McpConfigWriterTests/LlmConfigTests 15건). Promaker.Tests 의 silent skip 0. |
| `2549e597` | **B5+B6+B7** — SetLightHousePsk(SecureString) overload + SkippableFact (LighthouseToolsTests/HolderTests 6건) + SemaphoreSlim Dispose. 자가 검열 M1/M2 fix (helper SSOT 통합 + dead code 제거). |
| `19a6e536` | backlog 문서 update — B1~B4 처리 완료 표시 + B5/B6/B7 신설 |
| `f720fa12` | **B4** — SecureString 전체 chain (EnableLocalServiceDialog/Installer/ApplicationSettings) |
| `a6115340` | **B1** — app session pooling (cached token + SemaphoreSlim + 4 test) |
| `4e4ba44b` | **B2+B3** — LighthouseToolsTests 신설 + 4 helper JsonNode 단순화 |
| `862216a1` | (earlier) 메타리뷰 N=3 채택 patch |
| `abe0956b` | (earlier) Plan B 진입 + cert/OAuth 진단 + 3 사고 fix |

위 commit message + 각 위치의 `**B1 (2026-05-27)**` ~ `**PR2 (2026-05-27)**` 주석으로 모든 결정 사유 추적 가능.
