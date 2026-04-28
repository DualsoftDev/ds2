using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Ds2.Core;
using Ds2.Core.Store;
using Microsoft.FSharp.Core;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class TagWizardDialog
{
    /// <summary>
    /// 각 그리드/리스트 초기 로드.
    /// Step 1: 시스템/Flow 선두 주소 / Step 2: SystemType 템플릿 목록
    /// 외부 txt 파일 동기화는 수행하지 않음 (AASX 내 FBTagMapPresets 가 단일 진실원).
    /// </summary>
    private void LoadTemplateFileList()
    {
        LoadSystemBase();
        LoadFlowBase();
        LoadDeviceTemplateList();
    }

    #region Tab 1: 시스템 주소

    /// <summary>
    /// 시스템 주소 로드
    /// </summary>
    private void LoadSystemBase()
    {
        try
        {
            _systemBaseRows.Clear();

            // 템플릿 파일에서 사용 가능한 SystemType 목록 (콤보 데이터소스)
            // SystemType 목록 — 프로젝트 속성의 프리셋이 단일 진실원, AASX Preset 키는 보완.
            var availableSet = new System.Collections.Generic.SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in Promaker.Services.SystemTypePresetProvider.GetSystemTypes())
                availableSet.Add(t);
            foreach (var kv in Promaker.Services.FBTagMapStore.LoadAll(_store))
                availableSet.Add(kv.Key);
            var systemTypes = availableSet.ToList();

            // 각 Preset 의 BaseAddresses 를 그리드에 표시 (추가/삭제 방식)
            var existingConfig = ParseSystemBaseFile();
            foreach (var kv in existingConfig)
            {
                _systemBaseRows.Add(new SystemBaseRow
                {
                    SystemType = kv.Key,
                    IsEnabled  = true,
                    IW_Base    = kv.Value.IW_Base?.ToString() ?? "",
                    QW_Base    = kv.Value.QW_Base?.ToString() ?? "",
                    MW_Base    = kv.Value.MW_Base?.ToString() ?? "",
                });
            }

            RefreshAvailableSystemTypes(systemTypes);
            SystemBaseStatusText.Text = $"{_systemBaseRows.Count}개 추가됨 (가용 타입 {systemTypes.Count}개)";
        }
        catch (Exception ex)
        {
            SystemBaseStatusText.Text = $"로드 실패: {ex.Message}";
        }
    }

    /// <summary>
    /// AvailableSystemTypes 콤보 데이터 갱신 — 소스는 AASX 내 FBTagMapPresets 키 ∪ 임베디드 기본 템플릿 이름.
    /// 이미 추가된 타입은 제외.
    /// </summary>
    private void RefreshAvailableSystemTypes(System.Collections.Generic.List<string>? allTypes = null)
    {
        if (allTypes == null)
        {
            var set = new System.Collections.Generic.SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in Promaker.Services.SystemTypePresetProvider.GetSystemTypes())
                set.Add(t);
            foreach (var kv in Promaker.Services.FBTagMapStore.LoadAll(_store))
                set.Add(kv.Key);
            allTypes = set.ToList();
        }

        var usedTypes = _systemBaseRows.Select(r => r.SystemType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AvailableSystemTypes.Clear();
        foreach (var t in allTypes)
            if (!usedTypes.Contains(t))
                AvailableSystemTypes.Add(t);
    }

    private void AddSystemBaseRow_Click(object sender, RoutedEventArgs e)
    {
        var sysType = NewSystemTypeCombo.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(sysType))
        {
            SystemBaseStatusText.Text = "⚠ 시스템 타입을 선택하거나 입력하세요.";
            return;
        }
        if (_systemBaseRows.Any(r => r.SystemType.Equals(sysType, StringComparison.OrdinalIgnoreCase)))
        {
            SystemBaseStatusText.Text = $"⚠ '{sysType}' 은(는) 이미 추가되어 있습니다.";
            return;
        }

        _systemBaseRows.Add(new SystemBaseRow
        {
            SystemType = sysType,
            IsEnabled  = true,
            IW_Base    = NewSystemIwBox.Text?.Trim() ?? "",
            QW_Base    = NewSystemQwBox.Text?.Trim() ?? "",
            MW_Base    = NewSystemMwBox.Text?.Trim() ?? "",
        });

        // 입력 필드 리셋
        NewSystemTypeCombo.Text = "";
        NewSystemIwBox.Text = "";
        NewSystemQwBox.Text = "";
        NewSystemMwBox.Text = "";

        RefreshAvailableSystemTypes();
        SystemBaseStatusText.Text = $"✓ '{sysType}' 추가됨 ({_systemBaseRows.Count}개)";
    }

    private void RemoveSystemBaseRow_Click(object sender, RoutedEventArgs e)
    {
        if (SystemBaseGrid.SelectedItem is SystemBaseRow row)
        {
            var name = row.SystemType;
            _systemBaseRows.Remove(row);
            RefreshAvailableSystemTypes();
            SystemBaseStatusText.Text = $"✓ '{name}' 삭제됨 ({_systemBaseRows.Count}개)";
        }
        else
        {
            SystemBaseStatusText.Text = "⚠ 삭제할 행을 먼저 선택하세요.";
        }
    }

    /// <summary>
    /// SystemType 별 BaseAddress 를 FBTagMapPresets 에서 직접 읽어온다 (외부 파일 불필요).
    /// Preset 이 없는 SystemType 은 결과에 포함되지 않는다.
    /// </summary>
    private Dictionary<string, (int? IW_Base, int? QW_Base, int? MW_Base)> ParseSystemBaseFile()
    {
        var result = new Dictionary<string, (int? IW_Base, int? QW_Base, int? MW_Base)>(StringComparer.OrdinalIgnoreCase);
        var presets = Promaker.Services.FBTagMapStore.LoadAll(_store);
        foreach (var kv in presets)
        {
            var ba = kv.Value.BaseAddresses;
            if (ba == null) continue;
            result[kv.Key] = (TryParseNum(ba.InputBase), TryParseNum(ba.OutputBase), TryParseNum(ba.MemoryBase));
        }
        return result;
    }

    /// <summary>"%IW1234.0.0" / "4000" 등에서 첫 정수를 추출. 실패 시 null.</summary>
    private static int? TryParseNum(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"\d+");
        return m.Success && int.TryParse(m.Value, out var n) ? n : null;
    }

    /// <summary>
    /// 시스템 주소 저장 (사용 체크된 시스템만)
    /// </summary>
    private void SaveSystemBase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# System Base Address Configuration");
            sb.AppendLine("# 시스템 타입별 글로벌 주소 설정");
            sb.AppendLine("# 자동 생성됨 - TAG Wizard에서 편집");
            sb.AppendLine();

            // 사용 체크된 시스템만 저장
            var enabledSystems = _systemBaseRows.Where(row => row.IsEnabled).ToList();

            if (enabledSystems.Count == 0)
            {
                sb.AppendLine("# 사용 중인 시스템이 없습니다.");
                sb.AppendLine("# TAG Wizard에서 사용할 시스템을 선택하고 주소를 설정하세요.");
            }
            else
            {
                foreach (var row in enabledSystems)
                {
                    sb.AppendLine($"@SYSTEM {row.SystemType}");

                    if (!string.IsNullOrWhiteSpace(row.IW_Base) && int.TryParse(row.IW_Base, out _))
                        sb.AppendLine($"@IW_BASE {row.IW_Base}");

                    if (!string.IsNullOrWhiteSpace(row.QW_Base) && int.TryParse(row.QW_Base, out _))
                        sb.AppendLine($"@QW_BASE {row.QW_Base}");

                    if (!string.IsNullOrWhiteSpace(row.MW_Base) && int.TryParse(row.MW_Base, out _))
                        sb.AppendLine($"@MW_BASE {row.MW_Base}");

                    sb.AppendLine();
                }
            }

            var systemBaseContent = sb.ToString();
            // txt 파일 쓰기 제거 — AASX 내 FBTagMapPresets 가 유일한 진실원.
            var cp1 = GetOrCreateControlProps();
            if (cp1 != null)
                ControlIoLegacyMigration.applySystemBaseToPresets(cp1.FBTagMapPresets, systemBaseContent);

            SystemBaseStatusText.Text = $"✓ 저장 완료 ({enabledSystems.Count}개 시스템) | {DateTime.Now:HH:mm:ss}";

            DialogHelpers.ShowThemedMessageBox(
                $"시스템 주소 설정이 저장되었습니다.\n\n사용 중인 시스템: {enabledSystems.Count}개",
                "저장 완료",
                MessageBoxButton.OK,
                "✓");
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"저장 실패:\n\n{ex.Message}",
                "오류",
                MessageBoxButton.OK,
                "✖");

            SystemBaseStatusText.Text = $"저장 실패: {ex.Message}";
        }
    }

    #endregion

    #region Tab 2: Flow 주소

    /// <summary>
    /// Flow 주소 로드 — ACTIVE 시스템에 속한 Flow 만 대상.
    /// (Passive 시스템의 Flow 는 PLC 주소 설정 대상이 아님)
    /// </summary>
    private void LoadFlowBase()
    {
        try
        {
            _flowBaseRows.Clear();

            var projects = Queries.allProjects(_store);
            var flows = projects.IsEmpty
                ? new System.Collections.Generic.List<Flow>()
                : Queries.activeSystemsOf(projects.Head.Id, _store)
                    .SelectMany(sys => Queries.flowsOf(sys.Id, _store))
                    .ToList();
            var flowNames = flows
                .Select(f => f.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();

            // 기존 flow_base.txt 파일에서 설정 읽기
            var existingConfig = ParseFlowBaseFile();

            // 각 Flow에 대해 행 생성 (1000 단위로 자동 할당)
            for (int i = 0; i < flowNames.Count; i++)
            {
                var flowName = flowNames[i];
                var row = new FlowBaseRow { FlowName = flowName };

                if (existingConfig.TryGetValue(flowName, out var config))
                {
                    // 기존 설정이 있으면 사용
                    row.IW_Base = config.IW_Base?.ToString() ?? "";
                    row.QW_Base = config.QW_Base?.ToString() ?? "";
                    row.MW_Base = config.MW_Base?.ToString() ?? "";
                }
                else
                {
                    // 기존 설정이 없으면 1000 단위로 자동 할당 (0, 1000, 2000, ...)
                    int baseAddress = i * 1000;
                    row.IW_Base = baseAddress.ToString();
                    row.QW_Base = baseAddress.ToString();
                    row.MW_Base = baseAddress.ToString();
                }

                _flowBaseRows.Add(row);
            }

            FlowBaseStatusText.Text = flowNames.Count > 0
                ? $"{flowNames.Count}개의 Flow를 찾았습니다."
                : "프로젝트에 Flow가 없습니다.";
        }
        catch (Exception ex)
        {
            FlowBaseStatusText.Text = $"로드 실패: {ex.Message}";
        }
    }

    /// <summary>
    /// Flow 별 BaseAddressOverride 를 ControlFlowProperties 에서 직접 읽어온다 (외부 파일 불필요).
    /// </summary>
    private Dictionary<string, (int? IW_Base, int? QW_Base, int? MW_Base)> ParseFlowBaseFile()
    {
        var result = new Dictionary<string, (int? IW_Base, int? QW_Base, int? MW_Base)>(StringComparer.OrdinalIgnoreCase);
        foreach (var flow in _store.Flows.Values)
        {
            var cfpOpt = flow.GetControlProperties();
            if (!FSharpOption<ControlFlowProperties>.get_IsSome(cfpOpt)) continue;
            var ov = cfpOpt.Value.BaseAddressOverride;
            if (!FSharpOption<FBBaseAddressSet>.get_IsSome(ov)) continue;
            var ba = ov.Value;
            result[flow.Name] = (TryParseNum(ba.InputBase), TryParseNum(ba.OutputBase), TryParseNum(ba.MemoryBase));
        }
        return result;
    }

    /// <summary>
    /// Flow 주소 저장
    /// </summary>
    private void SaveFlowBase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Flow Base Address Configuration");
            sb.AppendLine("# Flow별 로컬 주소 설정");
            sb.AppendLine("# 자동 생성됨 - TAG Wizard에서 편집");
            sb.AppendLine();

            // 주소가 하나라도 설정된 Flow만 저장
            var flowsWithAddress = _flowBaseRows.Where(row =>
                !string.IsNullOrWhiteSpace(row.IW_Base) ||
                !string.IsNullOrWhiteSpace(row.QW_Base) ||
                !string.IsNullOrWhiteSpace(row.MW_Base)).ToList();

            if (flowsWithAddress.Count == 0)
            {
                sb.AppendLine("# 예시:");
                sb.AppendLine("# @FLOW Flow1");
                sb.AppendLine("# @IW_BASE 4000");
                sb.AppendLine("# @QW_BASE 4000");
                sb.AppendLine("# @MW_BASE 10000");
            }
            else
            {
                foreach (var row in flowsWithAddress)
                {
                    sb.AppendLine($"@FLOW {row.FlowName}");

                    if (!string.IsNullOrWhiteSpace(row.IW_Base) && int.TryParse(row.IW_Base, out _))
                        sb.AppendLine($"@IW_BASE {row.IW_Base}");

                    if (!string.IsNullOrWhiteSpace(row.QW_Base) && int.TryParse(row.QW_Base, out _))
                        sb.AppendLine($"@QW_BASE {row.QW_Base}");

                    if (!string.IsNullOrWhiteSpace(row.MW_Base) && int.TryParse(row.MW_Base, out _))
                        sb.AppendLine($"@MW_BASE {row.MW_Base}");

                    sb.AppendLine();
                }
            }

            var flowBaseContent = sb.ToString();
            // txt 파일 쓰기 제거 — Flow 별 BaseAddressOverride 는 ControlFlowProperties 에만 기록.
            // Flow 별 BaseAddressOverride 로 이식 (레거시 IoFlowBase 대체)
            var store = _store;
            if (store != null)
            {
                var flowMap = ControlIoLegacyMigration.parseFlowBase(flowBaseContent);
                foreach (var flow in store.Flows.Values)
                {
                    if (flowMap.TryGetValue(flow.Name, out var baseSet))
                    {
                        var cfpOpt = flow.GetControlProperties();
                        ControlFlowProperties cfp;
                        if (FSharpOption<ControlFlowProperties>.get_IsSome(cfpOpt))
                            cfp = cfpOpt.Value;
                        else
                        {
                            cfp = new ControlFlowProperties();
                            flow.SetControlProperties(cfp);
                        }
                        cfp.BaseAddressOverride = FSharpOption<FBBaseAddressSet>.Some(baseSet);
                    }
                }
            }

            FlowBaseStatusText.Text = $"✓ 저장 완료 | {DateTime.Now:HH:mm:ss}";

            DialogHelpers.ShowThemedMessageBox(
                "Flow 주소 설정이 저장되었습니다.",
                "저장 완료",
                MessageBoxButton.OK,
                "✓");
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"저장 실패:\n\n{ex.Message}",
                "오류",
                MessageBoxButton.OK,
                "✖");

            FlowBaseStatusText.Text = $"저장 실패: {ex.Message}";
        }
    }

    #endregion

    #region Tab 3: 신호 템플릿

    /// <summary>
    /// SystemType 목록 로드 — 프로젝트 속성의 "SystemType 프리셋" 이 단일 진실원.
    /// 이미 저장된 FBTagMap Preset 은 합쳐서 함께 표시 (사용자가 추가한 커스텀 포함).
    /// </summary>
    private void LoadDeviceTemplateList()
    {
        try
        {
            DeviceTemplateListBox.Items.Clear();

            var names = new System.Collections.Generic.SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            // 1순위: 프로젝트 속성 프리셋
            foreach (var t in Promaker.Services.SystemTypePresetProvider.GetSystemTypes())
                names.Add(t);
            // 2순위: 이미 AASX 안에 존재하는 FBTagMap Preset 키 (프리셋에 없는 사용자 추가분)
            foreach (var kv in Promaker.Services.FBTagMapStore.LoadAll(_store))
                names.Add(kv.Key);

            if (names.Count == 0)
            {
                DeviceTemplateStatusText.Text = "등록된 SystemType 프리셋이 없습니다. 프로젝트 속성에서 프리셋을 추가하세요.";
                return;
            }

            // 사전 seed — 사용자가 SystemType 을 클릭하지 않아도 모든 타입의
            // FBTagMapPreset 이 임베디드 디폴트 템플릿으로 채워지고 AASX 에 저장된다.
            // (Step 3 신호 생성이 클릭과 무관하게 동작하도록 보장.)
            SeedAllSystemTypes(names);

            foreach (var n in names)
                DeviceTemplateListBox.Items.Add(n);

            // 기본 선택: 첫 항목 (정렬된 상태)
            DeviceTemplateListBox.SelectedItem = names.First();

            DeviceTemplateStatusText.Text = $"{names.Count}개의 SystemType 이 발견되었습니다.";
        }
        catch (Exception ex)
        {
            DeviceTemplateStatusText.Text = $"목록 로드 실패: {ex.Message}";
        }
    }

    /// <summary>
    /// 모든 SystemType 의 FBTagMapPreset 을 사전에 seed. 빈 섹션만 채우므로
    /// 사용자 편집 데이터는 보존된다. 변경된 preset 만 저장한다.
    /// </summary>
    private void SeedAllSystemTypes(System.Collections.Generic.IEnumerable<string> systemTypes)
    {
        try
        {
            var presets = Promaker.Services.FBTagMapStore.LoadAll(_store);
            foreach (var systemType in systemTypes)
            {
                if (string.IsNullOrWhiteSpace(systemType)) continue;
                if (!presets.TryGetValue(systemType, out var presetDto))
                    presetDto = new Promaker.Services.FBTagMapPresetDto();

                if (SeedPresetDtoIfEmpty(presetDto, systemType))
                    Promaker.Services.FBTagMapStore.Save(_store, systemType, presetDto);
            }
        }
        catch (Exception ex)
        {
            // seed 실패는 치명적이지 않음 — 사용자가 클릭 시 다시 시도된다.
            DeviceTemplateStatusText.Text = $"사전 seed 일부 실패: {ex.Message}";
        }
    }

    /// <summary>
    /// 장치 템플릿 선택 시 내용 로드. ListBox 항목은 순수 SystemType 이름.
    /// </summary>
    private void DeviceTemplateListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DeviceTemplateListBox.SelectedItem is string systemType)
        {
            LoadDeviceTemplate(systemType);
        }
    }

    /// <summary>
    /// 장치 템플릿 로드 — AASX 내 FBTagMapPreset.(Iw|Qw|Mw)Patterns 를 단일 진실원으로 사용.
    /// Preset 의 해당 섹션이 비어있으면 임베디드 기본 템플릿(DefaultTemplatesRO) 으로 seed 후 Preset 에 저장.
    /// </summary>
    private void LoadDeviceTemplate(string systemType)
    {
        try
        {
            // 이전 버전 호환: ".txt" 가 섞여 들어오면 벗겨냄
            if (systemType.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                systemType = Path.GetFileNameWithoutExtension(systemType);

            _currentDeviceTemplateFile = systemType; // SystemType 식별자
            CurrentDeviceTemplateText.Text = systemType;

            // Preset 조회 (없으면 신규) → 비어있는 섹션에 한해 DefaultTemplates 로 seed → 다시 저장
            var presets = Promaker.Services.FBTagMapStore.LoadAll(_store);
            if (!presets.TryGetValue(systemType, out var presetDto))
                presetDto = new Promaker.Services.FBTagMapPresetDto();

            bool seeded = SeedPresetDtoIfEmpty(presetDto, systemType);
            if (seeded)
                Promaker.Services.FBTagMapStore.Save(_store, systemType, presetDto);

            var currentFb = GlobalFBTypeCombo?.SelectedItem as string ?? presetDto.FBTagMapName ?? "";

            _iwSignalRows.Clear();
            foreach (var e in presetDto.IwPatterns)
                _iwSignalRows.Add(HookAutoSave(new SignalPatternRow { ApiName = e.ApiName, Pattern = e.Pattern, TargetFBType = currentFb, TargetFBPort = e.TargetFBPort }));
            _qwSignalRows.Clear();
            foreach (var e in presetDto.QwPatterns)
                _qwSignalRows.Add(HookAutoSave(new SignalPatternRow { ApiName = e.ApiName, Pattern = e.Pattern, TargetFBType = currentFb, TargetFBPort = e.TargetFBPort }));
            _mwSignalRows.Clear();
            foreach (var e in presetDto.MwPatterns)
                _mwSignalRows.Add(HookAutoSave(new SignalPatternRow { ApiName = e.ApiName, Pattern = e.Pattern, TargetFBType = currentFb, TargetFBPort = e.TargetFBPort }));

            var totalCount = presetDto.IwPatterns.Count + presetDto.QwPatterns.Count + presetDto.MwPatterns.Count;
            DeviceTemplateStatusText.Text = $"✓ 로드 완료 | IW: {presetDto.IwPatterns.Count}, QW: {presetDto.QwPatterns.Count}, MW: {presetDto.MwPatterns.Count} | 총 {totalCount}개 신호";

            // AUX 포트 행 로드 (distinct API 이름 집계)
            var apis = presetDto.IwPatterns.Select(p => p.ApiName)
                .Concat(presetDto.QwPatterns.Select(p => p.ApiName))
                .Concat(presetDto.MwPatterns.Select(p => p.ApiName))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            LoadAuxPortRows(systemType, apis);
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"템플릿 로드 실패:\n\n{ex.Message}",
                "오류",
                MessageBoxButton.OK,
                "✖");

            DeviceTemplateStatusText.Text = $"로드 실패: {ex.Message}";
            ClearSignalGrids();
        }
    }

    /// <summary>
    /// presetDto 의 IW/QW/MW 섹션이 모두 비어있으면 "SystemType 프리셋" 의 API 목록으로 seed.
    /// 프리셋에 해당 SystemType 이 있고 API 리스트가 비어있지 않으면 각 API 당 IW/QW/MW 기본 패턴 한 줄씩 추가.
    /// </summary>
    private static bool SeedPresetDtoIfEmpty(Promaker.Services.FBTagMapPresetDto dto, string systemType)
    {
        // 섹션별 독립 seed — 한 섹션이 비어있지 않다고 다른 빈 섹션 seed 까지 막지 않는다.
        bool changed = false;

        // 1순위: 임베디드 디폴트 템플릿
        if (Promaker.Services.TemplateManager.DefaultTemplatesRO.TryGetValue(
                systemType + ".txt", out var embedded)
            && !string.IsNullOrWhiteSpace(embedded))
        {
            var (iw, qw, mw) = Promaker.Services.PresetTemplateSeeder.Parse(embedded);
            if (dto.IwPatterns.Count == 0 && iw.Count > 0)
            {
                foreach (var (api, pat) in iw)
                    dto.IwPatterns.Add(new Promaker.Services.SignalPatternEntryDto { ApiName = api, Pattern = pat });
                changed = true;
            }
            if (dto.QwPatterns.Count == 0 && qw.Count > 0)
            {
                foreach (var (api, pat) in qw)
                    dto.QwPatterns.Add(new Promaker.Services.SignalPatternEntryDto { ApiName = api, Pattern = pat });
                changed = true;
            }
            if (dto.MwPatterns.Count == 0 && mw.Count > 0)
            {
                foreach (var (api, pat) in mw)
                    dto.MwPatterns.Add(new Promaker.Services.SignalPatternEntryDto { ApiName = api, Pattern = pat });
                changed = true;
            }
            if (changed) return true;
        }

        // 2순위: 모든 섹션이 비어있을 때만 프로젝트 프리셋 API 기반 폴백.
        if (dto.IwPatterns.Count > 0 || dto.QwPatterns.Count > 0 || dto.MwPatterns.Count > 0)
            return false;

        var apis = Promaker.Services.SystemTypePresetProvider.GetApiNames(systemType);
        if (apis.Length == 0) return false;

        foreach (var api in apis)
        {
            dto.IwPatterns.Add(new Promaker.Services.SignalPatternEntryDto { ApiName = api, Pattern = "W_$(F)_WRS_$(D)_$(A)" });
            dto.QwPatterns.Add(new Promaker.Services.SignalPatternEntryDto { ApiName = api, Pattern = "W_$(F)_SOL_$(D)_$(A)" });
            dto.MwPatterns.Add(new Promaker.Services.SignalPatternEntryDto { ApiName = api, Pattern = "W_$(F)_M_$(D)_$(A)" });
        }
        return true;
    }

    /// <summary>
    /// 신호 그리드 초기화
    /// </summary>
    private void ClearSignalGrids()
    {
        _iwSignalRows.Clear();
        _qwSignalRows.Clear();
        _mwSignalRows.Clear();
        _auxPortRows.Clear();
    }

    /// <summary>
    /// 주어진 SystemType 에 대해 AUX 포트 행을 로드.
    /// FBTagMapPreset 에 기존 설정이 있으면 값 채움. API 목록은 signal template 에서 전달받은 distinct API 이름들.
    /// SystemType 당 단일 FB 타입을 글로벌 콤보에도 반영.
    /// </summary>
    private void LoadAuxPortRows(string systemType, IEnumerable<string> apis)
    {
        _auxPortRows.Clear();

        // 현재 preset 로드 (해당 SystemType 의 FBTagMapName 과 AUX 맵)
        var presets = Promaker.Services.FBTagMapStore.LoadAll(_store);
        var fbType = "";
        var existingAutoMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var existingComMap  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (presets.TryGetValue(systemType, out var preset))
        {
            fbType = preset.FBTagMapName ?? "";
            if (preset.AutoAuxPortMap != null)
                foreach (var kv in preset.AutoAuxPortMap) existingAutoMap[kv.Key] = kv.Value;
            if (preset.ComAuxPortMap != null)
                foreach (var kv in preset.ComAuxPortMap) existingComMap[kv.Key] = kv.Value;
        }

        // 글로벌 FB 타입 콤보 동기화 (이벤트 미발화 상태에서 선택 — 후속 AuxPortRow 주입과 충돌 방지)
        if (GlobalFBTypeCombo != null)
        {
            GlobalFBTypeCombo.SelectionChanged -= GlobalFBType_Changed;
            GlobalFBTypeCombo.SelectedItem =
                string.IsNullOrEmpty(fbType) ? null : WizFBTypes.FirstOrDefault(x => x == fbType);
            GlobalFBTypeCombo.SelectionChanged += GlobalFBType_Changed;
        }

        foreach (var api in apis)
        {
            var row = new AuxPortRow
            {
                ApiName      = api,
                TargetFBType = fbType,
                AutoAuxPort  = existingAutoMap.TryGetValue(api, out var a) ? a : "",
                ComAuxPort   = existingComMap.TryGetValue(api, out var c) ? c : "",
            };
            _auxPortRows.Add(HookAutoSave(row));
        }
    }

    /// <summary>
    /// SignalPatternRow 에 PropertyChanged 구독을 붙여 TargetFBPort/Pattern/ApiName 변경 시 Preset 자동 persist.
    /// </summary>
    private SignalPatternRow HookAutoSave(SignalPatternRow row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SignalPatternRow.TargetFBPort)
                              or nameof(SignalPatternRow.Pattern)
                              or nameof(SignalPatternRow.ApiName))
                PersistCurrentPreset();
        };
        return row;
    }

    /// <summary>AuxPortRow 에 PropertyChanged 구독을 붙여 AutoAuxPort/ComAuxPort 변경 시 Preset 자동 persist.</summary>
    private AuxPortRow HookAutoSave(AuxPortRow row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AuxPortRow.AutoAuxPort)
                              or nameof(AuxPortRow.ComAuxPort))
                PersistCurrentPreset();
        };
        return row;
    }

    /// <summary>
    /// 현재 SystemType 의 Preset 을 즉시 저장 — 💾 저장 버튼을 누르지 않아도 수정 사항이 유지되게 함.
    /// </summary>
    private void PersistCurrentPreset()
    {
        var systemType = _currentDeviceTemplateFile;
        if (string.IsNullOrWhiteSpace(systemType)) return;

        var presets = Promaker.Services.FBTagMapStore.LoadAll(_store);
        if (!presets.TryGetValue(systemType, out var presetDto))
            presetDto = new Promaker.Services.FBTagMapPresetDto();

        var selectedFb = GlobalFBTypeCombo?.SelectedItem as string;
        if (!string.IsNullOrWhiteSpace(selectedFb))
            presetDto.FBTagMapName = selectedFb;

        presetDto.IwPatterns.Clear();
        foreach (var row in _iwSignalRows)
            if (!string.IsNullOrWhiteSpace(row.ApiName))
                presetDto.IwPatterns.Add(new Promaker.Services.SignalPatternEntryDto { ApiName = row.ApiName, Pattern = row.Pattern, TargetFBPort = row.TargetFBPort ?? "" });

        presetDto.QwPatterns.Clear();
        foreach (var row in _qwSignalRows)
            if (!string.IsNullOrWhiteSpace(row.ApiName))
                presetDto.QwPatterns.Add(new Promaker.Services.SignalPatternEntryDto { ApiName = row.ApiName, Pattern = row.Pattern, TargetFBPort = row.TargetFBPort ?? "" });

        presetDto.MwPatterns.Clear();
        foreach (var row in _mwSignalRows)
            if (!string.IsNullOrWhiteSpace(row.ApiName))
                presetDto.MwPatterns.Add(new Promaker.Services.SignalPatternEntryDto { ApiName = row.ApiName, Pattern = row.Pattern, TargetFBPort = row.TargetFBPort ?? "" });

        presetDto.AutoAuxPortMap.Clear();
        presetDto.ComAuxPortMap.Clear();
        foreach (var row in _auxPortRows)
        {
            if (string.IsNullOrWhiteSpace(row.ApiName)) continue;
            if (!string.IsNullOrWhiteSpace(row.AutoAuxPort))
                presetDto.AutoAuxPortMap[row.ApiName] = row.AutoAuxPort;
            if (!string.IsNullOrWhiteSpace(row.ComAuxPort))
                presetDto.ComAuxPortMap[row.ApiName] = row.ComAuxPort;
        }

        // 코드 생성용 Ports 도 재생성 (SaveDeviceTemplate_Click 와 동일 규칙).
        presetDto.Ports.Clear();
        void AddPorts(System.Collections.Generic.List<Promaker.Services.SignalPatternEntryDto> entries, string direction, bool isDummy)
        {
            foreach (var e in entries)
            {
                if (string.IsNullOrWhiteSpace(e.TargetFBPort)) continue;
                presetDto.Ports.Add(new Promaker.Services.FBTagMapPortDto
                {
                    FBPort     = e.TargetFBPort,
                    Direction  = direction,
                    DataType   = "BOOL",
                    TagPattern = e.Pattern ?? "",
                    IsDummy    = isDummy,
                });
            }
        }
        AddPorts(presetDto.IwPatterns, "Input",  false);
        AddPorts(presetDto.QwPatterns, "Output", false);
        AddPorts(presetDto.MwPatterns, "Input",  true);

        Promaker.Services.FBTagMapStore.Save(_store, systemType, presetDto);
    }

    /// <summary>
    /// 장치 템플릿 저장 — AASX 내 FBTagMapPreset 에 IW/QW/MW 패턴을 기록.
    /// 외부 txt 파일은 더 이상 쓰지 않음 (Preset 이 유일한 진실원).
    /// </summary>
    private void SaveDeviceTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_currentDeviceTemplateFile))
        {
            DeviceTemplateStatusText.Text = "먼저 SystemType 을 선택하세요.";
            return;
        }

        try
        {
            var systemType = _currentDeviceTemplateFile; // LoadDeviceTemplate 이 SystemType 을 보관

            var presets = Promaker.Services.FBTagMapStore.LoadAll(_store);
            if (!presets.TryGetValue(systemType, out var presetDto))
                presetDto = new Promaker.Services.FBTagMapPresetDto();

            // 글로벌 FB 타입 반영
            var selectedFb = GlobalFBTypeCombo?.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(selectedFb))
                presetDto.FBTagMapName = selectedFb;

            // IW/QW/MW 그리드 → Preset (ApiName 이 비어있는 행은 스킵)
            presetDto.IwPatterns.Clear();
            foreach (var row in _iwSignalRows)
                if (!string.IsNullOrWhiteSpace(row.ApiName))
                    presetDto.IwPatterns.Add(new Promaker.Services.SignalPatternEntryDto { ApiName = row.ApiName, Pattern = row.Pattern, TargetFBPort = row.TargetFBPort ?? "" });

            presetDto.QwPatterns.Clear();
            foreach (var row in _qwSignalRows)
                if (!string.IsNullOrWhiteSpace(row.ApiName))
                    presetDto.QwPatterns.Add(new Promaker.Services.SignalPatternEntryDto { ApiName = row.ApiName, Pattern = row.Pattern, TargetFBPort = row.TargetFBPort ?? "" });

            presetDto.MwPatterns.Clear();
            foreach (var row in _mwSignalRows)
                if (!string.IsNullOrWhiteSpace(row.ApiName))
                    presetDto.MwPatterns.Add(new Promaker.Services.SignalPatternEntryDto { ApiName = row.ApiName, Pattern = row.Pattern, TargetFBPort = row.TargetFBPort ?? "" });

            // AUX 포트 매핑도 함께 반영
            presetDto.AutoAuxPortMap.Clear();
            presetDto.ComAuxPortMap.Clear();
            foreach (var row in _auxPortRows)
            {
                if (string.IsNullOrWhiteSpace(row.ApiName)) continue;
                if (!string.IsNullOrWhiteSpace(row.AutoAuxPort))
                    presetDto.AutoAuxPortMap[row.ApiName] = row.AutoAuxPort;
                if (!string.IsNullOrWhiteSpace(row.ComAuxPort))
                    presetDto.ComAuxPortMap[row.ApiName] = row.ComAuxPort;
            }

            // 코드 생성용 FBTagMapTemplate(Ports) 를 신호 패턴으로부터 재생성 — codegen 은 Ports 를 사용.
            // TargetFBPort 가 비어있는 엔트리는 FB 에 연결할 수 없으므로 제외.
            presetDto.Ports.Clear();
            void AddPorts(System.Collections.Generic.List<Promaker.Services.SignalPatternEntryDto> entries, string direction, bool isDummy)
            {
                foreach (var e in entries)
                {
                    if (string.IsNullOrWhiteSpace(e.TargetFBPort)) continue;
                    presetDto.Ports.Add(new Promaker.Services.FBTagMapPortDto
                    {
                        FBPort     = e.TargetFBPort,
                        Direction  = direction,
                        DataType   = "BOOL",
                        TagPattern = e.Pattern ?? "",
                        IsDummy    = isDummy,
                    });
                }
            }
            AddPorts(presetDto.IwPatterns, "Input",  false);
            AddPorts(presetDto.QwPatterns, "Output", false);
            AddPorts(presetDto.MwPatterns, "Input",  true);  // MW = Dummy 메모리

            Promaker.Services.FBTagMapStore.Save(_store, systemType, presetDto);

            var totalCount = _iwSignalRows.Count + _qwSignalRows.Count + _mwSignalRows.Count;
            DeviceTemplateStatusText.Text = $"✓ AASX 저장 완료 | IW: {_iwSignalRows.Count}, QW: {_qwSignalRows.Count}, MW: {_mwSignalRows.Count} | 총 {totalCount}개 신호";

            DialogHelpers.ShowThemedMessageBox(
                $"SystemType '{systemType}' 의 신호 템플릿이 AASX(프로젝트 모델) 에 저장되었습니다.\n외부 txt 파일은 사용되지 않습니다.",
                "저장 완료",
                MessageBoxButton.OK,
                "✓");
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"템플릿 저장 실패:\n\n{ex.Message}",
                "오류",
                MessageBoxButton.OK,
                "✖");

            DeviceTemplateStatusText.Text = $"저장 실패: {ex.Message}";
        }
    }

    /// <summary>
    /// IW 행 추가
    /// </summary>
    private void AddIwRow_Click(object sender, RoutedEventArgs e)
    {
        var fb = GlobalFBTypeCombo?.SelectedItem as string ?? "";
        _iwSignalRows.Add(HookAutoSave(new SignalPatternRow { ApiName = DefaultApiName(), Pattern = "W_$(F)_WRS_$(D)_$(A)", TargetFBType = fb }));
        PersistCurrentPreset();
    }

    /// <summary>
    /// IW 행 삭제 — 삭제 후 다음 행을 자동 선택해 연속 삭제가 가능하게 함.
    /// </summary>
    private void RemoveIwRow_Click(object sender, RoutedEventArgs e) =>
        RemoveSelectedAndAdvance(IwSignalGrid, _iwSignalRows);

    /// <summary>
    /// QW 행 추가
    /// </summary>
    private void AddQwRow_Click(object sender, RoutedEventArgs e)
    {
        var fb = GlobalFBTypeCombo?.SelectedItem as string ?? "";
        _qwSignalRows.Add(HookAutoSave(new SignalPatternRow { ApiName = DefaultApiName(), Pattern = "W_$(F)_SOL_$(D)_$(A)", TargetFBType = fb }));
        PersistCurrentPreset();
    }

    /// <summary>
    /// QW 행 삭제 — 삭제 후 다음 행 자동 선택.
    /// </summary>
    private void RemoveQwRow_Click(object sender, RoutedEventArgs e) =>
        RemoveSelectedAndAdvance(QwSignalGrid, _qwSignalRows);

    /// <summary>
    /// MW 행 추가
    /// </summary>
    private void AddMwRow_Click(object sender, RoutedEventArgs e)
    {
        var fb = GlobalFBTypeCombo?.SelectedItem as string ?? "";
        _mwSignalRows.Add(HookAutoSave(new SignalPatternRow { ApiName = DefaultApiName(), Pattern = "W_$(F)_M_$(D)_$(A)", TargetFBType = fb }));
        PersistCurrentPreset();
    }

    /// <summary>
    /// MW 행 삭제 — 삭제 후 다음 행 자동 선택.
    /// </summary>
    private void RemoveMwRow_Click(object sender, RoutedEventArgs e) =>
        RemoveSelectedAndAdvance(MwSignalGrid, _mwSignalRows);

    /// <summary>
    /// 선택된 행 제거 후 가장 작은 삭제 인덱스 위치로 커서 이동.
    /// 삭제 버튼을 연속으로 눌러 여러 행을 순차 제거할 수 있게 한다.
    /// </summary>
    private void RemoveSelectedAndAdvance<T>(
        System.Windows.Controls.DataGrid grid,
        ObservableCollection<T> rows) where T : class
    {
        if (grid == null || rows == null || rows.Count == 0) return;
        var selected = grid.SelectedItems.Cast<T>().ToList();
        if (selected.Count == 0) return;

        // 삭제될 행들의 최소 인덱스를 기록 — 삭제 후 그 자리의 행이 새 선택.
        int anchor = selected.Min(r => rows.IndexOf(r));

        foreach (var row in selected)
            rows.Remove(row);

        // 행 제거 후 Preset 도 반영 — 💾 없이 자동 persist.
        PersistCurrentPreset();

        grid.SelectedItems.Clear();
        if (rows.Count == 0) return;

        int next = Math.Min(anchor, rows.Count - 1);
        if (next < 0) return;
        var target = rows[next];
        grid.SelectedItem = target;
        grid.CurrentItem = target;
        grid.ScrollIntoView(target);
        grid.Focus();
    }

    #endregion
}
