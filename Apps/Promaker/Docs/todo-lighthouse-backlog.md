# LightHouse Plan B 잔여 backlog

본 문서는 `--transfer` 산출 — 새 Claude Code session 이 진입 시 본 문서 1개만 read 후 작업 시작.
처리 완료 항목은 별 git log + 코드 주석으로 추적 — §5 참조.

## 0. 진입 절차

```bash
git log --oneline f720fa12 a6115340 4e4ba44b 862216a1 abe0956b   # B4 / B1 / B2+B3 / 메타리뷰 / Plan B 진입 history
git show 4e4ba44b -- Apps/Promaker/Promaker/LlmAgent/Tools/LightHouseTools.cs       # B2+B3 정공
git show a6115340 -- Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs           # B1 session pooling
git show f720fa12 -- Apps/Promaker/Promaker/Services/LightHouseLocalInstaller.cs    # B4 SecureString chain
```

추가 read 권장:
- `Apps/Promaker/Promaker/LlmAgent/Tools/LightHouseTools.cs` (LightHouse mcp wrapper, fan-out, B3 JsonNode refactor 후)
- `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs` (mcp JSON-RPC + session pooling, B1 후)
- `Apps/Promaker/Promaker/Services/LightHouseLocalInstaller.cs` (SecureString chain, B4 후)
- `Solutions/Tests/Promaker.Tests/LighthouseToolsTests.cs` / `LightHouseClientTests.cs` / `LightHouseLocalInstallerTests.cs` (회귀 가드 SSOT)

## 1. 배경 / 맥락 (최소)

- Anthropic Claude CLI 의 mcp HTTP/SSE transport 가 OAuth 2.1 강제 (`_authThenStart`) — 단순 Bearer PSK 와 fundamental mismatch.
- 우회 = **Plan B**: Promaker 자체 MCP host (`mcp__promaker__*`) 가 lighthouse 의 attachment_* 6종을 fan-out wrapper 로 노출. Claude CLI 는 promaker MCP 1개만 직접 통신.
- 본 base 의 핵심 4축 (B1 session pooling / B2 test / B3 JsonNode / B4 SecureString) 은 `4e4ba44b / a6115340 / f720fa12` 에 완성. 본 backlog 는 그 위의 미세 polish 3건.

## 2. 잔여 backlog (3건 — 모두 ★ 단일 우선순위)

### B5. **LlmConfig.SetLightHousePsk(SecureString) overload** (B4 jest 잔여, 우선순위 ★)

#### 현상
`Apps/Promaker/Promaker/Services/LightHouseLocalInstaller.cs:` 의 `PersistLocalEntryFromSecureString` helper 가 DPAPI 박제 직전 1회 `Marshal.PtrToStringUni` → managed string 변환. GC heap 에 평문 잔존 (LlmConfig.SetLightHousePsk 시그니처 제약). B4 의 SecureString 정공 chain 의 *마지막 1지점 leak*.

#### 위치
- `Apps/Promaker/Promaker/Services/LightHouseLocalInstaller.cs` — `PersistLocalEntryFromSecureString` (~165-180)
- `Apps/Promaker/Promaker/LlmAgent/LlmConfig.cs:586` — `SetLightHousePsk(string serviceId, string? psk)` (DPAPI 박제 진입점)
- `Apps/Promaker/Promaker/LlmAgent/LlmConfig.cs:606` — `SetLightHousePsk(string? psk)` (EnsureActiveService 위임)

#### 권장 path
1. `LlmConfig.SetLightHousePsk(string serviceId, SecureString psk)` overload 신설 — 내부에서 `SecureStringToUtf8Bytes` (LightHouseLocalInstaller 의 internal helper 격상 또는 별 utility 모듈 추출) → DPAPI 의 `ProtectedData.Protect(byte[])` 박제 후 Array.Clear
2. `PersistLocalEntryFromSecureString` 를 `SetLightHousePsk(serviceId, SecureString)` 직접 호출로 단순화 + `PersistLocalEntry(string)` 호출 path 폐기
3. 기존 `SetLightHousePsk(serviceId, string)` 은 `[Obsolete]` 마킹 (다른 caller 호환 — `LightHouseClientHolderTests` 등)

#### test 의무
- `SetLightHousePsk(SecureString)` round-trip — DPAPI Protect/Unprotect 후 원본 일치
- 빈 SecureString 거부 또는 빈 PSK 박제 (기존 string overload 와 contract 동일성)

#### 영향
- 평문 lifetime *완전 제거* (UI dialog → installer → DPAPI 까지 SecureString/byte[] only)
- LlmConfig public API 변경 — 외부 caller (e.g. Settings dialog 의 service 추가 path) 영향 검토 필요

---

### B6. **`OperatingSystem.IsWindows()` skip → `SkippableFact` 정공화** (B2 m11 자가 검열 Minor-2, 우선순위 ★)

#### 현상
`Solutions/Tests/Promaker.Tests/LighthouseToolsTests.cs:262-286` 의 `FindClientByPrefix_*` / `LighthouseSummary_prefix_미매칭_시_error_응답` 등이 `if (!OperatingSystem.IsWindows()) return;` 으로 silent skip. Linux CI 환경에서 test 가 "통과" 로 잘못 집계 — false-positive risk.

#### 위치
- `Solutions/Tests/Promaker.Tests/LighthouseToolsTests.cs` — Windows-only test 5건 (`if (!OperatingSystem.IsWindows()) return;` 박제)
- `Solutions/Tests/Promaker.Tests/LightHouseClientHolderTests.cs` — 동일 pattern 4건 (기존)

#### 권장 path
1. `Xunit.SkippableFact` package 추가 (`Promaker.Tests.csproj`)
2. `[Fact]` → `[SkippableFact]` 교체 + `if (!OperatingSystem.IsWindows()) return;` → `Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only (DPAPI / WPF)");`
3. test runner 가 "skipped" 로 집계 → CI 가 skipped count alert 가능

#### test 의무
- 본인 — 변경 후 Windows 환경에서 기존 test 동일 결과 (`통과: N`)

#### 영향
- false-positive 제거 (CI 환경 안전)
- 단 backlog (m11) test 5건 + LightHouseClientHolderTests 4건 = 9건 변경 — 단순 mechanical edit

---

### B7. **SemaphoreSlim Dispose pattern 정합화** (B1 자가 검열 Minor-1, 우선순위 ★)

#### 현상
`Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs` 의 `_appSessionLock` (B1 신설) + 기존 `_mcpInitLock` (CR3) 두 SemaphoreSlim 가 `Dispose()` 메서드에서 dispose 안 됨. finalizer pressure 미세하나 SonarQube / FxCop 류 정적 분석에서 warning. B1 자가 검열에서 "기존 pattern 정합 — 비파괴" 로 Minor 등급.

#### 위치
- `Apps/Promaker/Promaker/Knowledge/LightHouseClient.cs:191-194` — `Dispose()` (현재 `_http.Dispose()` 만)
- 동일 파일 :46 (`_appSessionLock`) / :332 (`_mcpInitLock`)

#### 권장 path
1. `Dispose()` 안에 두 SemaphoreSlim 의 `Dispose()` 호출 박제
2. `_disposed` flag 박제 (double-dispose 방어) — 또는 SemaphoreSlim 의 `Dispose()` 가 idempotent 이므로 단순 호출
3. **주의**: SSE loop (`StartSseLoopLocked`) 가 다른 task 에서 `_mcpInitLock` 사용 중일 가능성 — `LightHouseClientHolder.DisposeAllAsync` 의 SSE cancel 후 호출 정합 검증

#### test 의무
- 기존 `LightHouseClientTests` / `LightHouseClientHolderTests` 의 disposal path 통과 — race 차단 검증
- (옵션) double-dispose 멱등성 unit test

#### 영향
- 정적 분석 warning 제거 (CA2213 / IDISP004 류)
- 비파괴 — 기존 동작 보존

---

## 3. 주의 사항 (모든 backlog 공통)

### W1 — 사용자 의도 verbatim
사용자가 `--transfer` 산출의 backlog 3건 모두 처리 의도. 진입 시 어느 항목 먼저 진행할지 사용자 결정 의뢰.

### 자가검열 trigger
B5 / B6 / B7 각각 다음 충족 시 sub-agent 위임 의무:
- 단일 파일 100+ line 변경 (B6 의 9건 mechanical edit 은 trigger 가능)
- 시그니처 변경 (B5 의 `LlmConfig.SetLightHousePsk` overload 신설 + 기존 `[Obsolete]`)
- public API 갱신 (B5)

### build 검증
모든 변경 후 `dotnet build Apps/Promaker/Promaker.sln -c Debug --nologo -v q` 의무 — Promaker.exe 실행 중이면 file lock 으로 fail.

### chat E2E 검증
B5 / B7 변경 후 사용자 측 chat 1회 시도 권장 — Settings 의 Local 서비스 활성화 path (B5) / session lock 정합 (B7).

### Plan B 원칙 (변경 금지)
- Claude CLI 의 `.mcp-config` 에는 `promaker` 1개만 박제 (lighthouse 직접 박제 금지 — OAuth 사고 재발)
- LightHouseService 의 `attachment_*` 6종은 Promaker wrapper 의 fan-out 으로만 호출
- fileId prefix `<sid8>:<orig>` SSOT 유지 (LightHouseTools.cs:38 `ServiceIdPrefixLen`)

### 신설 invariant (B1~B4 의 결과)
- LightHouseClient 의 app session token 은 cached (`_cachedAppSessionToken`) — fan-out N×op storm 차단. 401/403 시만 무효화 + 재발급.
- LightHouseTools 의 4 helper (Merge*/Prefix*) 는 JsonNode 단일 path. raw text 변환은 마지막 1회.
- LightHouseLocalInstaller 의 `EnableAsync` 는 SecureString overload 만 신규 caller 진입. string overload 는 `[Obsolete]`.

## 4. 진행 순서 권장

본 3건은 우선순위 동일 (★). 어느 순서로 진행해도 무관 — 단 B5 가 다른 caller 영향 가장 큼 (public API).

권장:
1. **B7 (SemaphoreSlim Dispose)** 가장 단순 — Dispose 메서드 1군데 + test 검증
2. **B6 (SkippableFact)** mechanical edit — package 추가 + 9건 `[SkippableFact]` 교체
3. **B5 (SecureString overload)** public API 변경 — 다른 caller 영향 검토 + test 박제

## 5. 처리 완료 history

| commit | 작업 |
|---|---|
| `4e4ba44b` | **B2+B3** — LighthouseToolsTests 신설 (28 test) + private helper internal 격상 + ExtractCollectionGuid 추출 + 4 helper JsonDocument → JsonNode 단순화 |
| `a6115340` | **B1** — app session pooling: _cachedAppSessionToken + SemaphoreSlim + EnsureAppSessionTokenAsync / InvalidateAppSessionTokenAsync helper + ExecuteWithSessionRetryAsync 의 cached/invalidate 분기 + test 4건 |
| `f720fa12` | **B4** — SecureString 전체 chain: EnableLocalServiceDialog (SecureString?) / LightHouseLocalInstaller.EnableAsync(SecureString,SecureString) 정공 overload / ApplicationSettingsDialog SecureString 전달 / 자가 검열 Critical-1 fix (early-throw 시 누수 차단) + test 4건 |
| `862216a1` | (earlier) 메타리뷰 N=3 채택 patch — Critical 4 + Major 8 + Minor 10 |
| `abe0956b` | (earlier) Plan B 진입 + cert/OAuth 진단 + 3 사고 fix |

위 commit message + 각 위치의 `**B1 (2026-05-27)**` / `**B2 (2026-05-27)**` / `**B3 (2026-05-27)**` / `**B4 (2026-05-27)**` 주석으로 모든 결정 사유 추적 가능.
