// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Text.Json;
using System.Text.Json.Nodes;
using DSPilot.Models;
using Microsoft.Data.Sqlite;

namespace DSPilot.Services;

public class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // 설정 파일 read-modify-write 직렬화 — 동시 저장(예: 백그라운드 자동보정 완료 플래그 박제 vs 설정 페이지 저장)의
    // lost-update·torn-write 방지. AppSettingsService 는 싱글톤이라 프로세스당 1개의 lock 으로 충분(파일은 단일 writer).
    private static readonly object _writeLock = new();

    private static readonly string[] ManagedSections =
        ["Database", "FlowCycle", "DspTables", "Hub", "Logging", "Ui", "HistoryView", "Cctv", "OeeSignals", "OeeManual", "Shift", "CycleExclusion", "AbnormalAlarm", "AutoCalibration", "EmailBriefing", "ExternalAccess"];

    private readonly string _filePath;
    private readonly string _productionFilePath;
    private readonly ILogger<AppSettingsService> _logger;

    public AppSettingsService(
        IWebHostEnvironment env,
        ILogger<AppSettingsService> logger)
    {
        _filePath = Path.Combine(env.ContentRootPath, "appsettings.json");
        _productionFilePath = Path.Combine(env.ContentRootPath, "appsettings.Production.json");
        _logger = logger;
    }

    /// <summary>
    /// appsettings.json이 없으면 AppSettingsModel 기본값으로 자동 생성.
    /// WebApplication.CreateBuilder 전에 정적으로 호출.
    /// </summary>
    public static void EnsureSettingsFiles(string contentRootPath)
    {
        var appSettingsPath = Path.Combine(contentRootPath, "appsettings.json");

        if (File.Exists(appSettingsPath))
            return;

        var defaultJson = JsonSerializer.Serialize(new AppSettingsModel(), JsonOptions);
        File.WriteAllText(appSettingsPath, defaultJson);
        Console.WriteLine("[AppSettings] appsettings.json 생성됨 (기본값)");
    }

    public AppSettingsModel LoadSettings()
    {
        var root = LoadRaw(_filePath);

        // Production.json이 있으면 각 섹션을 오버라이드 (ASP.NET Core 설정 병합과 동일 방식)
        if (File.Exists(_productionFilePath))
        {
            var prod = LoadRaw(_productionFilePath);
            foreach (var key in ManagedSections)
            {
                if (prod[key] is not null)
                    root[key] = prod[key]!.DeepClone();
            }
        }

        return new AppSettingsModel
        {
            Database = Deserialize<DatabaseSettings>(root["Database"]),
            FlowCycle = Deserialize<FlowCycleSettings>(root["FlowCycle"]),
            DspTables = Deserialize<DspTablesSettings>(root["DspTables"]),
            Hub = Deserialize<HubSettings>(root["Hub"]),
            Logging = Deserialize<LoggingSettings>(root["Logging"]),
            Ui = Deserialize<UiSettings>(root["Ui"]),
            HistoryView = Deserialize<HistoryViewSettings>(root["HistoryView"]),
            Cctv = Deserialize<CctvSettings>(root["Cctv"]),
            OeeSignals = Deserialize<OeeSignalSettings>(root["OeeSignals"]),
            OeeManual = Deserialize<OeeManualSettings>(root["OeeManual"]),
            Shift = Deserialize<ShiftSettings>(root["Shift"]),
            CycleExclusion = Deserialize<CycleExclusionSettings>(root["CycleExclusion"]),
            AbnormalAlarm = Deserialize<AbnormalAlarmSettings>(root["AbnormalAlarm"]),
            AutoCalibration = Deserialize<AutoCalibrationSettings>(root["AutoCalibration"]),
            EmailBriefing = Deserialize<EmailBriefingSettings>(root["EmailBriefing"]),
            ExternalAccess = Deserialize<ExternalAccessSettings>(root["ExternalAccess"]),
        };
    }

    public void SaveSettings(AppSettingsModel model)
    {
        lock (_writeLock)
        {
            var root = LoadRaw(_filePath);
            WriteSections(root, model);
            SaveRaw(_filePath, root);

            // Production.json에 사용자 설정 전체 동기화 — 재설치(업그레이드) 시 보존되는 사용자 설정 영속 저장소.
            // (설치 스크립트는 이 파일을 삭제/덮어쓰지 않고, 포트는 appsettings.Hosting.json 으로 분리한다.)
            var prod = File.Exists(_productionFilePath) ? LoadRaw(_productionFilePath) : new JsonObject();
            WriteSections(prod, model);
            SaveRaw(_productionFilePath, prod);
            _logger.LogInformation("appsettings.Production.json 전체 설정 동기화 완료");
        }
    }

    /// <summary>
    /// 현재 설정을 로드→변경→저장을 한 잠금 구간에서 원자적으로 수행한다(load-modify-save 의 lost-update 방지).
    /// 별도 경로의 mutator 가 같은 순간 서로의 변경을 덮어쓰는 것을 막는다 — 특히 백그라운드 자동보정 완료 플래그
    /// (<see cref="RecordAutoCalibrationApplied"/>)와 설정 페이지 저장(SettingsController.Save)이 경합해도
    /// CompletedAt 이 유실되지 않는다. <paramref name="mutate"/> 안에서는 await 하지 말 것(동기 잠금 구간).
    /// </summary>
    public void Update(Action<AppSettingsModel> mutate)
    {
        if (mutate is null) return;
        lock (_writeLock)
        {
            var settings = LoadSettings();
            mutate(settings);
            SaveSettings(settings); // 같은 스레드 재진입(Monitor 는 재진입 허용).
        }
    }

    /// <summary>
    /// 사용자가 직접 설정하는 전반 품질(양품률) %. null/범위밖이면 해제(→ 불량 입력 기반/100% 가정 폴백).
    /// 0~100 으로 클램프. <see cref="Update"/> 로 원자적 저장(설정 페이지 저장과 경합해도 유실 없음).
    /// </summary>
    public void SaveManualQualityPercent(double? qualityPercent)
    {
        var normalized = qualityPercent is double p ? Math.Clamp(p, 0.0, 100.0) : (double?)null;
        Update(settings => settings.OeeManual.QualityPercent = normalized);
    }

    /// <summary>
    /// 수동 지정 비생산 시간대 저장. 정규화: 분 클램프(0~1440)·End&gt;Start 만 유지·시작분 정렬.
    /// 병행 모델(2026-07-08): 당일 자동 판정(10×CT)은 항상 켜져 있고, 지정 시간대는 <b>추가로 무조건 비생산</b>으로
    /// 자르는 보조 규칙 — 저장이 자동 판정을 끄지 않는다(구 배타 토글 폐기).
    /// <see cref="Update"/> 로 원자적 저장(설정 페이지 저장과 경합해도 유실 없음).
    /// </summary>
    public void SavePlannedStops(IReadOnlyList<PlannedStopWindow>? windows)
    {
        var normalized = (windows ?? new List<PlannedStopWindow>())
            .Select(w => new PlannedStopWindow
            {
                StartMinutes = Math.Clamp(w.StartMinutes, 0, 1439),
                EndMinutes = Math.Clamp(w.EndMinutes, 1, 1440),
                Label = string.IsNullOrWhiteSpace(w.Label) ? null : w.Label.Trim()
            })
            .Where(w => w.EndMinutes > w.StartMinutes)
            .OrderBy(w => w.StartMinutes)
            .ToList();
        Update(settings => settings.OeeManual.PlannedStops = normalized);
    }

    /// <summary>
    /// 자동 비생산 패턴 캐시를 저장한다. OeeController.ComputeAndCacheAutoPatternAsync 전용.
    /// <see cref="Update"/> 로 원자적 저장(설정 페이지 저장과 경합해도 유실 없음).
    /// </summary>
    public void SavePlannedAutoPatternCache(PlannedAutoPatternCache cache)
        => Update(settings => settings.OeeManual.AutoPatternCache = cache);

    /// <summary>
    /// 정지·비생산 판정 배수 저장 (2026-07-13 사용자 설정화 → 2026-09-09 WT 축, doc/27). 호출측(컨트롤러)이 범위·역전
    /// 검증을 마친 값을 받는다(여기서도 클램프 방어). 배수가 바뀌면 학습기(§3.5)의 장시간 정지 문턱도 바뀌므로
    /// AutoPatternCache(24h)를 함께 폐기 — 다음 조회가 새 문턱으로 즉시 재학습한다.
    /// </summary>
    public void SaveStopMultipliers(double idleMult, double nonProdMult, double? faultMtMult = null)
    {
        var idle = Math.Clamp(idleMult, OeeManualSettings.IdleMultMin, OeeManualSettings.IdleMultMax);
        var nonProd = Math.Clamp(nonProdMult, OeeManualSettings.NonProdMultMin, OeeManualSettings.NonProdMultMax);
        var fault = faultMtMult is double fm && double.IsFinite(fm)
            ? Math.Clamp(fm, OeeManualSettings.FaultMultMin, OeeManualSettings.FaultMultMax)
            : (double?)null;
        Update(settings =>
        {
            settings.OeeManual.IdleWtMultiplier = idle;
            settings.OeeManual.NonProdWtMultiplier = nonProd;
            if (fault is double f) settings.OeeManual.FaultMtMultiplier = f;
            settings.OeeManual.AutoPatternCache = null;
        });
    }

    /// <summary>
    /// 모든 관리 설정을 코드 기본값(<see cref="AppSettingsModel"/>)으로 초기화하고 저장한다.
    /// 업그레이드 시 Production.json 을 보존하므로, 구버전에서 넘어온 stale 설정이 문제를 일으킬 때
    /// 사용자가 설정 페이지에서 명시적으로 깨끗한 기본값으로 되돌리는 escape hatch.
    /// CCTV 카메라·이상치 임계·시프트·OEE 신호·DB 경로·로그 수준 등 전부 기본값으로 돌아간다.
    /// (Database/Urls 등 호스트 바인딩 항목은 서비스 재시작 후 적용된다.)
    /// </summary>
    public void ResetToDefaults()
    {
        _logger.LogWarning("모든 설정을 코드 기본값으로 초기화합니다 (사용자 요청).");
        SaveSettings(new AppSettingsModel());
    }

    private static void WriteSections(JsonObject target, AppSettingsModel model)
    {
        target["Database"] = JsonSerializer.SerializeToNode(model.Database, JsonOptions);
        target["FlowCycle"] = JsonSerializer.SerializeToNode(model.FlowCycle, JsonOptions);
        target["DspTables"] = JsonSerializer.SerializeToNode(model.DspTables, JsonOptions);
        target["Hub"] = JsonSerializer.SerializeToNode(model.Hub, JsonOptions);
        target["Logging"] = JsonSerializer.SerializeToNode(model.Logging, JsonOptions);
        target["Ui"] = JsonSerializer.SerializeToNode(model.Ui, JsonOptions);
        target["HistoryView"] = JsonSerializer.SerializeToNode(model.HistoryView, JsonOptions);
        target["Cctv"] = JsonSerializer.SerializeToNode(model.Cctv, JsonOptions);
        target["OeeSignals"] = JsonSerializer.SerializeToNode(model.OeeSignals, JsonOptions);
        target["OeeManual"] = JsonSerializer.SerializeToNode(model.OeeManual, JsonOptions);
        target["Shift"] = JsonSerializer.SerializeToNode(model.Shift, JsonOptions);
        target["CycleExclusion"] = JsonSerializer.SerializeToNode(model.CycleExclusion, JsonOptions);
        target["AbnormalAlarm"] = JsonSerializer.SerializeToNode(model.AbnormalAlarm, JsonOptions);
        target["AutoCalibration"] = JsonSerializer.SerializeToNode(model.AutoCalibration, JsonOptions);
        target["EmailBriefing"] = JsonSerializer.SerializeToNode(model.EmailBriefing, JsonOptions);
        target["ExternalAccess"] = JsonSerializer.SerializeToNode(model.ExternalAccess, JsonOptions);
    }

    public FlowCycleOverride? GetFlowCycleOverride(string flowName)
    {
        if (string.IsNullOrWhiteSpace(flowName))
        {
            return null;
        }

        var settings = LoadSettings();
        return settings.FlowCycle.Overrides
            .FirstOrDefault(item => string.Equals(item.FlowName, flowName, StringComparison.OrdinalIgnoreCase));
    }

    public void SaveFlowCycleOverride(string flowName, string? startCallName, string? endCallName)
        => SaveFlowCycleOverride(flowName, startCallName, endCallName, chatterFilterMs: null, chatterSpecified: false);

    /// <summary>
    /// Head/Tail override 저장 + (선택) flow 별 채터링 필터. <paramref name="chatterSpecified"/>=false 면 기존
    /// <see cref="FlowCycleOverride.ChatterFilterMs"/> 를 보존(구 클라이언트/벌크 편집기 호환), true 면 값으로 교체
    /// (null = 글로벌 상속으로 되돌림, 0 = 이 flow 만 끔). 모든 override 필드(Head/Tail/idealCT/채터)가 비면 항목 제거.
    /// </summary>
    public void SaveFlowCycleOverride(
        string flowName, string? startCallName, string? endCallName, int? chatterFilterMs, bool chatterSpecified)
        => SaveFlowCycleOverride(flowName, startCallName, endCallName, chatterFilterMs, chatterSpecified, lookup: null);

    /// <summary>
    /// <paramref name="lookup"/> 이 있으면 Head/Tail 이름에서 Call GUID 를 함께 기록한다(이중 키 —
    /// <see cref="CallRefReconciler"/>). null 이면 GUID 는 비워 두고 다음 재해석에서 이름으로 채운다.
    /// </summary>
    public void SaveFlowCycleOverride(
        string flowName, string? startCallName, string? endCallName, int? chatterFilterMs, bool chatterSpecified,
        FlowCallLookup? lookup)
    {
        if (string.IsNullOrWhiteSpace(flowName))
        {
            throw new ArgumentException("Flow name is required.", nameof(flowName));
        }

        var settings = LoadSettings();
        var overrides = settings.FlowCycle.Overrides;
        var existing = overrides
            .FirstOrDefault(item => string.Equals(item.FlowName, flowName, StringComparison.OrdinalIgnoreCase));

        var normalizedStart = NormalizeOptional(startCallName);
        var normalizedEnd = NormalizeOptional(endCallName);
        int? normalizedChatter = chatterSpecified
            ? (chatterFilterMs is int c ? Math.Max(0, c) : null)
            : existing?.ChatterFilterMs;

        string? IdOf(string? name) =>
            lookup is not null && lookup.TryGetId(name, out var id) ? CallRefReconciler.IdText(id) : null;
        string? SigOf(string? name) =>
            lookup is not null && lookup.TryGetId(name, out var id) ? lookup.SigOf(id) : null;

        if (existing is null)
        {
            if (!string.IsNullOrWhiteSpace(normalizedStart) || !string.IsNullOrWhiteSpace(normalizedEnd) || normalizedChatter is not null)
            {
                overrides.Add(new FlowCycleOverride
                {
                    FlowName = flowName,
                    StartCallName = normalizedStart,
                    EndCallName = normalizedEnd,
                    StartCallId = IdOf(normalizedStart),
                    EndCallId = IdOf(normalizedEnd),
                    StartCallSig = SigOf(normalizedStart),
                    EndCallSig = SigOf(normalizedEnd),
                    ChatterFilterMs = normalizedChatter,
                });
            }
        }
        else
        {
            existing.StartCallName = normalizedStart;
            existing.EndCallName = normalizedEnd;
            existing.StartCallId = IdOf(normalizedStart);
            existing.EndCallId = IdOf(normalizedEnd);
            existing.StartCallSig = SigOf(normalizedStart);
            existing.EndCallSig = SigOf(normalizedEnd);
            existing.ChatterFilterMs = normalizedChatter;
            // 종전엔 Head/Tail 이 기본값으로 돌아가면 항목을 통째로 지워 IdealCycleTimeMs 까지 유실됐다 —
            // 이제 모든 override 필드가 비었을 때만 제거(SaveFlowIdealCycleTime 과 동일 원칙).
            if (string.IsNullOrWhiteSpace(existing.StartCallName)
                && string.IsNullOrWhiteSpace(existing.EndCallName)
                && existing.IdealCycleTimeMs is null
                && existing.ChatterFilterMs is null)
            {
                overrides.Remove(existing);
            }
        }

        settings.FlowCycle.Overrides = overrides
            .OrderBy(item => item.FlowName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SaveSettings(settings);
    }

    public void ClearFlowCycleOverrides()
    {
        var settings = LoadSettings();
        settings.FlowCycle.Overrides.Clear();
        SaveSettings(settings);
    }

    // ── flow 사이클 분기(BranchSets) ─────────────────────────────────────────

    /// <summary>분기 개수 상한 — OEE 행·precompute 작업량이 분기 수 배수라 폭주 방지용 가드.</summary>
    public const int MaxBranchesPerFlow = 8;

    /// <summary>마지막 재해석 결과(유령 목록 포함) — 화면/API 가 "모델에 없는 call" 안내에 쓴다. null=아직 안 돌았음.</summary>
    public CallRefReconcileReport? LastCallRefReport { get; private set; }

    /// <summary>flow 리네임 승계 기록(최근 50건, 최신순) — 설정 화면 '참조 재해석' 안내용. DB 승계는 DspDatabaseServiceAdapter 가 하고 결과만 남긴다.</summary>
    public IReadOnlyList<FlowRenameRecord> LastFlowRenames { get; private set; } = [];

    public void RecordFlowRenames(IEnumerable<FlowRenameRecord> records)
        => LastFlowRenames = LastFlowRenames.Concat(records).OrderByDescending(r => r.AtUtc).Take(50).ToList();

    /// <summary>
    /// flow 이름 변경 승계(2026-09-11) — flow 이름을 키로 든 설정 전부를 옛 이름에서 새 이름으로: 분기 정의·경계 override·
    /// CT 제외 범위·OEE 신호맵·출력 flow 목록·시프트 대상. 새 이름 항목이 이미 있으면 그쪽을 정본으로 두고 옛 항목은 제거한다
    /// (두 설정을 병합하지 않는다). 반환 = 바뀐 섹션 수, 0 이면 저장하지 않는다.
    /// </summary>
    public int RenameFlowInSettings(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)
            || string.Equals(oldName, newName, StringComparison.Ordinal))
            return 0;

        var settings = LoadSettings();
        var sections = 0;
        bool IsOld(string? s) => string.Equals(s, oldName, StringComparison.OrdinalIgnoreCase);
        bool IsNew(string? s) => string.Equals(s, newName, StringComparison.OrdinalIgnoreCase);

        // 이름 키 리스트 공통 처리 — 새 이름 항목이 있으면 옛 항목 제거, 없으면 이름만 갱신.
        int Carry<T>(List<T> list, Func<T, string?> get, Action<T, string> set)
        {
            if (!list.Any(x => IsOld(get(x)))) return 0;
            if (list.Any(x => IsNew(get(x)))) list.RemoveAll(x => IsOld(get(x)));
            else foreach (var x in list) if (IsOld(get(x))) set(x, newName);
            return 1;
        }

        sections += Carry(settings.FlowCycle.BranchSets, s => s.FlowName, (s, n) => s.FlowName = n);
        sections += Carry(settings.FlowCycle.Overrides, o => o.FlowName, (o, n) => o.FlowName = n);
        sections += Carry(settings.CycleExclusion.Ranges, r => r.FlowName, (r, n) => r.FlowName = n);
        sections += Carry(settings.OeeSignals.Flows, m => m.FlowName, (m, n) => m.FlowName = n);

        if (settings.OeeManual.OutputFlows.Any(IsOld))
        {
            settings.OeeManual.OutputFlows = settings.OeeManual.OutputFlows
                .Select(f => IsOld(f) ? newName : f)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            sections++;
        }
        if (IsOld(settings.Shift.TargetFlow))
        {
            settings.Shift.TargetFlow = newName;
            sections++;
        }

        if (sections > 0)
        {
            SaveSettings(settings);
            _logger.LogInformation("[FlowRename] 설정 승계 '{Old}' -> '{New}': {N}개 섹션", oldName, newName, sections);
        }
        return sections;
    }

    /// <summary>
    /// AASX (재)로드 직후: 분기 정의·flow 경계 override 의 Call 참조(GUID+이름)를 현재 모델에 맞춘다.
    /// GUID 로 찾은 call 의 이름이 바뀌었으면 스냅샷을 새 이름으로 갱신(리네임 추종), GUID 가 없던 구 데이터는
    /// 이름으로 GUID 를 채운다. 유령(둘 다 없음)은 지우지 않고 보고만 한다 — 화면이 사용자에게 제거/재지정을 맡긴다.
    /// 모델에 없는 flow 의 정의는 건드리지 않는다(유령 flow 정리는 PruneFlowCycleOverrides 의 몫).
    /// 변경이 있을 때만 1회 저장. 어떤 예외도 로드 경로를 막지 않는다.
    /// </summary>
    public CallRefReconcileReport ReconcileCallReferences(DsProjectService project)
    {
        var report = new CallRefReconcileReport();
        try
        {
            if (!project.IsLoaded) return report;
            var settings = LoadSettings();
            var lookups = new Dictionary<string, FlowCallLookup?>(StringComparer.OrdinalIgnoreCase);
            FlowCallLookup? LookupFor(string flowName)
            {
                if (lookups.TryGetValue(flowName, out var hit)) return hit;
                var flow = project.GetAllFlowsIncludingDisabled()
                    .FirstOrDefault(f => string.Equals(f.Name, flowName, StringComparison.OrdinalIgnoreCase));
                var built = flow is null ? null : project.GetFlowCallLookup(flow.Id);
                lookups[flowName] = built;
                return built;
            }

            foreach (var set in settings.FlowCycle.BranchSets)
            {
                var lookup = LookupFor(set.FlowName);
                if (lookup is null || lookup.Count == 0) continue;   // 모델에 없는 flow / 빈 flow — 판단 근거 없음
                CallRefReconciler.ReconcileBranchSet(set, lookup, report);
            }
            foreach (var ov in settings.FlowCycle.Overrides)
            {
                var lookup = LookupFor(ov.FlowName);
                if (lookup is null || lookup.Count == 0) continue;
                CallRefReconciler.ReconcileOverride(ov, lookup, report);
            }

            if (report.Changed)
            {
                SaveSettings(settings);
                foreach (var r in report.Renamed)
                    _logger.LogInformation("[CallRef] '{Flow}'{Branch} {Role}: '{Old}' -> '{New}' (GUID 동일 - 이름 추종)",
                        r.FlowName, r.BranchName is null ? "" : $" 분기 '{r.BranchName}'", r.Role, r.OldName, r.NewName);
                foreach (var r in report.Rematched)
                    _logger.LogInformation("[CallRef] '{Flow}'{Branch} {Role}: '{Old}' -> '{New}' (배선 지문 일치 - 이름·GUID 재매칭)",
                        r.FlowName, r.BranchName is null ? "" : $" 분기 '{r.BranchName}'", r.Role, r.OldName, r.NewName);
                if (report.FilledIds > 0)
                    _logger.LogInformation("[CallRef] Call GUID {N}건 채움(구 데이터/재생성 - 이름으로 해석)", report.FilledIds);
                if (report.FilledSigs > 0 || report.Rewired > 0)
                    _logger.LogInformation("[CallRef] 배선 지문 채움 {Filled}건 · 배선 변경 추종 {Rewired}건", report.FilledSigs, report.Rewired);
            }
            foreach (var g in report.Ghosts)
                _logger.LogWarning("[CallRef] '{Flow}'{Branch} {Role}: '{Name}' 이 현재 모델에 없음 - 화면에서 제거/재지정 필요",
                    g.FlowName, g.BranchName is null ? "" : $" 분기 '{g.BranchName}'", g.Role, g.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CallRef] 분기/경계 참조 재해석 실패(로드는 계속)");
        }
        LastCallRefReport = report;
        return report;
    }

    /// <summary>OEE 노출용 가상 flow 이름 규약 — "부모_분기". 파싱은 금지(이름에 '_' 가능), 역해석은 맵으로.</summary>
    public static string ComposeBranchFlowName(string parentFlow, string branchName) => parentFlow + "_" + branchName;

    /// <summary>
    /// 가상 flow 이름("부모_분기") → (부모 flow, 분기 이름) 역해석 맵. OEE 열거/집계가 분기 스코프 SQL
    /// (flowName=부모 AND branchName=분기)로 바꿔 태우는 단일 소스. 분기 미사용이면 빈 맵.
    /// </summary>
    public Dictionary<string, (string Parent, string Branch)> GetBranchVirtualMap()
    {
        var map = new Dictionary<string, (string Parent, string Branch)>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in GetAllFlowBranchSets())
            foreach (var b in set.Branches)
                if (!string.IsNullOrWhiteSpace(b.Name))
                    map[ComposeBranchFlowName(set.FlowName, b.Name)] = (set.FlowName, b.Name);
        return map;
    }

    /// <summary>분기가 활성인 부모 flow 이름 집합 — OEE 열거에서 부모를 빼고 가상 이름으로 치환할 때 사용.</summary>
    public HashSet<string> GetBranchedParentFlows()
        => new(GetAllFlowBranchSets().Select(s => s.FlowName), StringComparer.OrdinalIgnoreCase);

    public FlowBranchSet? GetFlowBranchSet(string flowName)
    {
        if (string.IsNullOrWhiteSpace(flowName)) return null;
        return LoadSettings().FlowCycle.BranchSets
            .FirstOrDefault(s => string.Equals(s.FlowName, flowName, StringComparison.OrdinalIgnoreCase));
    }

    public List<FlowBranchSet> GetAllFlowBranchSets()
        => LoadSettings().FlowCycle.BranchSets
            .Where(s => !string.IsNullOrWhiteSpace(s.FlowName) && s.Branches.Count > 0)
            .ToList();

    /// <summary>
    /// 분기 정의 저장. <paramref name="branches"/> 가 null/빈 목록이면 해제(항목 제거) — 해제 후에는
    /// 호출측이 전체 이력 재도출을 돌려 branchName 라벨을 걷어내야 완전 복귀된다.
    /// 검증 실패는 ArgumentException(사용자에게 그대로 보여줄 한글 메시지).
    /// </summary>
    public void SaveFlowBranchSet(string flowName, IReadOnlyList<FlowBranchDef>? branches)
    {
        if (string.IsNullOrWhiteSpace(flowName))
            throw new ArgumentException("Flow name is required.", nameof(flowName));

        var settings = LoadSettings();
        var sets = settings.FlowCycle.BranchSets;
        var existing = sets.FirstOrDefault(s => string.Equals(s.FlowName, flowName, StringComparison.OrdinalIgnoreCase));

        if (branches is null || branches.Count == 0)
        {
            if (existing is not null) { sets.Remove(existing); SaveSettings(settings); }
            return;
        }

        if (branches.Count > MaxBranchesPerFlow)
            throw new ArgumentException($"분기는 flow 당 최대 {MaxBranchesPerFlow}개까지 지정할 수 있습니다.");

        var cleaned = new List<FlowBranchDef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in branches)
        {
            var name = (b.Name ?? "").Trim();
            if (name.Length == 0)
                throw new ArgumentException("이름이 비어 있는 분기가 있습니다.");
            if (!seen.Add(name))
                throw new ArgumentException($"분기 이름이 중복됩니다: {name}");
            if (string.IsNullOrWhiteSpace(b.StartCallName) || string.IsNullOrWhiteSpace(b.EndCallName))
                throw new ArgumentException($"분기 '{name}' 의 시작/끝(Head/Tail) call 이 지정되지 않았습니다.");
            var excludedNames = (b.ExcludedCallNames ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            cleaned.Add(new FlowBranchDef
            {
                Name = name,
                StartCallName = b.StartCallName!.Trim(),
                EndCallName = b.EndCallName!.Trim(),
                ExcludedCallNames = excludedNames,
                // GUID(이중 키) — 호출측(FlowController)이 CallRefReconciler.StampIds 로 채운 값. 정리로 제외 목록
                // 개수가 달라졌으면 병렬 리스트가 어긋나므로 버린다(다음 재해석이 이름으로 다시 채움).
                StartCallId = b.StartCallId,
                EndCallId = b.EndCallId,
                ExcludedCallIds = b.ExcludedCallIds is { } ids
                    && ids.Count == (b.ExcludedCallNames?.Count ?? 0)
                    && excludedNames.Count == ids.Count ? ids : null,
                StartCallSig = b.StartCallSig,
                EndCallSig = b.EndCallSig,
                ExcludedCallSigs = b.ExcludedCallSigs is { } sigs
                    && sigs.Count == (b.ExcludedCallNames?.Count ?? 0)
                    && excludedNames.Count == sigs.Count ? sigs : null,
                // 미지의 확장 필드 보존(2026-09-11) — 구버전/신버전 혼재 시 다른 쪽이 기록한 필드가 이 저장으로 사라지지 않게.
                // 다른 설정 모델은 [JsonExtensionData] 로 자동 보존되지만 이 경로만 새 객체를 만들어 예외였다.
                ExtensionData = b.ExtensionData,
            });
        }

        if (existing is null)
            sets.Add(new FlowBranchSet { FlowName = flowName, Branches = cleaned });
        else
            existing.Branches = cleaned;

        settings.FlowCycle.BranchSets = sets
            .OrderBy(s => s.FlowName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SaveSettings(settings);
    }

    /// <summary>
    /// <paramref name="retainFlowNames"/> 에 없는 flow 의 FlowCycle override 를 제거한다(= 현재 AASX 에
    /// 없는 유령 설비 정리). override 는 설정 파일(appsettings.Production.json)에 있어 DB 초기화로도
    /// 안 지워지고 이름 기준 정리 경로가 없었다 — 모델을 여러 번 교체하면 무한 누적된다.
    /// <para>수동 지정 IdealCycleTimeMs(source ≠ auto*) 는 DB 없이도 CT 임계 맵에 강제 주입되므로,
    /// 남아 있으면 유령 설비가 OEE 설비축에서 영구히 사라지지 않는다.</para>
    /// <paramref name="countOnly"/>=true 면 저장하지 않고 대상 개수만 센다(정리 미리보기).
    /// retain 이 비면 no-op(0) — 전량 삭제는 <see cref="ClearFlowCycleOverrides"/> 의 영역.
    /// </summary>
    public int PruneFlowCycleOverrides(IEnumerable<string> retainFlowNames, bool countOnly = false)
    {
        var settings = LoadSettings();
        var stale = SelectStaleFlowCycleOverrides(settings.FlowCycle.Overrides, retainFlowNames);
        if (countOnly || stale.Count == 0) return stale.Count;

        foreach (var o in stale) settings.FlowCycle.Overrides.Remove(o);
        SaveSettings(settings);
        return stale.Count;
    }

    /// <summary>
    /// <see cref="PruneFlowCycleOverrides"/> 의 판정부(순수 함수) — 삭제 대상 override 목록.
    /// 파일 I/O 와 분리해 테스트로 고정한다(대소문자 무시 · 빈 retain 가드).
    /// <para>retain 이 비면 빈 목록 — retain 이 비는 것은 "모델에 설비가 없다"가 아니라 "판정 근거가
    /// 없다"는 뜻이므로, 여기서 막지 않으면 사용자 CT 설정이 전량 삭제된다.</para>
    /// </summary>
    public static List<FlowCycleOverride> SelectStaleFlowCycleOverrides(
        IEnumerable<FlowCycleOverride> overrides, IEnumerable<string>? retainFlowNames)
    {
        var retain = new HashSet<string>(
            retainFlowNames?.Where(n => !string.IsNullOrWhiteSpace(n)) ?? [],
            StringComparer.OrdinalIgnoreCase);
        if (retain.Count == 0) return [];

        return overrides
            .Where(o => !string.IsNullOrWhiteSpace(o.FlowName) && !retain.Contains(o.FlowName))
            .ToList();
    }

    /// <summary>
    /// Flow 의 표준(ideal) 사이클 시간(ms)만 갱신. P5 OEE Performance 의 단일 소스 (doc/21 §2.4).
    /// 기존 StartCallName/EndCallName override 는 보존 — backward compatible.
    /// idealCycleTimeMs == null 이면 IdealCycleTimeMs 만 비우고, 다른 override 도 모두 비어 있으면 항목 제거.
    /// </summary>
    public void SaveFlowIdealCycleTime(string flowName, int? idealCycleTimeMs)
    {
        if (string.IsNullOrWhiteSpace(flowName))
        {
            throw new ArgumentException("Flow name is required.", nameof(flowName));
        }

        var normalized = idealCycleTimeMs is > 0 ? idealCycleTimeMs : null;

        var settings = LoadSettings();
        var overrides = settings.FlowCycle.Overrides;
        var existing = overrides
            .FirstOrDefault(item => string.Equals(item.FlowName, flowName, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            if (normalized is null)
            {
                return; // 설정할 것도, 제거할 것도 없음
            }
            overrides.Add(new FlowCycleOverride
            {
                FlowName = flowName,
                IdealCycleTimeMs = normalized,
            });
        }
        else
        {
            existing.IdealCycleTimeMs = normalized;
            existing.IdealCycleTimeSource = null; // 사람이 직접 저장/해제 → 수동 출처로 환원

            // 모든 override 필드가 비면 항목 자체 제거 (SaveFlowCycleOverride 의 빈 항목 정리와 동일 원칙)
            if (normalized is null
                && string.IsNullOrWhiteSpace(existing.StartCallName)
                && string.IsNullOrWhiteSpace(existing.EndCallName)
                && existing.ChatterFilterMs is null)
            {
                overrides.Remove(existing);
            }
        }

        settings.FlowCycle.Overrides = overrides
            .OrderBy(item => item.FlowName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SaveSettings(settings);
    }

    /// <summary>
    /// idealCT(표준 사이클타임)가 설정(&gt;0)된 Flow 목록. 라인 전체 OEE 성능을 per-flow idealCT 로 집계할 때 사용.
    /// </summary>
    public IReadOnlyList<(string FlowName, int IdealCycleTimeMs)> GetFlowsWithIdealCycleTime()
    {
        return LoadSettings().FlowCycle.Overrides
            .Where(o => !string.IsNullOrWhiteSpace(o.FlowName) && o.IdealCycleTimeMs is > 0)
            .Select(o => (o.FlowName, o.IdealCycleTimeMs!.Value))
            .ToList();
    }

    /// <summary>
    /// 실측 자동기입 전용(<see cref="OeeIdealCycleAutoFillService"/>): idealCT 가 **비어 있는** Flow 에 값을 채우고
    /// 출처(<paramref name="items"/> 의 source: "auto"=확정 p10 / "auto-median"=임시 중앙값)를 스탬프한다.
    /// 수동 입력값(source=null)·확정 자동값("auto")은 절대 덮지 않는다(수동/확정 우선). 단 <b>임시 중앙값
    /// ("auto-median")은 확정 p10("auto")으로 승급(덮어쓰기) 허용</b> — 샘플이 30 도달 시 1회성 보정(doc/21 §12 D).
    /// 후보 선정~저장 사이 레이스도 <see cref="Update"/>(load-modify-save 원자화) 안의 재검사로 안전.
    /// 반환 = 실제 기입/승급된 Flow 수(0 이면 파일 쓰기 없음 — 람다 안에서 변경 없으면 저장 생략 위해 호출측이 후보를 선별).
    /// </summary>
    public int FillIdealCycleTimesAuto(IReadOnlyCollection<(string FlowName, int IdealCycleTimeMs, string Source)> items)
    {
        if (items is null || items.Count == 0) return 0;

        var applied = 0;
        Update(settings =>
        {
            applied = 0; // Update 재시도 대비 — 람다 안에서 셈
            var overrides = settings.FlowCycle.Overrides;
            foreach (var (flowName, idealCycleTimeMs, source) in items)
            {
                if (string.IsNullOrWhiteSpace(flowName) || idealCycleTimeMs <= 0) continue;
                var src = string.IsNullOrWhiteSpace(source) ? "auto" : source;

                var existing = overrides
                    .FirstOrDefault(item => string.Equals(item.FlowName, flowName, StringComparison.OrdinalIgnoreCase));

                if (existing is null)
                {
                    overrides.Add(new FlowCycleOverride
                    {
                        FlowName = flowName.Trim(),
                        IdealCycleTimeMs = idealCycleTimeMs,
                        IdealCycleTimeSource = src,
                    });
                    applied++;
                    continue;
                }

                if (existing.IdealCycleTimeMs is > 0)
                {
                    // 기존값 보존이 기본. 단 임시(auto-median)를 확정(auto)으로 승급하는 경우만 덮는다.
                    var canUpgrade = src == "auto"
                        && string.Equals(existing.IdealCycleTimeSource, "auto-median", StringComparison.OrdinalIgnoreCase)
                        && existing.IdealCycleTimeMs != idealCycleTimeMs;
                    if (!canUpgrade) continue; // 수동/확정 자동 보존
                    existing.IdealCycleTimeMs = idealCycleTimeMs;
                    existing.IdealCycleTimeSource = "auto";
                    applied++;
                }
                else
                {
                    existing.IdealCycleTimeMs = idealCycleTimeMs;
                    existing.IdealCycleTimeSource = src;
                    applied++;
                }
            }

            if (applied > 0)
            {
                settings.FlowCycle.Overrides = overrides
                    .OrderBy(item => item.FlowName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        });
        return applied;
    }

    /// <summary>
    /// 여러 Flow 의 표준(ideal) 사이클 시간을 한 번의 파일 쓰기로 일괄 갱신 (uptime 표준CT 일괄 적용용).
    /// per-flow <paramref name="items"/> 의 mode 로 자동/수동을 명시 선택한다(doc/21 §12.4):
    ///   • mode="manual" : 값을 수동으로 고정(IdealCycleTimeSource=null) — <b>값이 같아도</b> 출처를 수동으로 잠근다
    ///     (자동기입/승급이 더는 못 덮게). 값 null/0 이면 해제(다른 필드 없으면 항목 제거).
    ///   • mode="auto"   : 자동 관리로 해제 — 기존 <b>수동값만</b> 비워 자동기입 서비스가 다시 채우게 한다
    ///     (이미 자동/미설정이면 no-op — 자동값을 비웠다 다시 채우는 churn 방지).
    ///   • mode=null(레거시): 값 변경 시에만 수동 저장(no-op 행은 자동 출처 유지).
    /// 호출당 settings 파일을 한 번만 저장한다.
    /// </summary>
    public void SaveFlowIdealCycleTimesBatch(IEnumerable<(string FlowName, int? IdealCycleTimeMs, string? Mode)> items)
    {
        if (items is null) return;

        var settings = LoadSettings();
        var overrides = settings.FlowCycle.Overrides;
        var changed = false;

        bool RemoveIfEmpty(FlowCycleOverride ov)
        {
            if (ov.IdealCycleTimeMs is null or <= 0
                && string.IsNullOrWhiteSpace(ov.StartCallName)
                && string.IsNullOrWhiteSpace(ov.EndCallName))
            {
                overrides.Remove(ov);
                return true;
            }
            return false;
        }

        foreach (var (flowName, idealCycleTimeMs, mode) in items)
        {
            if (string.IsNullOrWhiteSpace(flowName)) continue;
            var normalized = idealCycleTimeMs is > 0 ? idealCycleTimeMs : null;
            var existing = overrides
                .FirstOrDefault(item => string.Equals(item.FlowName, flowName, StringComparison.OrdinalIgnoreCase));

            // ── mode="auto": 자동 관리로 해제. 수동값(source=null & 값 있음)만 비운다. ──
            if (string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase))
            {
                if (existing?.IdealCycleTimeMs is > 0 && existing.IdealCycleTimeSource is null)
                {
                    existing.IdealCycleTimeMs = null;
                    existing.IdealCycleTimeSource = null;
                    RemoveIfEmpty(existing);
                    changed = true;
                }
                continue; // 이미 자동/미설정이면 변경 없음
            }

            // ── mode="manual"(또는 레거시): 값을 수동으로 저장/고정. ──
            var isManualMode = string.Equals(mode, "manual", StringComparison.OrdinalIgnoreCase);

            if (existing is null)
            {
                if (normalized is null) continue;
                overrides.Add(new FlowCycleOverride { FlowName = flowName.Trim(), IdealCycleTimeMs = normalized }); // source=null=수동
                changed = true;
                continue;
            }

            // 레거시(mode=null)는 값이 같으면 no-op(자동 출처 유지). manual 은 값이 같아도 수동으로 잠근다.
            if (!isManualMode && existing.IdealCycleTimeMs == normalized) continue;

            existing.IdealCycleTimeMs = normalized;
            existing.IdealCycleTimeSource = null; // 수동 출처로 환원(고정)
            if (normalized is null) RemoveIfEmpty(existing);
            changed = true;
        }

        if (!changed) return;

        settings.FlowCycle.Overrides = overrides
            .OrderBy(item => item.FlowName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SaveSettings(settings);
    }

    /// <summary>
    /// 대시보드 히스토리 이상치 제외 범위를 Flow별로 upsert/삭제 (단위 = 초). minSec/maxSec 둘 다 null 이면
    /// 해당 Flow 의 제외 규칙을 제거(전체 복원). 음수는 무시(null), min &gt; max 로 뒤집힌 입력은 자동 교정한다.
    /// (SaveFlowCycleOverride 와 같은 per-flow 리스트 정리 원칙 — 빈 항목은 남기지 않음.)
    /// </summary>
    public void SaveCycleExclusion(string flowName, double? minSec, double? maxSec)
    {
        if (string.IsNullOrWhiteSpace(flowName))
        {
            throw new ArgumentException("Flow name is required.", nameof(flowName));
        }

        var min = minSec is >= 0 ? minSec : null;
        var max = maxSec is >= 0 ? maxSec : null;
        if (min is not null && max is not null && min > max)
        {
            (min, max) = (max, min);   // 뒤집힌 입력 자동 교정
        }

        var settings = LoadSettings();
        var ranges = settings.CycleExclusion.Ranges;
        var existing = ranges
            .FirstOrDefault(item => string.Equals(item.FlowName, flowName, StringComparison.OrdinalIgnoreCase));

        if (min is null && max is null)
        {
            if (existing is not null) ranges.Remove(existing);   // 미설정 = 제외 해제
        }
        else if (existing is null)
        {
            ranges.Add(new FlowCycleExclusion { FlowName = flowName, MinSec = min, MaxSec = max });
        }
        else
        {
            existing.MinSec = min;
            existing.MaxSec = max;
        }

        settings.CycleExclusion.Ranges = ranges
            .OrderBy(item => item.FlowName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SaveSettings(settings);
    }

    /// <summary>
    /// Flow 의 "유효" 비가동 CT 범위(ms). per-flow 이상치 제외(CycleExclusion, 초)가 있으면 그것을, 없으면
    /// 글로벌 비가동 임계(HistoryView, ms)를 쓴다 — 방향별 독립(예: max 만 per-flow 지정 시 min 은 글로벌 유지).
    /// 0 = 해당 방향 제한 없음(기존 컨벤션 유지). 대시보드 평균/시프트/OEE/CallTest/사이클분석이 동일 기준을
    /// 보도록 "글로벌=기본, per-flow=override" 단일 유효범위로 합치는 단일 소스.
    /// </summary>
    public (int MaxMs, int MinMs) GetEffectiveCycleRangeMs(string flowName)
        => ResolveEffectiveCycleRangeMs(LoadSettings(), flowName);

    /// <summary>
    /// Flow 의 "유효" 신호 채터링 필터(ms) — flow override(<see cref="FlowCycleOverride.ChatterFilterMs"/>, 0 포함) 가
    /// 있으면 그 값, 없으면 글로벌 <see cref="FlowCycleSettings.ChatterFilterMs"/>. 0 = 필터 없음.
    /// 사이클 경계 재도출·간트 신호 구간·라이브 head 재시작 억제가 같은 값을 보도록 하는 단일 소스.
    /// </summary>
    public int GetEffectiveChatterFilterMs(string flowName)
        => ResolveEffectiveChatterFilterMs(LoadSettings(), flowName);

    /// <summary>이미 로드한 모델로 유효 채터링 필터 계산(반복 호출 시 디스크 재로드 방지).</summary>
    public static int ResolveEffectiveChatterFilterMs(AppSettingsModel settings, string flowName)
    {
        int global = Math.Max(0, settings.FlowCycle.ChatterFilterMs);
        if (string.IsNullOrWhiteSpace(flowName)) return global;
        var ov = settings.FlowCycle.Overrides
            .FirstOrDefault(o => string.Equals(o.FlowName, flowName, StringComparison.OrdinalIgnoreCase));
        return ov?.ChatterFilterMs is int c ? Math.Max(0, c) : global;
    }

    /// <summary>
    /// Flow 평균(AvgMT/WT/CT) 롤링 윈도우 크기 = 최근 비가동-제외 사이클 수. 0/음수 = 전체 이력(윈도우 비활성).
    /// 라이브 누산기·SQL 재집계가 동일 N 을 쓰도록 하는 단일 소스.
    /// </summary>
    public int GetCycleAverageWindow() => LoadSettings().HistoryView.CycleAverageWindow;

    /// <summary>
    /// 자동 보정 1회성 완료 플래그(<see cref="AutoCalibrationSettings.CompletedAt"/>)를 현재 UTC 시각으로 박제한다.
    /// 이미 채워져 있으면 덮어쓰지 않는다(멱등) — 트리거 결정~저장 사이 재시작에도 안전. 다른 설정은 보존(load-modify-save).
    /// 설정 파일의 단일 writer 경로(<see cref="SaveSettings"/>)를 통하므로 백그라운드/수동 호출과 무관히 일관 저장된다.
    /// </summary>
    /// <summary>
    /// 실측 보정이 project.aasx 에 실제 기록된 직후 호출. <see cref="AutoCalibrationSettings.LastAppliedAt"/>/
    /// <see cref="AutoCalibrationSettings.LastAppliedSummary"/> 를 항상 최신화(자동/수동 무관 — AASX 수정 시각)하고,
    /// 1회성 트리거 플래그 <see cref="AutoCalibrationSettings.CompletedAt"/> 는 아직 비어 있을 때만 박제한다(멱등).
    /// load-modify-save 전체를 <see cref="_writeLock"/> 으로 원자화 — 설정 페이지 저장과 경합해도 유실 없음.
    /// </summary>
    public void RecordAutoCalibrationApplied(string summary)
    {
        lock (_writeLock)
        {
            var settings = LoadSettings();
            var now = DateTime.UtcNow;
            settings.AutoCalibration.LastAppliedAt = now;
            settings.AutoCalibration.LastAppliedSummary = summary;
            settings.AutoCalibration.CompletedAt ??= now; // 최초 1회만 — 재시작 자동 재실행 차단.
            SaveSettings(settings);
        }
    }

    /// <summary>이미 로드한 모델로 유효범위 계산(반복 호출 시 디스크 재로드 방지).</summary>
    public static (int MaxMs, int MinMs) ResolveEffectiveCycleRangeMs(AppSettingsModel settings, string flowName)
    {
        int globalMax = settings.HistoryView.MaxCycleTimeMs;
        int globalMin = settings.HistoryView.MinCycleTimeMs;

        var ov = string.IsNullOrWhiteSpace(flowName)
            ? null
            : settings.CycleExclusion.Ranges
                .FirstOrDefault(r => string.Equals(r.FlowName, flowName, StringComparison.OrdinalIgnoreCase));

        int maxMs = ov?.MaxSec is double mx && mx > 0 ? (int)Math.Round(mx * 1000) : globalMax;
        int minMs = ov?.MinSec is double mn && mn > 0 ? (int)Math.Round(mn * 1000) : globalMin;
        return (maxMs, minMs);
    }

    /// <summary>
    /// per-flow 이상치 제외가 명시된 Flow 들의 유효범위(ms) 맵. 소급 재스탬프(ReapplyIdleThresholds)가
    /// 글로벌 기본 위에 flow 별 override 만 덮어쓰도록 전달한다(명시 안 된 Flow 는 글로벌 그대로).
    /// </summary>
    public Dictionary<string, (int MaxMs, int MinMs)> GetPerFlowEffectiveRangesMs()
    {
        var settings = LoadSettings();
        var map = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (var r in settings.CycleExclusion.Ranges)
        {
            if (string.IsNullOrWhiteSpace(r.FlowName)) continue;
            map[r.FlowName] = ResolveEffectiveCycleRangeMs(settings, r.FlowName);
        }
        return map;
    }

    /// <summary>
    /// DSP 데이터베이스 삭제 (plc.db 및 관련 파일)
    /// </summary>
    public void DeleteDatabase(string dbPath)
    {
        _logger.LogInformation("데이터베이스 삭제 시작: {DbPath}", dbPath);

        try
        {
            // 모든 SQLite 연결 풀 해제 (파일 잠금 방지)
            SqliteConnection.ClearAllPools();
            _logger.LogInformation("SQLite 연결 풀 해제 완료");

            // plc.db 삭제
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
                _logger.LogInformation("데이터베이스 파일 삭제: {DbPath}", dbPath);
            }

            // WAL 파일 삭제
            var walPath = dbPath + "-wal";
            if (File.Exists(walPath))
            {
                File.Delete(walPath);
                _logger.LogInformation("WAL 파일 삭제: {WalPath}", walPath);
            }

            // SHM 파일 삭제
            var shmPath = dbPath + "-shm";
            if (File.Exists(shmPath))
            {
                File.Delete(shmPath);
                _logger.LogInformation("SHM 파일 삭제: {ShmPath}", shmPath);
            }

            _logger.LogInformation("데이터베이스 삭제 완료");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "데이터베이스 삭제 실패: {DbPath}", dbPath);
            throw;
        }
    }

    private JsonObject LoadRaw(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _logger.LogWarning("{File} 파일 없음, 기본값으로 생성", Path.GetFileName(path));
                var defaultJson = JsonSerializer.Serialize(new AppSettingsModel(), JsonOptions);
                File.WriteAllText(path, defaultJson);
            }

            var json = File.ReadAllText(path);
            return JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "{File} JSON 파싱 실패, 백업 후 기본값으로 복구", Path.GetFileName(path));
            var backupPath = path + $".bak.{DateTime.Now:yyyyMMdd_HHmmss}";
            try { File.Copy(path, backupPath, overwrite: true); } catch { /* best effort */ }

            var defaultJson = JsonSerializer.Serialize(new AppSettingsModel(), JsonOptions);
            File.WriteAllText(path, defaultJson);
            return JsonNode.Parse(defaultJson)?.AsObject() ?? new JsonObject();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{File} 읽기 실패", Path.GetFileName(path));
            return new JsonObject();
        }
    }

    private void SaveRaw(string path, JsonObject root)
    {
        try
        {
            File.WriteAllText(path, root.ToJsonString(JsonOptions));
            _logger.LogInformation("{File} 저장 완료", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{File} 저장 실패", Path.GetFileName(path));
        }
    }

    private static T Deserialize<T>(JsonNode? node) where T : new()
    {
        if (node is null) return new T();
        return node.Deserialize<T>(JsonOptions) ?? new T();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
