# DSPilot Refactoring Plan — Maintainability & Architecture

> Scope: backend C# (ASP.NET Core) **+** frontend (Alpine.js/Chart.js static pages). Goal: **maintainability + architecture**. No rewrite, no framework migration, behavior-preserving.
> Produced by a 19-agent analysis workflow (15 subsystem maps + 4 cross-cut analyses), then reconciled against a completeness critique. Detailed evidence: [REFACTORING_FINDINGS.md](REFACTORING_FINDINGS.md).

## 1. Executive Summary

DSPilot is **structurally sound at the macro level** — the `Controller → Service → Repository/Adapter → SQLite + F# Ds2.Core` layering is real and mostly respected, background-service lifetimes avoid the classic captive-dependency trap, and the no-framework static frontend works. The debt is concentrated in two recurring patterns rather than rot:

1. **An incomplete Blazor → static-HTML migration** left ~1.5k LOC of dead frontend JS, a mostly-dead `Components/` Razor tree, and ~600 LOC of unreachable backend code (half of `CycleAnalysisService.cs`, all of `CallStatisticsTracker.cs`, `DiagnoseFlowDag`) still shipped.
2. **Systematic copy-paste of ~8 primitives** — SQLite connection strings, datetime parsing, the boundary-match SQL clause, repo guard-prologues (BE); the `apiGet`/503-demo wrapper, theme toggle, duration formatters, Chart.js lifecycle (FE) — each reimplemented in 8–10 places.

Three abstractions have eroded into liabilities: `IDspRepository` exposes only ~13 of ~31 public adapter methods (forcing concrete coupling + a runtime downcast at `SimulationEngineService.cs:640`); `IDatabasePathResolver` hardcodes `IsUnified => true` with three identical getters; and the F# store is reached twice via reflection (`GetField("_store")`) although the public `DsProjectService.GetStore()` already exists.

**The single biggest lever is a dead-code sweep followed by extracting the duplicated primitives** — both near-zero risk, both unblock the harder god-class decompositions (`SimulationEngineService` 821 LOC, `DspRepositoryAdapter` 1598 LOC, `DspDbService` 664 LOC, and the 1000–2600-line god Alpine pages).

## 2. Themes

| Theme | Rationale |
|---|---|
| **T1 — Dead-code sweep** | Delete unreachable Blazor-era JS/Razor + dead backend analysis subsystems. Immediate clarity, shipped-byte reduction, removes misleading "circuit"-era comments, makes later "is this dead?" questions moot. |
| **T2 — Extract duplicated primitives** | One shared helper per repeated primitive (connection factory, datetime, fetch/503, theme, duration format) removes 8–10× drift risk app-wide. |
| **T3 — Restore eroded abstractions** | Right-size `IDspRepository`, collapse the vestigial `IDatabasePathResolver`, kill the two reflection break-ins — re-enable testability, remove concrete coupling. |
| **T4 — Centralize data access** | Pull raw SQLite/DDL out of services/controllers into repositories; one `SchemaInitializer` owns all DDL; stop running migration checks on hot read paths. |
| **T5 — Pull domain math out of controllers/god-services** | OEE/cycle/idle/gap math belongs in services; controllers return to thin dispatchers. |
| **T6 — Decompose god classes/components** | Split the BE god services + FE god pages into composable collaborators — only *after* T2/T3 make it safe. |
| **T7 — Codify conventions in one place** | The fragile InTag/OutTag polarity rule, SignalR event names, and the tz contract each live in one named location instead of scattered comments. |

## 3. Prioritized Backlog (reconciled)

Priorities: **P0** dead-code/safety baseline · **P1** low-risk high-leverage · **P2** domain-math + data-access centralization · **P3** god-class decomposition / deferred. Effort: S/M/L/XL.

### Phase 0 — Dead-code sweep (do first; near-zero risk)

| ID | Title | Scope | Problem (cited) | Proposal | Effort | Risk | Impact |
|---|---|---|---|---|---|---|---|
| **R1** | Delete dead Blazor-era frontend JS cluster | FE | `wwwroot/js/{flow-reorder, flow-trend-chart, flow-history-chart, gantt-crosshair, cycle-time-chart, canvas-interop, io-chart}.js` referenced only by `.razor` components; `App.razor:48-58` script tags. **(7 files, not 5 — corrected from critique G2.)** | Confirm no live Blazor host route, delete the 7 JS files + their `App.razor` script tags in one commit | M | low | high |
| **R28** | Inventory full `Components/` Razor tree vs route-rewrite dictionary | BE/FE | Only `FlowWorkspace.razor`/`PowerTools.razor` carry `@page`; **all 10 routes are rewritten** to static `*.html` (`Program.cs:252-264`), so ~12 `Components/` files' liveness is unconfirmed (`FlowLayoutSvg`, `IoDataChart`, `CallDirectionWidget`, layout/shared shell). **(Largest un-mapped surface — critique G3.)** | Grep each component against live routes + `App.razor`/`Routes.razor` compile graph; delete confirmed-dead in the R1 commit | M | low | high |
| **R2** | Delete dead backend analysis subsystems | BE | `CycleAnalysisService.cs:42-307,896-1180` (no live REST caller — incl. `GetIOEventsInTimeRangeAsync`, corrected per critique G7; this is **one strongly-connected dead subgraph**, delete together); `Statistics/CallStatisticsTracker.cs:1-112` (doc/20 says 사용 금지); `DiagnosticTool.cs` `DiagnoseFlowDag`; `DsStoreExtensions.cs` stub | Final caller-grep each, delete as a connected unit; cuts ~half of `CycleAnalysisService` + a dead N+1 path | M | low | high |
| **R7** | Delete reflection break-ins to F# store | BE | `FlowMetricsService.cs:561-578` (`GetField("_store")`) and `DiagnosticTool.cs:111-124`, while `DsProjectService.cs:34` already exposes public `GetStore()` | Replace both with `GetStore()`; the `FlowMetricsService` half is **independent of R2** and can ship immediately | S | low | med |

### Phase 1 — Shared primitives & restored abstractions (low-risk, high-leverage)

| ID | Title | Scope | Problem (cited) | Proposal | Effort | Risk | Impact |
|---|---|---|---|---|---|---|---|
| **R3** | Shared `dsp-api.js` fetch/503-demo helper | FE | `apiGet`/`apiPost`/`demoBlocked` duplicated in 8 pages (`cctv.html:840`, `uptime.html:795`, `settings.html:422`, `dashboard2.html:1002`…); 503 branch 22× | `wwwroot/js/dsp-api.js` (`dspApiGet/Post/Blob`); spread into each `xxxApp()`; route raw `fetch`es through it | M | low | high |
| **R4** | Shared `theme.js` + extract design tokens | FE | Theme toggle in `dashboard2.html:959`, `shell.js:42,379`, all pages; `:root`/`.dark-theme` + base components inline (`dashboard2.html:114-481`) | `wwwroot/js/theme.js` loaded first; lift tokens → `dash-tokens.css`, base components → `dash-components.css`; delete dead inline tailwind config | L | med | high |
| **R5a** | `SqliteConnectionFactory` | BE | Conn-string literal in ~13 sites (`DspRepositoryAdapter.cs:30`, `OeeRepositoryAdapter.cs:37`, `UserTagAlertRepository.cs:28`, `OeeController.cs:362`, `PlcDebugService.cs:89…`, `SimulationEngineService.cs:268…`) | `SqliteConnectionFactory.OpenReadOnly/OpenReadWrite` delegating to `DatabaseConfigLoader.CreateConnectionString`; preserve WAL/BusyTimeout shape. **(Split from datetime work — critique O5.)** | M | low | high |
| **R5b** | Consolidate datetime parsers (named variants) | BE | 3 rival parsers vs `SqliteDateTimeHelpers.cs:13-29` — the "9h 밀림" tz-drift class | Fold into `SqliteDateTimeHelpers` as **explicitly named** `ParseUtcNoSuffix` / `ParseLocalRoundtrip` (never one "smart" parser); add before/after round-trip assertions; keep history DTOs on `SpecifyKind(Utc)` | M | med | high |
| **R6** | Right-size `IDspRepository`; delete downcast | BE | Interface 13 members (`IDspRepository.cs:9-92`) vs adapter ~31; concrete injection in `FlowMetricsService`, `CycleRecomputeService`, `DatabaseLifecycleService`, `DashboardController`; downcast `SimulationEngineService.cs:640` | Split into `IDspRepository` + `IFlowHistoryRepository` + `IFlowStatsMaintenance`; inject interfaces; delete downcast | M | med | high |
| **R8** | `ApiTime` + `ProjectStructureQuery` for controllers | BE | tz helpers re-implemented per controller (`CallTestController.cs:411`, `CycleAnalysisController.cs:266`, `PlcDebugController.cs:288`, `OeeController.cs:403`, `DashboardController.cs:160`); flow-flatten + head/tail forked across controllers | One `ApiTime` static; `ProjectStructureQuery` service (`GetOrderedFlows/GetCallToFlowMap/FindFlow`) | M | low | med |
| **R9** | Cache `AppSettingsService` + section-scoped writes | BE | Singleton, zero cache; `LoadSettings()` re-reads 2 files + 11 deserialize per call (`:44-73`) on hot paths (`HeatmapService.cs:237`, `CctvMediaMtxService.cs:85` every 30s, 19 sites); read-modify-write-all-sections race (`:75-101`) | Cache parsed model, rebuild on save, lock read+write; add `UpdateCctv/Shift/HistoryView`; generic `UpsertByFlowName<T>`; public API unchanged | M | med | high |
| **R25** | Move `DeleteDatabase` out of `AppSettingsService` | BE | `AppSettingsService.cs:265` deletes plc.db/-wal/-shm + `ClearAllPools()`, called from `DatabaseLifecycleService.cs:146` — DB-lifecycle code in a settings reader. **(Dropped finding — critique G4.)** | Move to `DatabaseLifecycleService`; `AppSettingsService` becomes pure load/save | S | low | med |
| **R22** | `MonitoringEvents` constants + shared JS event names | Both | Stringly-typed `SendAsync("DatabaseRebuilt"/"ShiftChanged"…)` across `MonitoringBroadcastService.cs:61`, `MonitoringHub.cs:82`, controllers; JS literals in `flow.html:1186`, `dashboard2.html:1060`, `uptime.html:908` | `MonitoringEvents` `const string` class; consume from all `SendAsync`; delete dead hub group/subscribe machinery (`MonitoringHub.cs:42-89`) | S | low | med |
| **R12a** | Drop hot-path `EnsureIsIdleColumnAsync` migration | BE | Invoked on **9** read/aggregate paths (`DspRepositoryAdapter.cs:843,894,941,972,1004,1161,1224,1458,1565`) → a `pragma_table_info` round-trip on every history read forever. **(Promoted to P1 — critique mis-priority.)** | Run the migration once at startup; delete per-call checks | S | med | high |
| **R29** | Spike: is `PlcDatabaseMonitorService`'s `CallStateChanged` consumed? | BE | Two registered tag→state pipelines (`Program.cs:121,131`); SignalR map found per-call group machinery is dead — broadcast may have no consumer | One-grep spike for JS `conn.on('CallStateChanged')` + engine coverage; may **downgrade R21 to a dead-code delete** | S | low | med |
| **R13** | `time-format.js` — one duration formatter | FE | `formatMs`/`fmtDur`/`hms`/`durShort` in `flow.html:2186`, `cycle-time-analysis.html:1400`, `heatmap.html:689`, `call-history-chart.js:16`; `1000/60000/3600000` repeated | `wwwroot/js/time-format.js` `fmtDuration(ms,{short,precision})` + ms consts | S | low | med |
| **R16** | `chart-theme.js` — shared token/lifecycle helpers | FE | `cssVar`/`themeChartColors`/`destroyIfExists` repeated in `user-tag-trend-chart.js:8`, `flow-trend-chart.js:17`, `call-history-chart.js:6`, `plc-debug.js:51` | Shared `wwwroot/js/chart-theme.js`; delete dead `renderLevelDoughnut` (`user-tag-trend-chart.js:183-227`) | S | low | med |

### Phase 2 — Pull domain math down & centralize data access

| ID | Title | Scope | Problem (cited) | Proposal | Effort | Risk | Impact |
|---|---|---|---|---|---|---|---|
| **R10** | Extract OEE math + raw SQL from controllers | BE | `OeeController.cs:210-342` (A/P/Q/OEE/MTBF/MTTR), `:355-390` opens its own `SqliteConnection` — only controller doing data access | New `OeeCalculationService`; add `CountFlowHistoryAsync` to `IOeeRepository`; controller → dispatcher | M | med | high |
| **R11** | Move CallTest/CycleAnalysis interval/idle/gap math to services | BE | `CallTestController.cs:346-406` + god-action `Load:83-184`; `CycleAnalysisController.cs:164-264` (`BuildIdleRegionsFromEdges`/`CalculateTopGaps`) | Finish the extraction into `CycleDerivation`-style pure helpers; split `Load` into `BuildLanes/ResolveHeadTail/AssembleDto` | M | med | med |
| **R12b** | Extract one `SchemaInitializer` (DDL ownership) | BE | Dual schema owners: indexes created in both `DspRepositoryAdapter.cs:270-272` and `PlcRepository.cs:384-385` | One `SchemaInitializer` owns all `CREATE TABLE`/index/`ALTER`; change **ownership only, not index definitions** | L | med | med |
| **R14** | `gantt-svg.js` — `computeCycleSpans` + palette | FE | Span/tail walk triplicated per page (`flow.html:1716/2050/2121`; `cycle-time-analysis.html:949/1254/1329`), divergent rounding; hex literals vs CSS | `computeCycleSpans(...)` + `GANTT_COLORS` shared by ribbon/bands/list. **Guardrail: `CycleTimeChartExporterTests.cs` pins the server-side `CycleExcelModel` shape — keep it green (critique G1).** | L | med | med |
| **R15** | `ct-donut.js` + standardize Chart.js update-in-place | FE | `dashboard2.html:1478-1566` `renderCtDonut` (90-line dual create/update, **OOM hotspot per MEMORY**); `io-chart.js:120-127` dead `updateChart` | Extract `ct-donut.js createOrUpdate(canvas,model)` owning chart in a **closure `let`** (never reactive state); standardize on `chart.update('none')` | M | med | high |
| **R31** | Standardize controller error/success envelope | BE | `BadRequest(string)` vs `BadRequest(new{error})` vs `NotFound(new{message})` → FE handles 3+ shapes; couples to R3's parser. **(Dropped finding — critique G8.)** | One `ApiError`/`ApiResult` record + `Fail/Ok` extensions; apply incrementally | M | low | med |
| **R17a** | Move raw plcTag upsert into a repository | BE | `SimulationEngineService.cs:272,338` duplicate `INSERT…ON CONFLICT` SQLite DDL/DML inside the engine | Extract `PlcTagRegistrar` → a `PlcTagRepository` method (the layering fix; the valuable half of R17) | M | med | med |

### Phase 3 — Decompose god classes/components (highest risk, now safe)

| ID | Title | Scope | Problem (cited) | Proposal | Effort | Risk | Impact |
|---|---|---|---|---|---|---|---|
| **R18** | Split `DspRepositoryAdapter` along interface seams | BE | 1598-LOC god class; owns DDL for tables it never queries (`:108-370`); `ExecuteIfEnabledAsync` guard copy-pasted ~25×; `BoundaryMatchClause` 4–9× (`:1465-1534`) | After R12, split into `FlowRepository`/`FlowHistoryRepository`/`FlowStatsMaintenance`; extract `ExecuteIfEnabledAsync<T>` + `const BoundaryMatchClause` + `MapAddressRow` | XL | high | med |
| **R27** | Decompose `DspDbService` + fix constructor-lifecycle | BE | `:75-98` launches 2 timers + sync `TryRefresh()` in ctor; `TryRefresh` 200-line method; 7× immutable clone-with-one-field. **(Peer god-class, was absent — critique G6.)** | Convert to `IHostedService`; extract `MergeProgress`/`ProtectGoingCount`/`DetectChanges`; make state types `record` | L | high | med |
| **R17b** | Engine Welford/debounce collaborator split | BE | `SimulationEngineService` Welford accumulator + fire-and-forget debounce (`Task.Delay().ContinueWith` + manual CTS) | Extract `CallStatsAccumulator` + `FlowSyncDebouncer` (lower-value half of R17; **confirm one accumulator, not reviving dead `CallStatisticsTracker`**) | M | high | low |
| **R19** | FE god-component pure-helper extraction (descoped) | FE | `flow.html:994-2613` (`flowApp` ~1620), `dashboard2.html`, `cctv.html`, `cycle-time-analysis.html`, `uptime.html` god `x-data` | **Descoped (critique O1):** extract only the *pure* helpers (covered by R14/R15) into testable modules; **defer the full object-spread state-mixin split** until those prove the pattern — XL + zero FE tests + Alpine-reactivity foot-guns make the full split speculative | L | med | med |
| **R20** | Collapse vestigial split-DB abstraction | BE | `DatabasePathResolverAdapter.cs:15,24-28` (`IsUnified=>true`, 3 identical getters); `DspDbService` split-mode fields; orphan `IPlcHistorySource.cs` | Collapse to single `GetSharedDbPath()`; delete `IsUnified`/split getters; delete `IPlcHistorySource` after grep | S | low | low |
| **R21** | Consolidate tag-change → CallState pipeline + polarity helper | BE | Two pipelines (`PlcDatabaseMonitorService.cs:100-192` vs `SimulationEngineService.cs:181-222`), both registered; polarity contradictory per MEMORY | **Gated on R29.** If consumer exists: confirm engine authoritative, demote monitor to pure ingester, centralize start/finish edge selection on `CycleDerivation`; **never falling-edge completion (rising ON = canon)** | M | high | med |
| **R23** | Trim `Program.cs` (descoped) | BE | `:185-203` temp uploads middleware ("원인 파악 후 제거"); `:341-390` connstring parser duplicating `DatabaseConfigLoader.cs:25-61` | **Descoped (critique O3):** delete temp middleware + dedupe connstring parsing only. **Leave demo-gate/route-rewrite middleware inline** — small, legible, self-documenting revert hatch | S | low | med |
| **R24** | `JsonFileStore<T>` for file-backed services | BE | `CctvOverlayService.cs:23-34/127-154` and `BlueprintService.cs:17-27/273-306` duplicate dir-create/Load/Save/lock; BlueprintService timer race `:267-271` | Extract `JsonFileStore<T>`; both compose it; add locking to BlueprintService | M | low | low |
| **R30** | `IConnectionNudge` seam for `MonitoringHub` | BE | `MonitoringHub.cs:13-31` holds concrete `HubSubscriberService` only to call `NudgeConnectAsync()`. **(Dropped finding — critique G9.)** | One-method `IConnectionNudge` implemented by `HubSubscriberService`, injected into the hub | S | low | low |
| **R26** | Converge dual config onto `IOptions<T>` (deferred) | BE | Raw `IConfiguration` reads (`HubSubscriberService.cs:94`, `DspDbService.cs:65`, `OeeDowntimeStateMachine.cs:54`) vs `AppSettingsModel`; `IOptions` used once. **(HIGH cross-cut finding, consciously deferred — critique G5.)** | Bind `HubSettings`/`DatabaseSettings`/`OeeSettings` via `IOptionsMonitor`, one section at a time | L | med | med |

## 4. Recommended Sequencing

- **Phase 0 (R1, R28, R2, R7)** — delete unreachable Blazor JS/Razor + dead backend analysis (+ the cheap reflection fix). Shrinks the surface and is the **safety baseline**: each delete in isolation, verify boot + `/app/*` render, build on a clean trunk.
- **Phase 1 (R3, R4, R5a, R5b, R6, R8, R9, R25, R22, R12a, R29, R13, R16)** — establish shared primitives + restore abstractions. `R5a`/`R6` are prerequisites for the god-class and controller work. The FE helpers (R13/R16) and R12a are cheap, high-value wins.
- **Phase 2 (R10, R11, R12b, R14, R15, R31, R17a)** — pull domain math into services; give DDL one owner; FE format/gantt/donut helpers. Controllers return to thin dispatchers.
- **Phase 3 (R18, R27, R17b, R19, R20, R21, R23, R24, R30, R26)** — decompose god classes/components last, one extraction per commit, because they touch the realtime/AASX-writeback hot paths.

**Ordering invariants:** abstractions (R5a/R6) before god-class splits (R17/R18/R27); dead-code (R1/R2/R28) before everything; shared FE helpers (R3/R4/R13/R14/R15) before any FE component decomposition (R19); the R29 spike before R21.

## 5. Quick Wins (<1 day each)

- **R7 (FlowMetricsService half)** — replace `GetField("_store")` reflection with existing `GetStore()`. Compiler-checked; removes a runtime foot-gun.
- **R2 (partial)** — delete `Statistics/CallStatisticsTracker.cs` (zero refs, doc says 사용 금지) + `DsStoreExtensions.cs` stub.
- **R13** — single `time-format.js` replacing 4–7 duplicated formatters.
- **R16** — delete dead `renderLevelDoughnut` (`user-tag-trend-chart.js:183-227`); extract `chart-theme.js`.
- **R22** — `MonitoringEvents` const class + delete dead hub group/subscribe methods.
- **R12a** — drop the 9× per-read `EnsureIsIdleColumnAsync` migration (measurable perf win).
- **R20 (partial)** — delete orphan `IPlcHistorySource.cs` + no-op `CleanupDatabaseAsync` (`DspRepositoryAdapter.cs:631`) after a grep.
- **FE dead-helper sweep** — `dashboard2.html:1127-1147` (`flowColor`/`stateColor` dupes), `cctv.html:1053-1064`, `heatmap.html:681-687`, `PlcRepository.cs:746-751`.

## 6. Global Risks & Guardrails

- **Tests are thin but not absent.** Two unit tests exist: `DSPilot.Tests/HubSignalProcessorTests.cs` **and** `DSPilot.Tests/CycleTimeChartExporterTests.cs` (6 cases pinning `CycleExcelModel`). Treat the **delete-dead-code commits as the safety baseline**, verify boot + `/app/*` render (Playwright per MEMORY `project_dspilot_ui_inspection`, port 80, `.demo-bypass`), and **keep `CycleTimeChartExporterTests` green when touching R14 / any exporter DTO**. Add targeted tests around each newly-extracted pure helper (R10/R11/R14).
- **Blazor-host confirmation gate (R1/R2/R28).** Before deleting, confirm no live route serves the Blazor host: `Program.cs:252-264` rewrites all 10 routes to `*.html` and `:290` still `MapRazorComponents`. **Grep every rewritten route, not just `/flow`**, before deleting `App.razor` script tags and `Components/`.
- **2-writer AASX conflict (R6/R9/R17/R27).** Per MEMORY `project_dspilot_aasx_writeback`, AASX write-back + Promaker is a known 2-writer hazard. When touching `FlowMetricsService`/`DatabaseLifecycleService`/`AppSettingsService`/`DspDbService`, **do not change the AASX reload/resync ordering or the SHA256-debounce** in `AasxFileWatcherService`; keep `SemaphoreSlim` semantics identical.
- **SQLite pooling (R5a/R12/R18).** Per MEMORY `reference_sqlite_pooling_last_insert_rowid`, handle pooling makes `last_insert_rowid` unreliable — preserve existing `RETURNING` usage and the WAL/`BusyTimeout` connection-string shape when centralizing. `SchemaInitializer` changes **ownership, not index definitions** (verify `idx_plcTagLog_tagId_id` before consolidating).
- **Timezone drift (R5b/R8).** The "9h 밀림" class (MEMORY `reference_dspilot_datetime_tz_conventions`) means the two conventions (CallTest local-ISO vs dashboard-history UTC) must both survive — expose **explicitly named variants** (`ParseUtcNoSuffix` / `ParseLocalRoundtrip`), not one "smart" parser; keep history DTOs on `SpecifyKind(Utc)`.
- **Chart.js lifecycle (R15/R16/R19).** MEMORY `project_dspilot_main_screen_oom` + `reference_alpine_reactive_chartjs_update_crash` are load-bearing: OOM root cause was destroy/recreate churn, and a chart on a reactive Alpine field crashes on the *second* update. **Keep chart instances in closure `let` (histCache pattern), never on reactive state; preserve `chart.update('none')`.** JS reads `getComputedStyle(documentElement)`, so palette tokens must stay in `:root` when extracting `dash-tokens.css` (R4).
- **Polarity rule (R21).** InTag/OutTag polarity is contradictory between live engine and analysis (MEMORY `project_dspilot_intag_outtag_polarity_conflict`). Centralizing is valuable but **high-risk** — gate on the R29 spike, treat the engine path as authoritative, never introduce falling-edge completion (completion = rising ON is canon).
- **DI lifetime changes (R9/R17/R27).** Promoting scoped repos to singleton / converting to hosted services is safe only if connection-per-call statelessness holds — verify no controller relies on per-request disposal before changing lifetimes.
- **Solution sync rule.** Per project `CLAUDE.md`, any `.csproj` add/remove (e.g. a new test project) must be mirrored between `Apps/DSPilot/DSPilot.sln` and `solutions/Ds2.sln`.

---

_Appendix with full per-subsystem evidence: [REFACTORING_FINDINGS.md](REFACTORING_FINDINGS.md)._
