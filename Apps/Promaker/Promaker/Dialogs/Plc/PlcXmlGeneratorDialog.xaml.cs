using AAStoPLC.TagWizard;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using AAStoPLC.Ir;
using AAStoPLC.LadderEditor.Adapters;
using AAStoPLC.LadderEditor.Models;
using AAStoPLC.LadderEditor.Rendering;
using Ds2.Core.Store;
using Microsoft.Win32;
using Plc.Xgi;
using Promaker.Presentation;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class PlcXmlGeneratorDialog : Window
{
    private readonly DsStore _store;
    private readonly string? _currentFilePath;
    private string? _generatedXml;

    private IrProject? _irProject;
    /// <summary>현재 program 의 rung list — LadderEditorControl 이 binding.</summary>
    private readonly ObservableCollection<RungViewModel> _rungs = new();
    private readonly EditorContext _ladderCtx = new() { GridCols = 12, IsReadOnly = true };

    public PlcXmlGeneratorDialog(DsStore store, string? currentFilePath = null)
    {
        InitializeComponent();
        _store = store;
        _currentFilePath = currentFilePath;

        LadderView.Context = _ladderCtx;
        LadderView.Rungs   = _rungs;
        SyncLadderTheme(ThemeManager.CurrentTheme);
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;

        // 다이얼로그가 열리면 즉시 PLC 생성 (별도 버튼 없음)
        Loaded += (_, _) => RunGeneration();
    }

    private void OnThemeChanged(AppTheme theme)
    {
        SyncLadderTheme(theme);
        RenderSelectedProgram();
    }

    private void SyncLadderTheme(AppTheme theme)
    {
        LadderView.Theme = theme == AppTheme.Dark
            ? new DefaultDarkTheme()
            : new DefaultLightTheme();
    }

    // ── 생성 ─────────────────────────────────────────────────────────────────

    /// <summary>다이얼로그 Loaded 시점에 자동 실행되는 생성 루틴.</summary>
    private void RunGeneration()
    {
        var cfg              = PlcConfig.Settings;
        var xgiTemplatePath  = cfg.EffectiveXgiTemplatePath;

        SaveButton.IsEnabled = false;
        _generatedXml = null;
        ClearLadder();
        SetStatus("생성 중...");

        try
        {
            // IO 신호/Dummy 통계는 IoSignalPipeline facade 에서 추출 (IoListPipeline 직접 호출 없음).
            var ioBundle = IoSignalPipeline.GenerateAll(_store);

            var xgiOpt = !string.IsNullOrEmpty(xgiTemplatePath) && File.Exists(xgiTemplatePath)
                ? Microsoft.FSharp.Core.FSharpOption<string>.Some(xgiTemplatePath)
                : Microsoft.FSharp.Core.FSharpOption<string>.None;

            var fbTagMaps = FBTagMapStore.ToFSharpMap(_store);
            // 주소 할당은 F# 측 SignalPipeline 이 내부에서 수행 — 별도 override 불필요.
            var detailResult = Plc.Xgi.Api.generateXmlWithDetail(_store, fbTagMaps, xgiOpt);

            UpdateSummary(ioBundle, detailResult);

            if (detailResult.IsOk)
            {
                _generatedXml = detailResult.ResultValue.Xml;

                // 생성 결과를 store에 기록 → 다음 AASX 저장 시 SequenceControl 서브모델 포함
                Plc.Xgi.Api.persistFbMappings(_store, detailResult.ResultValue.Output);

                SaveButton.IsEnabled = true;
                var fbCount = detailResult.ResultValue.Output.CallPlans
                    .Select(p => p.InstanceName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                SetStatus($"✓ 완료 — IO {ioBundle.IoRows.Count + ioBundle.DummyRows.Count}개 신호, FB {fbCount}개 인스턴스");

                // XML 재파싱 없이 IR 그대로 사용 — RungViewModel 변환만.
                LoadLadder(detailResult.ResultValue.Project);
                PreviewTabs.SelectedIndex = 1; // 래더 탭
            }
            else
            {
                SetStatus("❌ FB 생성 실패 — 요약 탭 확인", isError: true);
                PreviewTabs.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"❌ 오류: {ex.Message}", isError: true);
        }
    }

    // ── 래더 뷰어 ─────────────────────────────────────────────────────────────

    private void LoadLadder(IrProject project)
    {
        _irProject = project;
        LadderProgramComboBox.SelectionChanged -= LadderProgram_SelectionChanged;
        LadderProgramComboBox.Items.Clear();
        foreach (var prog in project.Programs)
            LadderProgramComboBox.Items.Add(prog.Name);
        LadderProgramComboBox.SelectionChanged += LadderProgram_SelectionChanged;

        // 구분자 더미 POU (===이름===) 는 건너뛰고 첫 실 POU 선택.
        if (LadderProgramComboBox.Items.Count > 0)
        {
            int idx = 0;
            for (int i = 0; i < LadderProgramComboBox.Items.Count; i++)
            {
                var n = LadderProgramComboBox.Items[i] as string ?? "";
                if (!(n.StartsWith("=") && n.EndsWith("="))) { idx = i; break; }
            }
            LadderProgramComboBox.SelectedIndex = idx;
        }
        else
            ClearLadder();
    }

    private void RenderSelectedProgram()
    {
        _rungs.Clear();
        if (_irProject == null || LadderProgramComboBox.SelectedItem is not string name) return;
        var program = _irProject.Programs.FirstOrDefault(p => p.Name == name);
        if (program == null) return;
        foreach (var r in program.Rungs)
            _rungs.Add(IrRungAdapter.ToViewModel(r));
    }

    private void ClearLadder()
    {
        _rungs.Clear();
        _irProject = null;
        LadderProgramComboBox.SelectionChanged -= LadderProgram_SelectionChanged;
        LadderProgramComboBox.Items.Clear();
        LadderProgramComboBox.SelectionChanged += LadderProgram_SelectionChanged;
    }

    private void LadderProgram_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
        => RenderSelectedProgram();

    // ── 요약 텍스트 ──────────────────────────────────────────────────────────

    private const int MaxErrorsShown   = 10;
    private const int MaxCriticalShown = 15;
    private const int MaxWarningsShown = 10;

    private void UpdateSummary(
        IoSignalPipeline.GenerateResult ioBundle,
        Microsoft.FSharp.Core.FSharpResult<Plc.Xgi.PlcXmlGenerationDetail, string> detailResult)
    {
        var sb = new StringBuilder();
        AppendIoListSection(sb, ioBundle);
        sb.AppendLine();
        AppendFbSection(sb, detailResult);
        SummaryText.Text = sb.ToString().TrimEnd();
    }

    private static void AppendIoListSection(StringBuilder sb, IoSignalPipeline.GenerateResult bundle)
    {
        sb.AppendLine("=== IOList 신호 생성 ===");
        sb.AppendLine($"  IO 신호 (IW/QW): {bundle.IoRows.Count}개");
        sb.AppendLine($"  Dummy 신호 (MW): {bundle.DummyRows.Count}개");
        if (bundle.ErrorMessages.Count == 0)
        {
            sb.AppendLine("  오류: 없음");
            return;
        }
        sb.AppendLine($"  ⚠ 오류: {bundle.ErrorMessages.Count}개");
        AppendList(sb, bundle.ErrorMessages, MaxErrorsShown, "    - ");
    }

    private void AppendFbSection(
        StringBuilder sb,
        Microsoft.FSharp.Core.FSharpResult<Plc.Xgi.PlcXmlGenerationDetail, string> detailResult)
    {
        sb.AppendLine("=== FB 매핑 생성 ===");

        if (detailResult.IsError)
        {
            sb.AppendLine($"  ❌ 실패: {detailResult.ErrorValue}");
            return;
        }

        var output = detailResult.ResultValue.Output;
        var fbCount = output.CallPlans
            .Select(p => p.InstanceName)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        sb.AppendLine($"  FB 인스턴스: {fbCount}개 (Device 당 1개)");

        // Diagnostic.Severity 로 직접 분류 — SevError = PLC 동작 불가, SevWarning = 소프트 경고.
        var errors   = output.Diagnostics.Where(d => d.Severity.IsSevError).ToList();
        var warnings = output.Diagnostics.Where(d => d.Severity.IsSevWarning).ToList();

        if (errors.Count == 0) sb.AppendLine("  오류: 없음");
        else
        {
            sb.AppendLine();
            sb.AppendLine($"  🔴 오류 {errors.Count}건 (PLC 동작 불가):");
            AppendList(sb, errors.Select(e => e.Message), MaxCriticalShown, "    - ");
        }
        if (warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  🟡 경고 {warnings.Count}건:");
            AppendList(sb, warnings.Select(w => w.Message), MaxWarningsShown, "    - ");
        }
    }

    /// <summary>최대 N 항목까지 출력 + 초과분 요약.</summary>
    private static void AppendList(StringBuilder sb, IEnumerable<string> items, int max, string prefix)
    {
        var list = items.ToList();
        foreach (var s in list.Take(max)) sb.AppendLine(prefix + s);
        if (list.Count > max)
            sb.AppendLine($"{prefix}... 외 {list.Count - max}건");
    }

    // ── 저장 ─────────────────────────────────────────────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_generatedXml))
        {
            SetStatus("⚠ 저장할 XML 이 없습니다 — 먼저 생성이 성공해야 합니다.", isError: true);
            return;
        }

        var defaultName =
            !string.IsNullOrEmpty(_currentFilePath)
                ? Path.GetFileNameWithoutExtension(_currentFilePath) + ".xml"
                : "xgi_plc.xml";

        var dlg = new SaveFileDialog
        {
            Title      = "XGI 프로젝트 저장",
            Filter     = "XGI XML (*.xml)|*.xml|모든 파일 (*.*)|*.*",
            FileName   = defaultName,
            DefaultExt = ".xml",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            // UTF-8 BOM — XG5000 한글 인식 호환
            File.WriteAllText(dlg.FileName, _generatedXml,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            SetStatus($"✓ 저장 완료: {Path.GetFileName(dlg.FileName)}");
            OpenWithXg5000(dlg.FileName);
        }
        catch (Exception ex)
        {
            SetStatus($"❌ 저장 실패: {ex.Message}", isError: true);
        }
    }

    private void OpenWithXg5000(string filePath)
    {
        var xg5000Exe = PlcConfig.Settings.EffectiveXg5000ExePath;
        if (string.IsNullOrEmpty(xg5000Exe))
        {
            SetStatus("✓ 저장 완료 (XG5000 경로 미설정 — 프로젝트 속성에서 지정하세요)", isError: false);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = xg5000Exe,
                Arguments = $"\"{filePath}\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            SetStatus($"⚠ XG5000 실행 실패: {ex.Message}", isError: true);
        }
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? System.Windows.Media.Brushes.OrangeRed
            : (System.Windows.Media.Brush)FindResource("SecondaryTextBrush");
    }
}
