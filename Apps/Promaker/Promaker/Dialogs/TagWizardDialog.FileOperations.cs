using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Ds2.Core;
using Ds2.Core.Store;
using Microsoft.FSharp.Core;
using AAStoPLC.TagWizard;
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
    /// "Cylinder_5" → "Cylinder_#" 로 정규화. 그 외는 원본.
    /// 사용자 표시는 systemTypePreset 의 템플릿명을 따른다.
    /// </summary>
    private static string NormalizeSystemTypeForDisplay(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        const string prefix = "Cylinder_";
        if (name.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(prefix.Length), out _))
            return prefix + "#";
        return name;
    }

    /// <summary>"Cylinder_#" → ["Cylinder_1".."Cylinder_10"]. 그 외 원본 1개.</summary>
    private static IEnumerable<string> ExpandSystemTypeTemplate(string name)
    {
        if (string.Equals(name, "Cylinder_#", StringComparison.Ordinal))
            return Ds2.Core.Store.DevicePresets.Entries()
                .Select(t => t.Item1)
                .Where(s => s.StartsWith("Cylinder_") && int.TryParse(s.AsSpan("Cylinder_".Length), out _));
        return new[] { name };
    }

    /// <summary>
    /// 시스템 주소 로드 — Cylinder_N 은 Cylinder_# 단일 행으로 묶어 표시.
    /// </summary>
    private void LoadSystemBase()
    {
        try
        {
            _systemBaseRows.Clear();

            // SystemType 목록 — systemTypePreset (Cylinder_# 포함) 우선, AASX Preset 키 보완.
            var availableSet = new System.Collections.Generic.SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in SystemTypePresetProvider.GetSystemTypes())
                availableSet.Add(t);
            foreach (var kv in FBTagMapStore.LoadAll(_store))
                availableSet.Add(NormalizeSystemTypeForDisplay(kv.Key));
            var systemTypes = availableSet.ToList();

            // 각 Preset 의 BaseAddresses 를 그리드에 표시 — Cylinder_N 은 Cylinder_# 로 그룹.
            var existingConfig = ParseSystemBaseFile();
            var grouped = existingConfig
                .GroupBy(kv => NormalizeSystemTypeForDisplay(kv.Key), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var g in grouped)
            {
                var first = g.First().Value;
                _systemBaseRows.Add(new SystemBaseRow
                {
                    SystemType = g.Key,
                    IsEnabled  = true,
                    IW_Base    = first.IW_Base?.ToString() ?? "",
                    QW_Base    = first.QW_Base?.ToString() ?? "",
                    MW_Base    = first.MW_Base?.ToString() ?? "",
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
    /// AvailableSystemTypes 콤보 데이터 갱신 — systemTypePreset 의 템플릿명 그대로 (Cylinder_# 포함).
    /// AASX preset 키는 NormalizeSystemTypeForDisplay 로 정규화 후 합집합. 이미 추가된 타입은 제외.
    /// </summary>
    private void RefreshAvailableSystemTypes(System.Collections.Generic.List<string>? allTypes = null)
    {
        if (allTypes == null)
        {
            var set = new System.Collections.Generic.SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in SystemTypePresetProvider.GetSystemTypes())
                set.Add(t);
            foreach (var kv in FBTagMapStore.LoadAll(_store))
                set.Add(NormalizeSystemTypeForDisplay(kv.Key));
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
        var presets = FBTagMapStore.LoadAll(_store);
        foreach (var kv in presets)
        {
            var ba = kv.Value.BaseAddresses;
            if (ba == null) continue;
            var iw = TryParseNum(ba.InputBase);
            var qw = TryParseNum(ba.OutputBase);
            var mw = TryParseNum(ba.MemoryBase);
            if (iw == null && qw == null && mw == null) continue;
            result[kv.Key] = (iw, qw, mw);
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

    /// <summary>SystemBase 행을 FBTagMapPresets 에 직접 반영 — 텍스트 round-trip 불필요.</summary>
    private void SaveSystemBase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var enabled = _systemBaseRows.Where(r => r.IsEnabled).ToList();
            var cp = GetOrCreateControlProps();
            if (cp != null)
            {
                foreach (var row in enabled)
                    foreach (var concrete in ExpandSystemTypeTemplate(row.SystemType))
                    {
                        if (!cp.FBTagMapPresets.TryGetValue(concrete, out var preset))
                        {
                            preset = new FBTagMapPreset();
                            cp.FBTagMapPresets[concrete] = preset;
                        }
                        var ba = preset.BaseAddresses ?? new FBBaseAddressSet();
                        if (int.TryParse(row.IW_Base, out _)) ba.InputBase  = row.IW_Base;
                        if (int.TryParse(row.QW_Base, out _)) ba.OutputBase = row.QW_Base;
                        if (int.TryParse(row.MW_Base, out _)) ba.MemoryBase = row.MW_Base;
                        preset.BaseAddresses = ba;
                    }
            }
            SystemBaseStatusText.Text = $"✓ 저장 완료 ({enabled.Count}개 시스템) | {DateTime.Now:HH:mm:ss}";
            DialogHelpers.ShowThemedMessageBox(
                $"시스템 주소 설정이 저장되었습니다.\n\n사용 중인 시스템: {enabled.Count}개",
                "저장 완료", MessageBoxButton.OK, "✓");
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox($"저장 실패:\n\n{ex.Message}", "오류", MessageBoxButton.OK, "✖");
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

    /// <summary>FlowBase 행을 ControlFlowProperties.BaseAddressOverride 에 직접 반영 — 텍스트 round-trip 불필요.</summary>
    private void SaveFlowBase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rows = _flowBaseRows.ToDictionary(r => r.FlowName, StringComparer.OrdinalIgnoreCase);
            foreach (var flow in _store.Flows.Values)
            {
                if (!rows.TryGetValue(flow.Name, out var row)) continue;
                if (string.IsNullOrWhiteSpace(row.IW_Base)
                    && string.IsNullOrWhiteSpace(row.QW_Base)
                    && string.IsNullOrWhiteSpace(row.MW_Base)) continue;

                var cfpOpt = flow.GetControlProperties();
                ControlFlowProperties cfp;
                if (FSharpOption<ControlFlowProperties>.get_IsSome(cfpOpt))
                    cfp = cfpOpt.Value;
                else
                {
                    cfp = new ControlFlowProperties();
                    flow.SetControlProperties(cfp);
                }
                var ba = new FBBaseAddressSet();
                if (int.TryParse(row.IW_Base, out _)) ba.InputBase  = row.IW_Base;
                if (int.TryParse(row.QW_Base, out _)) ba.OutputBase = row.QW_Base;
                if (int.TryParse(row.MW_Base, out _)) ba.MemoryBase = row.MW_Base;
                cfp.BaseAddressOverride = FSharpOption<FBBaseAddressSet>.Some(ba);
            }
            FlowBaseStatusText.Text = $"✓ 저장 완료 | {DateTime.Now:HH:mm:ss}";
            DialogHelpers.ShowThemedMessageBox("Flow 주소 설정이 저장되었습니다.", "저장 완료", MessageBoxButton.OK, "✓");
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox($"저장 실패:\n\n{ex.Message}", "오류", MessageBoxButton.OK, "✖");
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
            // '#' 포함 SystemType 은 AddCall 용 템플릿 — TagWizard 의 구체 FB 매핑 대상이 아니므로 제외.
            static bool IsTemplate(string n) => !string.IsNullOrEmpty(n) && n.Contains('#');
            // 1순위: 프로젝트 속성 프리셋
            foreach (var t in SystemTypePresetProvider.GetSystemTypes())
                if (!IsTemplate(t)) names.Add(t);
            // 2순위: 이미 AASX 안에 존재하는 FBTagMap Preset 키 (프리셋에 없는 사용자 추가분)
            foreach (var kv in FBTagMapStore.LoadAll(_store))
                if (!IsTemplate(kv.Key)) names.Add(kv.Key);

            if (names.Count == 0)
            {
                DeviceTemplateStatusText.Text = "등록된 SystemType 프리셋이 없습니다. 프로젝트 속성에서 프리셋을 추가하세요.";
                return;
            }

            // 사전 seed — FBTagMapStore.LoadAll → RebuildPresetsFromJson 가
            // 모든 SystemType 의 preset 을 임베디드 JSON 으로 통째 재생성하므로 별도 호출 불필요.
            _ = FBTagMapStore.LoadAll(_store);

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
        // 로딩 중 — 행 setter 의 PropertyChanged 가 잘못된 SystemType 으로 auto-save 되는 것 방지.
        _isLoadingTemplate = true;
        try
        {
            // 이전 버전 호환: ".txt" 가 섞여 들어오면 벗겨냄
            if (systemType.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                systemType = Path.GetFileNameWithoutExtension(systemType);

            _currentDeviceTemplateFile = systemType; // SystemType 식별자
            CurrentDeviceTemplateText.Text = systemType;

            // 행을 먼저 비운다 — WizApiNames 변경으로 인한 ComboBox 재바인딩 시
            // 옛 SystemType 의 ApiName 이 새 list 에 없어 SelectedItem=null 트리거되는 race 방지.
            _iwSignalRows.Clear();
            _qwSignalRows.Clear();
            _mwSignalRows.Clear();
            _auxPortRows.Clear();

            // ApiName 콤보 갱신 — 현재 SystemType 의 프리셋 API 목록 (Robot 등) 포함.
            ReloadWizApiNames(systemType);

            // Preset 조회 (없으면 신규) → 비어있는 섹션에 한해 DefaultTemplates 로 seed → 다시 저장
            var presets = FBTagMapStore.LoadAll(_store);
            // FBTagMapStore.LoadAll 이 RebuildPresetsFromJson 를 통해 임베디드 JSON 으로
            // 매번 통째 재생성. 별도 seed 호출 불필요.
            if (!presets.TryGetValue(systemType, out var presetDto))
                presetDto = new FBTagMapPresetDto();

            // SystemType 별 preset 의 FBTagMapName 이 진실원 — 콤보 선택을 그에 맞게 즉시 동기화.
            var currentFb = presetDto.FBTagMapName ?? "";
            if (GlobalFBTypeCombo != null)
            {
                GlobalFBTypeCombo.SelectionChanged -= GlobalFBType_Changed;
                GlobalFBTypeCombo.SelectedItem =
                    string.IsNullOrEmpty(currentFb) ? null : WizFBTypes.FirstOrDefault(x => x == currentFb);
                GlobalFBTypeCombo.SelectionChanged += GlobalFBType_Changed;
            }

            // 행은 이미 try 블록 진입 시 Clear 됨. 모든 섹션 일괄 로드.
            foreach (var sec in AllSections())
                LoadSectionRows(sec, presetDto, currentFb);


            // SystemType 변경 시 32점 단위 보기 chunks 도 새 데이터로 재구성 (이전 SystemType 데이터 잔존 방지).
            RefreshChunkedViewsIfActive();

            var totalCount = presetDto.IwPatterns.Count + presetDto.QwPatterns.Count + presetDto.MwPatterns.Count;
            int iwSpare = presetDto.IwPatterns.Count(p => p.IsSpare);
            int qwSpare = presetDto.QwPatterns.Count(p => p.IsSpare);
            int mwSpare = presetDto.MwPatterns.Count(p => p.IsSpare);
            string SparePart(int n) => n > 0 ? $" ({n} 예비)" : "";
            DeviceTemplateStatusText.Text =
                $"✓ 로드 완료 | IW: {presetDto.IwPatterns.Count}{SparePart(iwSpare)}, " +
                $"QW: {presetDto.QwPatterns.Count}{SparePart(qwSpare)}, " +
                $"MW: {presetDto.MwPatterns.Count}{SparePart(mwSpare)} | 총 {totalCount}개 신호";

            // AUX 포트 행 로드 (distinct API 이름 집계).
            // 'Api_None' (글로벌 신호 sentinel) / '-' (빈 슬롯) 은 특정 ApiCall 미바인딩 → AUX 매핑 대상에서 제외.
            var apis = presetDto.IwPatterns.Select(p => p.ApiName)
                .Concat(presetDto.QwPatterns.Select(p => p.ApiName))
                .Concat(presetDto.MwPatterns.Select(p => p.ApiName))
                .Where(n => !string.IsNullOrWhiteSpace(n)
                         && !string.Equals(n, IoConstants.ApiNoneSentinel, StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(n, "-", StringComparison.Ordinal))
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
        finally
        {
            _isLoadingTemplate = false;
        }

        // 로딩 중 누적된 콤보 동기화 등을 AASX 에 1회 명시 저장 — 첫 진입 상태가 보존됨.
        PersistCurrentPreset();
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
    /// <summary>AUX 포트 심볼 콤보 후보 — 현 SystemType 의 preset IW/QW/MW 패턴 중 FB 포트가 설정된 항목.
    /// 자유 입력도 허용 (IsEditable=True). 자유 입력한 심볼은 코일 비트로 단독 럼 생성.</summary>
    private List<string> BuildAuxApiOptions(string systemType)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(systemType)) return result;
        var presets = FBTagMapStore.LoadAll(_store);
        if (!presets.TryGetValue(systemType, out var preset) || preset == null) return result;

        void Collect(IEnumerable<SignalPatternEntryDto> entries)
        {
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.Pattern)) continue;
                if (e.IsSpare) continue;
                // FB 포트 미설정 신호도 포함 — 단독 코일/외부 참조 가능.
                if (!result.Contains(e.Pattern, StringComparer.OrdinalIgnoreCase))
                    result.Add(e.Pattern);
            }
        }
        Collect(preset.IwPatterns);
        Collect(preset.QwPatterns);
        Collect(preset.MwPatterns);
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private void LoadAuxPortRows(string systemType, IEnumerable<string> apis)
    {
        _auxPortRows.Clear();

        // 현재 preset 로드 (해당 SystemType 의 FBTagMapName 과 AuxPortMap)
        var presets = FBTagMapStore.LoadAll(_store);
        var fbType = "";
        var auxEntries = new List<AuxPortMapEntryDto>();
        if (presets.TryGetValue(systemType, out var preset))
        {
            fbType = preset.FBTagMapName ?? "";
            if (preset.AuxPortMap != null)
                foreach (var e in preset.AuxPortMap)
                    if (e != null && !string.IsNullOrEmpty(e.ApiName))
                        auxEntries.Add(e);
        }

        // 글로벌 FB 타입 콤보 동기화 (이벤트 미발화 상태에서 선택 — 후속 AuxPortRow 주입과 충돌 방지)
        if (GlobalFBTypeCombo != null)
        {
            GlobalFBTypeCombo.SelectionChanged -= GlobalFBType_Changed;
            GlobalFBTypeCombo.SelectedItem =
                string.IsNullOrEmpty(fbType) ? null : WizFBTypes.FirstOrDefault(x => x == fbType);
            GlobalFBTypeCombo.SelectionChanged += GlobalFBType_Changed;
        }

        // Preset 의 기존 entry 만 표시 — 자동 빈 행 추가 X (사용자가 ➕ 행 추가 로 수동 추가).
        foreach (var e in auxEntries)
        {
            var row = new AuxPortRow
            {
                ApiName      = e.ApiName,
                TargetFBType = fbType,
                TargetFBPort = e.TargetFBPort ?? "",
                Kind         = string.IsNullOrEmpty(e.Kind) ? "DirectFB" : e.Kind,
                AuxKind      = string.IsNullOrEmpty(e.AuxKind) ? "AutoAux" : e.AuxKind,
                Condition    = DtoToCoreExpr(e.Condition),
                ApiOptions   = BuildAuxApiOptions(systemType),
            };
            _auxPortRows.Add(HookAutoSave(row));
        }
    }

    /// <summary>SignalPatternRow / AuxPortRow 가 자체 정의한 어떤 INPC 변경에서도 자동 persist.
    /// (필터 없이 모든 PropertyChanged 에 반응 — debounce 가 다회 호출을 1회 save 로 합쳐줌.)</summary>
    private TRow HookAutoSave<TRow>(TRow row) where TRow : INotifyPropertyChanged
    {
        row.PropertyChanged += (_, _) => PersistCurrentPreset();
        return row;
    }

    /// <summary>
    /// 현재 SystemType 의 Preset 을 저장 (auto-save).
    /// LoadDeviceTemplate 진행 중에는 race 방지 위해 skip.
    /// 짧은 시간(150ms) 내 다회 호출은 debounce 처리 — 그리드 일괄 변경 시 마지막 한 번만 실제 save.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _persistDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(150),
    };
    private bool _persistDebounceWired;

    private void PersistCurrentPreset()
    {
        if (_isLoadingTemplate) return;
        if (string.IsNullOrWhiteSpace(_currentDeviceTemplateFile)) return;

        if (!_persistDebounceWired)
        {
            _persistDebounceWired = true;
            _persistDebounce.Tick += (_, _) => { _persistDebounce.Stop(); PersistCurrentPresetNow(); };
        }
        _persistDebounce.Stop();
        _persistDebounce.Start();
    }

    /// <summary>실제 저장 본체 — debounce 만료 시 1회만 실행.</summary>
    private void PersistCurrentPresetNow()
    {
        var systemType = _currentDeviceTemplateFile;
        if (string.IsNullOrWhiteSpace(systemType)) return;

        // LoadOne — 단일 SystemType DTO 만 가져옴 (debounced auto-save 빈번 호출 최적화).
        var presetDto = FBTagMapStore.LoadOne(_store, systemType) ?? new FBTagMapPresetDto();

        var selectedFb = GlobalFBTypeCombo?.SelectedItem as string;
        if (!string.IsNullOrWhiteSpace(selectedFb))
            presetDto.FBTagMapName = selectedFb;

        // IW/QW/MW 모든 섹션 일괄 저장.
        foreach (var sec in AllSections())
            SaveSectionRows(sec, presetDto);

        // 통합 AuxPortMap 단일 진실원 — 한 행 = 한 entry.
        presetDto.AuxPortMap.Clear();
        foreach (var row in _auxPortRows)
        {
            if (string.IsNullOrWhiteSpace(row.ApiName)) continue;
            if (string.IsNullOrWhiteSpace(row.TargetFBPort)) continue;
            presetDto.AuxPortMap.Add(new AuxPortMapEntryDto
            {
                ApiName      = row.ApiName,
                TargetFBPort = row.TargetFBPort,
                Kind         = string.IsNullOrEmpty(row.Kind) ? "DirectFB" : row.Kind,
                AuxKind      = string.IsNullOrEmpty(row.AuxKind) ? "AutoAux" : row.AuxKind,
                Condition    = CoreToDtoExpr(row.Condition),
            });
        }

        // legacy Ports/FBTagMapTemplate 재생성 제거 — Direction 은 XGI_Template 가 단일 출처.
        FBTagMapStore.Save(_store, systemType, presetDto);
    }


    /// <summary>섹션 1개에 빈 행 추가 — 기본 패턴은 SignalSectionInfo 가 보유.</summary>
    private void AddSignalRow(SignalSectionInfo sec)
    {
        var fb = GlobalFBTypeCombo?.SelectedItem as string ?? "";
        sec.Rows.Add(HookAutoSave(new SignalPatternRow {
            ApiName = DefaultApiName(), Pattern = sec.DefaultPattern, TargetFBType = fb }));
        PersistCurrentPreset();
    }

    private void AddIwRow_Click(object sender, RoutedEventArgs e) => AddSignalRow(AllSections()[0]);
    private void AddQwRow_Click(object sender, RoutedEventArgs e) => AddSignalRow(AllSections()[1]);
    private void AddMwRow_Click(object sender, RoutedEventArgs e) => AddSignalRow(AllSections()[2]);

    private void RemoveIwRow_Click(object sender, RoutedEventArgs e) =>
        RemoveSelectedAndAdvance(IwSignalGrid, _iwSignalRows);
    private void RemoveQwRow_Click(object sender, RoutedEventArgs e) =>
        RemoveSelectedAndAdvance(QwSignalGrid, _qwSignalRows);
    private void RemoveMwRow_Click(object sender, RoutedEventArgs e) =>
        RemoveSelectedAndAdvance(MwSignalGrid, _mwSignalRows);

    private void MoveIwUp_Click(object sender, RoutedEventArgs e) =>
        MoveSelected(IwSignalGrid, _iwSignalRows, up: true);
    private void MoveIwDown_Click(object sender, RoutedEventArgs e) =>
        MoveSelected(IwSignalGrid, _iwSignalRows, up: false);
    private void MoveQwUp_Click(object sender, RoutedEventArgs e) =>
        MoveSelected(QwSignalGrid, _qwSignalRows, up: true);
    private void MoveQwDown_Click(object sender, RoutedEventArgs e) =>
        MoveSelected(QwSignalGrid, _qwSignalRows, up: false);
    private void MoveMwUp_Click(object sender, RoutedEventArgs e) =>
        MoveSelected(MwSignalGrid, _mwSignalRows, up: true);
    private void MoveMwDown_Click(object sender, RoutedEventArgs e) =>
        MoveSelected(MwSignalGrid, _mwSignalRows, up: false);

    /// <summary>선택된 행을 위/아래로 1칸 이동. 다중 선택 시 그룹 전체 이동.
    /// up=true 면 인덱스 오름차순으로 (위 행과 swap), 아래로는 내림차순으로 처리해 안전하게 이동.</summary>
    private void MoveSelected<T>(
        System.Windows.Controls.DataGrid grid,
        ObservableCollection<T> rows,
        bool up) where T : class
    {
        if (grid == null || rows == null || rows.Count < 2) return;
        var selected = grid.SelectedItems.Cast<T>().ToList();
        if (selected.Count == 0) return;

        var indices = selected.Select(r => rows.IndexOf(r)).Where(i => i >= 0).ToList();
        if (indices.Count == 0) return;

        if (up)
        {
            indices.Sort();
            if (indices[0] == 0) return;  // 이미 맨 위
            foreach (var i in indices) rows.Move(i, i - 1);
        }
        else
        {
            indices.Sort((a, b) => b.CompareTo(a));
            if (indices[0] == rows.Count - 1) return;  // 이미 맨 아래
            foreach (var i in indices) rows.Move(i, i + 1);
        }

        PersistCurrentPreset();

        grid.SelectedItems.Clear();
        foreach (var item in selected)
        {
            grid.SelectedItems.Add(item);
        }
        grid.CurrentItem = selected[0];
        grid.ScrollIntoView(selected[0]);
    }

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
