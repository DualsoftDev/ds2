using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Promaker.Services;
using Ds2.Store;

namespace Promaker.Dialogs;

public partial class TagWizardDialog
{
    /// <summary>
    /// Step 2 초기화: 3개 탭 로드
    /// </summary>
    private void LoadTemplateFileList()
    {
        // Tab 1: system_base.txt 로드
        LoadSystemBase();

        // Tab 2: flow_base.txt 로드
        LoadFlowBase();

        // Tab 3: 장치 템플릿 목록 로드
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

            // 템플릿 파일에서 SystemType 목록 추출
            var templateFiles = TemplateManager.GetDeviceTemplateFiles();
            var systemTypes = templateFiles
                .Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(s => s)
                .ToList();

            // 기존 system_base.txt 파일에서 설정 읽기
            var existingConfig = ParseSystemBaseFile();

            // 각 SystemType에 대해 행 생성
            foreach (var systemType in systemTypes)
            {
                var row = new SystemBaseRow { SystemType = systemType };

                if (existingConfig.TryGetValue(systemType, out var config))
                {
                    // 파일에 존재하면 사용 중으로 표시
                    row.IsEnabled = true;
                    row.IW_Base = config.IW_Base?.ToString() ?? "";
                    row.QW_Base = config.QW_Base?.ToString() ?? "";
                    row.MW_Base = config.MW_Base?.ToString() ?? "";
                }
                else
                {
                    // RBT는 기본적으로 사용 체크하고 기본값 설정
                    if (systemType.Equals("RBT", StringComparison.OrdinalIgnoreCase))
                    {
                        row.IsEnabled = true;
                        row.IW_Base = "3070";
                        row.QW_Base = "3070";
                        row.MW_Base = "9110";
                    }
                    else
                    {
                        row.IsEnabled = false;
                    }
                }

                _systemBaseRows.Add(row);
            }

            SystemBaseStatusText.Text = $"{systemTypes.Count}개의 시스템 타입을 찾았습니다.";
        }
        catch (Exception ex)
        {
            SystemBaseStatusText.Text = $"로드 실패: {ex.Message}";
        }
    }

    /// <summary>
    /// system_base.txt 파일 파싱 (기존 설정 읽기)
    /// </summary>
    private Dictionary<string, (int? IW_Base, int? QW_Base, int? MW_Base)> ParseSystemBaseFile()
    {
        var result = new Dictionary<string, (int? IW_Base, int? QW_Base, int? MW_Base)>();

        try
        {
            var content = TemplateManager.ReadTemplateFile("system_base.txt");
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            string? currentSystem = null;
            int? currentIW = null, currentQW = null, currentMW = null;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
                    continue;

                if (trimmed.StartsWith("@SYSTEM ", StringComparison.OrdinalIgnoreCase))
                {
                    // 이전 시스템 저장
                    if (currentSystem != null)
                    {
                        result[currentSystem] = (currentIW, currentQW, currentMW);
                    }

                    // 새 시스템 시작
                    currentSystem = trimmed.Substring(8).Trim();
                    currentIW = currentQW = currentMW = null;
                }
                else if (trimmed.StartsWith("@IW_BASE ", StringComparison.OrdinalIgnoreCase) && currentSystem != null)
                {
                    if (int.TryParse(trimmed.Substring(9).Trim(), out var val))
                        currentIW = val;
                }
                else if (trimmed.StartsWith("@QW_BASE ", StringComparison.OrdinalIgnoreCase) && currentSystem != null)
                {
                    if (int.TryParse(trimmed.Substring(9).Trim(), out var val))
                        currentQW = val;
                }
                else if (trimmed.StartsWith("@MW_BASE ", StringComparison.OrdinalIgnoreCase) && currentSystem != null)
                {
                    if (int.TryParse(trimmed.Substring(9).Trim(), out var val))
                        currentMW = val;
                }
            }

            // 마지막 시스템 저장
            if (currentSystem != null)
            {
                result[currentSystem] = (currentIW, currentQW, currentMW);
            }
        }
        catch
        {
            // 파일이 없거나 파싱 실패 시 빈 딕셔너리 반환
        }

        return result;
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

            TemplateManager.WriteTemplateFile("system_base.txt", sb.ToString());

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
    /// Flow 주소 로드 (모델에서 자동 추출)
    /// </summary>
    private void LoadFlowBase()
    {
        try
        {
            _flowBaseRows.Clear();

            // DsStore에서 Flow 목록 추출
            var flows = DsQuery.allFlows(_store);
            var flowNames = flows
                .Select(f => f.Name)
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
    /// flow_base.txt 파일 파싱 (기존 설정 읽기)
    /// </summary>
    private Dictionary<string, (int? IW_Base, int? QW_Base, int? MW_Base)> ParseFlowBaseFile()
    {
        var result = new Dictionary<string, (int? IW_Base, int? QW_Base, int? MW_Base)>();

        try
        {
            var content = TemplateManager.ReadTemplateFile("flow_base.txt");
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            string? currentFlow = null;
            int? currentIW = null, currentQW = null, currentMW = null;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
                    continue;

                if (trimmed.StartsWith("@FLOW ", StringComparison.OrdinalIgnoreCase))
                {
                    // 이전 Flow 저장
                    if (currentFlow != null)
                    {
                        result[currentFlow] = (currentIW, currentQW, currentMW);
                    }

                    // 새 Flow 시작
                    currentFlow = trimmed.Substring(6).Trim();
                    currentIW = currentQW = currentMW = null;
                }
                else if (trimmed.StartsWith("@IW_BASE ", StringComparison.OrdinalIgnoreCase) && currentFlow != null)
                {
                    if (int.TryParse(trimmed.Substring(9).Trim(), out var val))
                        currentIW = val;
                }
                else if (trimmed.StartsWith("@QW_BASE ", StringComparison.OrdinalIgnoreCase) && currentFlow != null)
                {
                    if (int.TryParse(trimmed.Substring(9).Trim(), out var val))
                        currentQW = val;
                }
                else if (trimmed.StartsWith("@MW_BASE ", StringComparison.OrdinalIgnoreCase) && currentFlow != null)
                {
                    if (int.TryParse(trimmed.Substring(9).Trim(), out var val))
                        currentMW = val;
                }
            }

            // 마지막 Flow 저장
            if (currentFlow != null)
            {
                result[currentFlow] = (currentIW, currentQW, currentMW);
            }
        }
        catch
        {
            // 파일이 없거나 파싱 실패 시 빈 딕셔너리 반환
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

            TemplateManager.WriteTemplateFile("flow_base.txt", sb.ToString());

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
    /// 장치 템플릿 파일 목록 로드
    /// </summary>
    private void LoadDeviceTemplateList()
    {
        try
        {
            DeviceTemplateListBox.Items.Clear();

            var files = TemplateManager.GetDeviceTemplateFiles();

            if (files.Count == 0)
            {
                DeviceTemplateStatusText.Text = "장치 템플릿 파일이 없습니다.";
                return;
            }

            foreach (var file in files)
            {
                DeviceTemplateListBox.Items.Add(file);
            }

            // RBT.txt를 기본 선택
            var defaultFile = files.FirstOrDefault(f => f?.Equals("RBT.txt", StringComparison.OrdinalIgnoreCase) == true);
            if (defaultFile != null)
            {
                DeviceTemplateListBox.SelectedItem = defaultFile;
            }
            else if (files.Count > 0)
            {
                DeviceTemplateListBox.SelectedIndex = 0;
            }

            DeviceTemplateStatusText.Text = $"{files.Count}개의 장치 템플릿이 발견되었습니다.";
        }
        catch (Exception ex)
        {
            DeviceTemplateStatusText.Text = $"파일 목록 로드 실패: {ex.Message}";
        }
    }

    /// <summary>
    /// 장치 템플릿 선택 시 내용 로드
    /// </summary>
    private void DeviceTemplateListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DeviceTemplateListBox.SelectedItem is string fileName)
        {
            LoadDeviceTemplate(fileName);
        }
    }

    /// <summary>
    /// 장치 템플릿 파일 로드 (DataGrid 방식)
    /// </summary>
    private void LoadDeviceTemplate(string fileName)
    {
        try
        {
            var filePath = Path.Combine(TemplateManager.TemplatesFolderPath, fileName);

            if (!File.Exists(filePath))
            {
                DeviceTemplateStatusText.Text = $"파일을 찾을 수 없습니다: {fileName}";
                ClearSignalGrids();
                return;
            }

            _currentDeviceTemplateFile = filePath;
            CurrentDeviceTemplateText.Text = fileName;

            // 파일 파싱: [IW], [QW], [MW] 섹션 분리
            var content = File.ReadAllText(filePath, Encoding.UTF8);
            var (iwSignals, qwSignals, mwSignals) = ParseTemplateContent(content);

            // DataGrid에 로드
            _iwSignalRows.Clear();
            foreach (var (apiName, pattern) in iwSignals)
            {
                _iwSignalRows.Add(new SignalPatternRow { ApiName = apiName, Pattern = pattern });
            }

            _qwSignalRows.Clear();
            foreach (var (apiName, pattern) in qwSignals)
            {
                _qwSignalRows.Add(new SignalPatternRow { ApiName = apiName, Pattern = pattern });
            }

            _mwSignalRows.Clear();
            foreach (var (apiName, pattern) in mwSignals)
            {
                _mwSignalRows.Add(new SignalPatternRow { ApiName = apiName, Pattern = pattern });
            }

            var fileInfo = new FileInfo(filePath);
            var totalCount = iwSignals.Count + qwSignals.Count + mwSignals.Count;
            DeviceTemplateStatusText.Text = $"✓ 로드 완료 | IW: {iwSignals.Count}, QW: {qwSignals.Count}, MW: {mwSignals.Count} | 총 {totalCount}개 신호";
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"파일 로드 실패:\n\n{ex.Message}",
                "오류",
                MessageBoxButton.OK,
                "✖");

            DeviceTemplateStatusText.Text = $"로드 실패: {ex.Message}";
            ClearSignalGrids();
        }
    }

    /// <summary>
    /// 템플릿 파일 내용 파싱 ([IW], [QW], [MW] 섹션 분리)
    /// </summary>
    private (List<(string, string)> iw, List<(string, string)> qw, List<(string, string)> mw) ParseTemplateContent(string content)
    {
        var iwSignals = new List<(string, string)>();
        var qwSignals = new List<(string, string)>();
        var mwSignals = new List<(string, string)>();

        var currentSection = "";
        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // 주석 또는 빈 줄 무시
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
                continue;

            // 섹션 헤더 감지: [IW], [QW], [MW], [RBT.IW] 등
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                var section = trimmed.Trim('[', ']').ToUpperInvariant();

                // 간소화된 형식 또는 레거시 형식 모두 지원
                if (section.EndsWith(".IW") || section == "IW")
                    currentSection = "IW";
                else if (section.EndsWith(".QW") || section == "QW")
                    currentSection = "QW";
                else if (section.EndsWith(".MW") || section == "MW")
                    currentSection = "MW";
                else
                    currentSection = "";

                continue;
            }

            // API: Pattern 형식 파싱
            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex > 0 && !string.IsNullOrEmpty(currentSection))
            {
                var apiName = trimmed.Substring(0, colonIndex).Trim();
                var pattern = trimmed.Substring(colonIndex + 1).Trim();

                if (currentSection == "IW")
                    iwSignals.Add((apiName, pattern));
                else if (currentSection == "QW")
                    qwSignals.Add((apiName, pattern));
                else if (currentSection == "MW")
                    mwSignals.Add((apiName, pattern));
            }
        }

        return (iwSignals, qwSignals, mwSignals);
    }

    /// <summary>
    /// 신호 그리드 초기화
    /// </summary>
    private void ClearSignalGrids()
    {
        _iwSignalRows.Clear();
        _qwSignalRows.Clear();
        _mwSignalRows.Clear();
    }

    /// <summary>
    /// 장치 템플릿 저장 버튼 클릭 (DataGrid 방식)
    /// </summary>
    private void SaveDeviceTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentDeviceTemplateFile))
        {
            DeviceTemplateStatusText.Text = "저장할 파일을 선택하세요.";
            return;
        }

        try
        {
            // DataGrid에서 텍스트 파일 형식으로 변환
            var sb = new StringBuilder();
            sb.AppendLine("# 신호 템플릿");
            sb.AppendLine($"# 파일명이 SystemType으로 사용됩니다");
            sb.AppendLine("# $(F)=Flow명, $(D)=Device명, $(A)=Api명");
            sb.AppendLine();

            // [IW] 섹션
            if (_iwSignalRows.Count > 0)
            {
                sb.AppendLine("[IW]");
                foreach (var row in _iwSignalRows)
                {
                    if (!string.IsNullOrWhiteSpace(row.ApiName))
                    {
                        sb.AppendLine($"{row.ApiName}: {row.Pattern}");
                    }
                }
                sb.AppendLine();
            }

            // [QW] 섹션
            if (_qwSignalRows.Count > 0)
            {
                sb.AppendLine("[QW]");
                foreach (var row in _qwSignalRows)
                {
                    if (!string.IsNullOrWhiteSpace(row.ApiName))
                    {
                        sb.AppendLine($"{row.ApiName}: {row.Pattern}");
                    }
                }
                sb.AppendLine();
            }

            // [MW] 섹션
            if (_mwSignalRows.Count > 0)
            {
                sb.AppendLine("[MW]");
                foreach (var row in _mwSignalRows)
                {
                    if (!string.IsNullOrWhiteSpace(row.ApiName))
                    {
                        sb.AppendLine($"{row.ApiName}: {row.Pattern}");
                    }
                }
            }

            File.WriteAllText(_currentDeviceTemplateFile, sb.ToString(), Encoding.UTF8);

            var totalCount = _iwSignalRows.Count + _qwSignalRows.Count + _mwSignalRows.Count;
            DeviceTemplateStatusText.Text = $"✓ 저장 완료 | IW: {_iwSignalRows.Count}, QW: {_qwSignalRows.Count}, MW: {_mwSignalRows.Count} | 총 {totalCount}개 신호";

            DialogHelpers.ShowThemedMessageBox(
                $"'{Path.GetFileName(_currentDeviceTemplateFile)}' 파일이 저장되었습니다.",
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
        _iwSignalRows.Add(new SignalPatternRow { ApiName = "", Pattern = "W_$(F)_I_$(D)_$(A)_LS" });
    }

    /// <summary>
    /// IW 행 삭제
    /// </summary>
    private void RemoveIwRow_Click(object sender, RoutedEventArgs e)
    {
        var selected = IwSignalGrid.SelectedItems.Cast<SignalPatternRow>().ToList();
        foreach (var row in selected)
        {
            _iwSignalRows.Remove(row);
        }
    }

    /// <summary>
    /// QW 행 추가
    /// </summary>
    private void AddQwRow_Click(object sender, RoutedEventArgs e)
    {
        _qwSignalRows.Add(new SignalPatternRow { ApiName = "", Pattern = "W_$(F)_Q_$(D)_$(A)_CMD" });
    }

    /// <summary>
    /// QW 행 삭제
    /// </summary>
    private void RemoveQwRow_Click(object sender, RoutedEventArgs e)
    {
        var selected = QwSignalGrid.SelectedItems.Cast<SignalPatternRow>().ToList();
        foreach (var row in selected)
        {
            _qwSignalRows.Remove(row);
        }
    }

    /// <summary>
    /// MW 행 추가
    /// </summary>
    private void AddMwRow_Click(object sender, RoutedEventArgs e)
    {
        _mwSignalRows.Add(new SignalPatternRow { ApiName = "", Pattern = "W_$(F)_M_$(D)_$(A)_BUSY" });
    }

    /// <summary>
    /// MW 행 삭제
    /// </summary>
    private void RemoveMwRow_Click(object sender, RoutedEventArgs e)
    {
        var selected = MwSignalGrid.SelectedItems.Cast<SignalPatternRow>().ToList();
        foreach (var row in selected)
        {
            _mwSignalRows.Remove(row);
        }
    }

    #endregion
}
