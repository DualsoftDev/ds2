using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Ds2.CSV;
using Promaker.Presentation;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Microsoft.Win32;

namespace Promaker.Dialogs;

public class CsvRowViewModel
{
    public string FlowName { get; set; } = "";
    public string WorkName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string SystemName { get; set; } = "";
    public string ApiName { get; set; } = "";
    public string InName { get; set; } = "";
    public string InAddress { get; set; } = "";
    public string OutName { get; set; } = "";
    public string OutAddress { get; set; } = "";
}

public enum CsvImportMode
{
    Standard9,
    Basic3
}

public class BasicCsvRowViewModel
{
    public string FlowName { get; set; } = "";
    public string WorkName { get; set; } = "";
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }
    public string CallSummary { get; set; } = "";
}

public partial class CsvImportDialog : Window
{
    private const string DefaultImportedName = "csv_import";
    private const string DefaultSourceText = "또는 아래에 CSV를 직접 붙여넣으세요.";
    private const string EmptyPreviewText = "CSV 내용을 붙여넣거나 CSV 파일 불러오기를 누르세요.";
    private const string PreviewFailureText = "미리보기를 생성하지 못했습니다.";
    private const string SampleCsv = @"Flow,Work,Device,System,Api,InName,InAddress,OutName,OutAddress
Cutting,Load,Cylinder,Cutting_Cylinder,Up,입력신호,X10A0,출력신호,Y10B0
Cutting,Load,Sensor,Cutting_Sensor,Detect,,X10A2,,
Cutting,Load,Motor,Cutting_Motor,Run,,X10A3,,Y10B3
Cutting,Unload,Cylinder,Cutting_Cylinder,Down,하강신호,X10B0,하강출력,Y10C0
Cutting,Unload,Conveyor,Cutting_Conveyor,Forward,,,,Y10C1
Assembly,PartIn,Gripper,Assembly_Gripper,Grip,그립신호,X20A0,그립출력,Y20B0
Assembly,PartIn,Gripper,Assembly_Gripper,Release,릴리즈신호,X20A1,릴리즈출력,Y20B1
Assembly,PartIn,Sensor,Assembly_Sensor,Detect,,X20A2,,
Assembly,Process,Press,Assembly_Press,Down,프레스하강,X20C0,프레스출력,Y20D0
Assembly,Process,Press,Assembly_Press,Up,프레스상승,X20C1,프레스상승출력,Y20D1
Assembly,PartOut,Ejector,Assembly_Ejector,Push,,X20E0,,Y20F0
Assembly,PartOut,Ejector,Assembly_Ejector,Return,,X20E1,,Y20F1";

    private const string SampleBasicCsv = @"FLOW,WORK,CALL
투입,리프트작업,리프트.상승>리프트.투입위치정지>리프트.하강
투입,컨베이어작업,컨베이어.이송시작>위치센서A.감지>컨베이어.이송정지;컨베이어.이송시작>위치센서B.감지>컨베이어.이송정지
가공,고정작업,클램프.전진>클램프.고정확인
가공,드릴링작업,드릴.회전시작>드릴축.하강>드릴축.상승>드릴.회전정지
검사,밀착작업,측정헤드.하강>측정헤드.밀착확인
검사,측정작업,측정기.측정시작>측정기.결과판정>측정헤드.상승
반출,로봇추출,로봇.제품파지>로봇.반출위치이동>로봇.제품해제>로봇.원점복귀";

    private const string LlmPromptBasic = @"너는 자동화 공정 사양을 DS2 기본 CSV(ds2-basic-csv/v1)로 변환하는 생성기다.
아래 규칙을 따르고, 사용자가 공법을 설명하면 CSV만 출력한다.

[출력]
- 설명, 마크다운, 코드 펜스 없이 CSV 본문만 출력한다.
- 헤더는 정확히 FLOW,WORK,CALL 3열이다.
- 한 행은 Work 하나이며 같은 Flow도 매 행 FLOW 값을 반복한다.
- 데이터 행 순서 = Work 실행 순서다. 인접 Work는 자동으로 StartReset 연결되며 Flow가 바뀌어도 이어진다.
- 모든 구분자는 반각이다. 전각 문자(＞ ； ，)를 쓰지 않는다.

[CALL 문법]
- Call 이름은 반드시 '디바이스.액션' 형식이다(점 정확히 1개).
- '>' 는 순차(Start) 연결, ';' 는 별도 경로 구분이다.
- 여러 경로의 노드와 엣지는 합집합으로 병합되어 하나의 DAG가 된다.
- 같은 Call 이름은 동일 노드다. 공유·분기·합류는 전체 이름을 각 경로에 반복해 표현한다.
  예: 컨베이어.시작>센서A.감지>컨베이어.정지;컨베이어.시작>센서B.감지>컨베이어.정지
- 별칭 문법(ID=디바이스.액션)은 없다. '=' 를 쓰지 않는다.
- 합류 노드는 모든 선행 경로가 완료(AND)된 후 시작된다. OR 합류는 표현할 수 없다.
- 같은 Call의 별도 재실행은 한 Work 안에 표현할 수 없다(순환으로 거부). Work를 분할한다.
- 자기 Edge, 순환, 빈 노드/경로를 만들지 않는다.

[디바이스 규칙]
- 실린더·모터류 구동 디바이스는 상보 동작 쌍(전진-후진, 상승-하강, ON-OFF, 클램프-언클램프)을 함께 기재한다.
  공법에 반대 동작이 있는데 누락하면 안 된다. API가 1개뿐인 디바이스는 불러오기 시 경고 대상이다(센서류는 예외).
- 디바이스/액션 이름에 . > ; = 쉼표 따옴표를 쓰지 않는다.
- 예약어: 디바이스 BUFFER·CLEAR, 액션 DO·'-', '@' 접두 금지.

[정확성]
- 입력에 없는 센서, 완료확인, 안전동작, 원점복귀를 임의로 추가하지 않는다.
- Flow와 Work는 사용자가 제시한 순서를 유지한다.
- 실행 관계가 불명확하면 추측하지 말고 질문한다.

[예시]
입력: 투입에서 리프트가 상승, 투입위치정지, 하강한다. 가공에서 클램프가 전진 후 고정확인하고, 드릴이 회전시작 후 드릴축이 하강, 상승하면 드릴이 회전정지한다.
출력:
FLOW,WORK,CALL
투입,리프트작업,리프트.상승>리프트.투입위치정지>리프트.하강
가공,고정작업,클램프.전진>클램프.고정확인
가공,드릴링작업,드릴.회전시작>드릴축.하강>드릴축.상승>드릴.회전정지

이제 공법을 설명해 주시면 위 규칙에 따라 CSV만 출력한다.";

    private CsvDocument? _document;
    private BasicCsvDocument? _basicDocument;
    private string _autoProjectName = DefaultImportedName;
    private string _autoSystemName = DefaultImportedName;
    private string _sourceDisplayName = "붙여넣기";
    private bool _loadingFileContent;

    public CsvImportDialog()
    {
        InitializeComponent();

        ProjectNameBox.Text = DefaultImportedName;
        SystemNameBox.Text = DefaultImportedName;
        SourceText.Text = DefaultSourceText;
        ResetPreview(EmptyPreviewText);
        UpdatePreviewGridVisibility();

        Loaded += (_, _) => ContentBox.Focus();
    }

    public string ProjectName => ProjectNameBox.Text.Trim();

    public string SystemName => SystemNameBox.Text.Trim();

    public CsvDocument Document =>
        _document ?? throw new InvalidOperationException("CSV document is not loaded.");

    public CsvImportMode SelectedMode =>
        BasicModeRadio?.IsChecked == true ? CsvImportMode.Basic3 : CsvImportMode.Standard9;

    public BasicCsvDocument BasicDocument =>
        _basicDocument ?? throw new InvalidOperationException("Basic CSV document is not loaded.");

    public string SourceDisplayName => _sourceDisplayName;

    private static string BuildPreviewSummary(CsvImportPreview preview, int entryCount)
    {
        var sb = new StringBuilder()
            .AppendLine($"✓ Flow: {preview.FlowNames.Length}개")
            .AppendLine($"✓ Work: {preview.WorkNames.Length}개")
            .AppendLine($"✓ Call: {preview.CallNames.Length}개")
            .AppendLine($"✓ Passive Device System: {preview.PassiveSystemNames.Length}개")
            .AppendLine();

        AppendSample(sb, "Flow 샘플", preview.FlowNames, 5);
        AppendSample(sb, "Work 샘플", preview.WorkNames, 5);
        AppendSample(sb, "Call 샘플", preview.CallNames, 5);

        if (entryCount > 100)
        {
            sb.AppendLine();
            sb.AppendLine($"※ 총 {entryCount}개 항목 중 100개만 미리보기에 표시됩니다.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildSyntheticWarningText(int syntheticApiCount) =>
        syntheticApiCount > 0
            ? $"⚠ Api 열이 비어 있는 {syntheticApiCount}개 항목은 Signal_<addr> 형식으로 자동 생성됩니다."
            : "";

    private static void AppendSample(StringBuilder sb, string label, IEnumerable<string> items, int take)
    {
        var sample = string.Join(", ", items.Take(take));
        if (!string.IsNullOrWhiteSpace(sample))
            sb.AppendLine($"{label}: {sample}");
    }

    private static bool ValidateRequired(Window owner, TextBox textBox, string label)
    {
        if (!string.IsNullOrWhiteSpace(textBox.Text?.Trim()))
            return true;

        DialogHelpers.Info(owner, $"{label} 이름을 입력하세요.", "CSV 불러오기");
        textBox.Focus();
        return false;
    }

    private static string OptionText(FSharpOption<string> value) =>
        value.GetOrDefault("");

    private static CsvRowViewModel ToRowViewModel(CsvEntry entry) =>
        new()
        {
            FlowName = entry.FlowName,
            WorkName = entry.WorkName,
            DeviceName = entry.DeviceName,
            SystemName = entry.SystemName,
            ApiName = entry.ApiName,
            InName = OptionText(entry.InName),
            InAddress = OptionText(entry.InAddress),
            OutName = OptionText(entry.OutName),
            OutAddress = OptionText(entry.OutAddress)
        };

    private void SetSourceDisplay(string displayName, string description)
    {
        _sourceDisplayName = displayName;
        SourceText.Text = description;
    }

    private void ResetDirectInputPreview()
    {
        SetSourceDisplay("붙여넣기", DefaultSourceText);
        ResetPreview(EmptyPreviewText);
    }

    private void SetPreviewState(
        CsvDocument? document,
        IEnumerable<CsvRowViewModel>? rows,
        string previewText,
        string? errorText = null,
        string? warningText = null)
    {
        _document = document;
        _basicDocument = null;
        PreviewGrid.ItemsSource = rows?.ToList();
        BasicPreviewGrid.ItemsSource = null;
        PreviewText.Text = previewText;
        ErrorBorder.Visibility = string.IsNullOrWhiteSpace(errorText) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = errorText ?? "";
        WarningBorder.Visibility = string.IsNullOrWhiteSpace(warningText) ? Visibility.Collapsed : Visibility.Visible;
        WarningText.Text = warningText ?? "";
    }

    private void ShowPreviewFailure(string message)
    {
        SetPreviewState(null, null, PreviewFailureText, errorText: message);
    }

    private void ShowInfo(string message, string title = "CSV 불러오기") =>
        DialogHelpers.Info(this, message, title);

    private void ShowError(string message, string title = "오류") =>
        DialogHelpers.Error(this, message, title);

    private void ApplyPreview(CsvDocument document, CsvImportPreview preview)
    {
        SetPreviewState(
            document,
            document.Entries.Take(100).Select(ToRowViewModel),
            BuildPreviewSummary(preview, document.Entries.Length),
            warningText: BuildSyntheticWarningText(preview.SyntheticApiCount));
    }

    private void SetBasicPreviewState(
        BasicCsvDocument? document,
        IEnumerable<BasicCsvRowViewModel>? rows,
        string previewText,
        string? errorText = null,
        string? warningText = null)
    {
        _basicDocument = document;
        _document = null;
        BasicPreviewGrid.ItemsSource = rows?.ToList();
        PreviewGrid.ItemsSource = null;
        PreviewText.Text = previewText;
        ErrorBorder.Visibility = string.IsNullOrWhiteSpace(errorText) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = errorText ?? "";
        WarningBorder.Visibility = string.IsNullOrWhiteSpace(warningText) ? Visibility.Collapsed : Visibility.Visible;
        WarningText.Text = warningText ?? "";
    }

    private void ApplyBasicPreview(BasicCsvDocument document, BasicCsvPreview preview)
    {
        var warnings = document.Warnings.ToList();
        SetBasicPreviewState(
            document,
            document.Works.Take(100).Select(ToBasicRowViewModel),
            BuildBasicPreviewSummary(preview, document.Works.Length),
            warningText: warnings.Count > 0 ? string.Join("\n", warnings) : null);
    }

    private static BasicCsvRowViewModel ToBasicRowViewModel(BasicCsvWork work)
    {
        var nodeKeys = work.Nodes.Select(node => node.Item1).ToList();
        var summary = string.Join(" · ", nodeKeys.Take(8));
        if (nodeKeys.Count > 8)
            summary += " …";

        return new BasicCsvRowViewModel
        {
            FlowName = work.FlowName,
            WorkName = work.WorkName,
            NodeCount = nodeKeys.Count,
            EdgeCount = work.Edges.Length,
            CallSummary = summary
        };
    }

    private static string BuildBasicPreviewSummary(BasicCsvPreview preview, int workCount)
    {
        var sb = new StringBuilder()
            .AppendLine($"✓ Flow: {preview.FlowNames.Length}개")
            .AppendLine($"✓ Work: {preview.WorkNames.Length}개 — 행 순서 StartReset 체인 {preview.WorkArrowCount}개")
            .AppendLine($"✓ Call 노드: {preview.CallNodeCount}개 / Start 엣지: {preview.CallEdgeCount}개")
            .AppendLine($"✓ Passive Device System: {preview.PassiveSystemNames.Length}개")
            .AppendLine();

        AppendSample(sb, "Flow 샘플", preview.FlowNames, 5);
        AppendSample(sb, "Work 샘플", preview.WorkNames, 5);
        AppendSample(sb, "Device 샘플", preview.PassiveSystemNames, 5);

        if (workCount > 100)
        {
            sb.AppendLine();
            sb.AppendLine($"※ 총 {workCount}개 Work 중 100개만 미리보기에 표시됩니다.");
        }

        return sb.ToString().TrimEnd();
    }

    private void UpdatePreviewGridVisibility()
    {
        var basic = SelectedMode == CsvImportMode.Basic3;
        PreviewBorderOf(basic ? Visibility.Collapsed : Visibility.Visible,
                        basic ? Visibility.Visible : Visibility.Collapsed);
        if (CopyPromptButton != null)
            CopyPromptButton.Visibility = basic ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PreviewBorderOf(Visibility standard, Visibility basic)
    {
        if (PreviewGrid?.Parent is FrameworkElement standardBorder)
            standardBorder.Visibility = standard;
        if (BasicPreviewBorder != null)
            BasicPreviewBorder.Visibility = basic;
    }

    private void ImportMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        UpdatePreviewGridVisibility();

        if (string.IsNullOrWhiteSpace(ContentBox.Text))
        {
            ResetDirectInputPreview();
            return;
        }

        TryLoadDocument();
    }

    private void ResetPreview(string message)
    {
        SetPreviewState(null, null, message);
    }

    private void ShowErrors(IEnumerable<string> errors)
    {
        SetPreviewState(null, null, PreviewFailureText, errorText: string.Join("\n", errors));
    }

    private void ApplyAutoNames(string defaultName)
    {
        var normalized = string.IsNullOrWhiteSpace(defaultName)
            ? DefaultImportedName
            : defaultName.Trim();

        UpdateAutoName(ProjectNameBox, ref _autoProjectName, normalized);
        UpdateAutoName(SystemNameBox, ref _autoSystemName, normalized);
    }

    private static void UpdateAutoName(TextBox textBox, ref string previousAutoName, string nextAutoName)
    {
        var current = textBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(current) || string.Equals(current, previousAutoName, StringComparison.Ordinal))
            textBox.Text = nextAutoName;

        previousAutoName = nextAutoName;
    }

    private bool TryLoadDocument()
    {
        var content = ContentBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            ResetPreview(EmptyPreviewText);
            return false;
        }

        if (SelectedMode == CsvImportMode.Basic3)
        {
            var basicResult = CsvImporter.parseBasicContent(content);
            if (basicResult.IsError)
            {
                ShowErrors(basicResult.ErrorValue);
                return false;
            }

            var basicDocument = basicResult.ResultValue;
            ApplyBasicPreview(basicDocument, CsvImporter.previewBasic(basicDocument));
            return true;
        }

        if (!TryGetDocument(CsvImporter.parseContent(content), out var document))
            return false;

        ApplyPreview(document, CsvImporter.preview(document));
        return true;
    }

    private bool TryGetDocument(FSharpResult<CsvDocument, FSharpList<string>> result, out CsvDocument document)
    {
        if (result.IsError)
        {
            ShowErrors(result.ErrorValue);
            document = default!;
            return false;
        }

        document = result.ResultValue;
        return true;
    }

    private void CopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMode != CsvImportMode.Basic3)
            return;

        try
        {
            Clipboard.SetText(LlmPromptBasic);
            ShowInfo(
                "LLM 생성 지침이 클립보드에 복사되었습니다.\n\n" +
                "ChatGPT·Gemini 등 다른 LLM에 붙여넣은 뒤 공법을 설명하면 CSV가 생성됩니다.\n" +
                "생성된 CSV를 이 창의 'CSV 내용'에 붙여넣으세요.",
                "지침 복사 완료");
        }
        catch (Exception ex)
        {
            ShowError($"클립보드 복사 실패: {ex.Message}");
        }
    }

    private void SaveSample_Click(object sender, RoutedEventArgs e)
    {
        var basic = SelectedMode == CsvImportMode.Basic3;
        var picker = new SaveFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv",
            DefaultExt = FileExtensions.Csv,
            FileName = basic ? "sample_basic.csv" : "sample.csv"
        };

        if (picker.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(picker.FileName, basic ? SampleBasicCsv : SampleCsv, Encoding.UTF8);
            ShowInfo($"샘플 CSV 파일이 저장되었습니다.\n\n{picker.FileName}", "샘플 저장 완료");
        }
        catch (Exception ex)
        {
            ShowError($"샘플 저장 실패: {ex.Message}");
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"
        };

        if (picker.ShowDialog() != true)
            return;

        try
        {
            _loadingFileContent = true;
            ContentBox.Text = CsvFileHelper.ReadAllTextShared(picker.FileName);
            SetSourceDisplay(Path.GetFileName(picker.FileName), $"원본: {Path.GetFileName(picker.FileName)}");
            ApplyAutoNames(Path.GetFileNameWithoutExtension(picker.FileName));
        }
        catch (Exception ex)
        {
            ShowPreviewFailure($"파일 읽기 실패: {ex.Message}");
        }
        finally
        {
            _loadingFileContent = false;
        }
    }

    private void ContentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        if (string.IsNullOrWhiteSpace(ContentBox.Text))
        {
            ResetDirectInputPreview();
            return;
        }

        if (!_loadingFileContent)
            SetSourceDisplay("붙여넣기", "원본: 직접 입력");

        TryLoadDocument();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRequired(this, ProjectNameBox, "Project") ||
            !ValidateRequired(this, SystemNameBox, "Active System"))
            return;

        if (!TryLoadDocument())
        {
            ShowInfo("유효한 CSV 내용을 먼저 입력하세요.");
            ContentBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
