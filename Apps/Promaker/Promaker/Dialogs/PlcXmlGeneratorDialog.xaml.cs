using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using AAStoXGI.LadderViewer.Models;
using AAStoXGI.LadderViewer.Renderers;
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

    private readonly LadderRenderer _ladderRenderer = new() { LineFactor = 0.3 };
    private XG5000Project? _ladderProject;
    private double _ladderZoom = 1.0;

    private const double ZoomStep = 0.2;
    private const double ZoomMin  = 0.1;
    private const double ZoomMax  = 4.0;

    public PlcXmlGeneratorDialog(DsStore store, string? currentFilePath = null)
    {
        InitializeComponent();
        _store = store;
        _currentFilePath = currentFilePath;

        SyncLadderTheme(ThemeManager.CurrentTheme);
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(AppTheme theme)
    {
        SyncLadderTheme(theme);
        RenderSelectedProgram();
    }

    private void SyncLadderTheme(AppTheme theme)
    {
        LadderRenderer.DarkMode = theme == AppTheme.Dark;
        LadderCanvas.Background = LadderRenderer.CanvasBackground;
    }

    // ── 생성 ─────────────────────────────────────────────────────────────────

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var cfg              = PlcConfig.Settings;
        var ioTemplateDir    = cfg.EffectiveIoTemplateDirPath;
        var xgiTemplatePath  = cfg.EffectiveXgiTemplatePath;

        if (!Directory.Exists(ioTemplateDir))
        {
            SetStatus("❌ IOList 템플릿 폴더가 존재하지 않습니다.", isError: true);
            return;
        }

        GenerateButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        _generatedXml = null;
        ClearLadder();
        SetStatus("생성 중...");

        try
        {
            var ioListResult = IoListPipeline.generate(_store, ioTemplateDir);
            var ioCount      = ioListResult.IoSignals.Length;
            var dummyCount   = ioListResult.DummySignals.Length;
            var ioErrCount   = ioListResult.Errors.Length;

            var xgiOpt = !string.IsNullOrEmpty(xgiTemplatePath) && File.Exists(xgiTemplatePath)
                ? Microsoft.FSharp.Core.FSharpOption<string>.Some(xgiTemplatePath)
                : Microsoft.FSharp.Core.FSharpOption<string>.None;

            // FBTagMap 프리셋은 첫 번째 ActiveSystem ControlSystemProperties에서 로드 (없으면 AppData JSON 폴백)
            var fbTagMaps = FBTagMapStore.ToFSharpMap(_store);
            var detailResult = Plc.Xgi.Api.generateXmlWithDetail(_store, fbTagMaps, xgiOpt);

            UpdateSummary(ioListResult, ioCount, dummyCount, ioErrCount, detailResult);

            if (detailResult.IsOk)
            {
                _generatedXml = detailResult.ResultValue.Xml;

                // 생성 결과를 store에 기록 → 다음 AASX 저장 시 SequenceControl 서브모델 포함
                Plc.Xgi.Api.persistFbMappings(_store, detailResult.ResultValue.FBResult);

                SaveButton.IsEnabled = true;
                var fbCount = detailResult.ResultValue.FBResult.FBMappings.Length;
                SetStatus($"✓ 완료 — IO {ioCount + dummyCount}개 신호, FB {fbCount}개 인스턴스");

                LoadLadder(_generatedXml);
                PreviewTabs.SelectedIndex = 1; // 래더 탭
            }
            else
            {
                SetStatus("❌ FB 생성 실패", isError: true);
                PreviewTabs.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"❌ 오류: {ex.Message}", isError: true);
        }
        finally
        {
            GenerateButton.IsEnabled = true;
        }
    }

    // ── 래더 뷰어 ─────────────────────────────────────────────────────────────

    private void LoadLadder(string xml)
    {
        try
        {
            _ladderProject = XmlParser.ParseProjectXml(xml);

            LadderProgramComboBox.SelectionChanged -= LadderProgram_SelectionChanged;
            LadderProgramComboBox.Items.Clear();
            foreach (var prog in _ladderProject.Programs)
                LadderProgramComboBox.Items.Add(prog.Name);
            LadderProgramComboBox.SelectionChanged += LadderProgram_SelectionChanged;

            if (LadderProgramComboBox.Items.Count > 0)
                LadderProgramComboBox.SelectedIndex = 0;
            else
                ClearLadder();
        }
        catch (Exception ex)
        {
            ClearLadder();
            SetStatus($"⚠ 래더 파싱 오류: {ex.Message}", isError: true);
        }
    }

    private void RenderSelectedProgram()
    {
        LadderCanvas.Children.Clear();
        if (_ladderProject == null || LadderProgramComboBox.SelectedItem is not string name) return;

        var program = _ladderProject.Programs.FirstOrDefault(p => p.Name == name);
        if (program?.Body == null || program.Body.Rungs.Count == 0) return;

        _ladderRenderer.RenderRungs(LadderCanvas, program.Body.Rungs);
    }

    private void ClearLadder()
    {
        LadderCanvas.Children.Clear();
        _ladderProject = null;

        LadderProgramComboBox.SelectionChanged -= LadderProgram_SelectionChanged;
        LadderProgramComboBox.Items.Clear();
        LadderProgramComboBox.SelectionChanged += LadderProgram_SelectionChanged;
    }

    private void LadderProgram_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
        => RenderSelectedProgram();

    // ── 래더 줌 ──────────────────────────────────────────────────────────────

    private void SetLadderZoom(double zoom)
    {
        _ladderZoom       = Math.Clamp(zoom, ZoomMin, ZoomMax);
        LadderScale.ScaleX = _ladderZoom;
        LadderScale.ScaleY = _ladderZoom;
        LadderZoomLabel.Text = $"{_ladderZoom * 100:0}%";
    }

    private void LadderZoomIn_Click(object sender, RoutedEventArgs e)
        => SetLadderZoom(Math.Round(_ladderZoom + ZoomStep, 2));

    private void LadderZoomOut_Click(object sender, RoutedEventArgs e)
        => SetLadderZoom(Math.Round(_ladderZoom - ZoomStep, 2));

    private void LadderZoomReset_Click(object sender, RoutedEventArgs e)
        => SetLadderZoom(1.0);

    private void LadderScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        SetLadderZoom(_ladderZoom + (e.Delta > 0 ? ZoomStep : -ZoomStep));
    }

    // ── 요약 텍스트 ──────────────────────────────────────────────────────────

    private void UpdateSummary(
        GenerationResult ioResult,
        int ioCount, int dummyCount, int ioErrCount,
        Microsoft.FSharp.Core.FSharpResult<Plc.Xgi.PlcXmlGenerationDetail, string> detailResult)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== IOList 신호 생성 ===");
        sb.AppendLine($"  IO 신호 (IW/QW): {ioCount}개");
        sb.AppendLine($"  Dummy 신호 (MW): {dummyCount}개");
        if (ioErrCount > 0)
        {
            sb.AppendLine($"  ⚠ 오류: {ioErrCount}개");
            foreach (var err in ioResult.Errors.Take(10))
                sb.AppendLine($"    - {err.Message}");
            if (ioErrCount > 10)
                sb.AppendLine($"    ... 외 {ioErrCount - 10}개");
        }
        else sb.AppendLine("  오류: 없음");
        sb.AppendLine();

        if (detailResult.IsError)
        {
            sb.AppendLine("=== FB 매핑 생성 ===");
            sb.AppendLine($"  ❌ 실패: {detailResult.ErrorValue}");
        }
        else
        {
            var fbResult   = detailResult.ResultValue.FBResult;
            var fbCount    = fbResult.FBMappings.Length;
            var fbErrCount = fbResult.Errors.Length;

            sb.AppendLine("=== FB 매핑 생성 ===");
            sb.AppendLine($"  FB 인스턴스: {fbCount}개");
            if (fbErrCount > 0)
            {
                sb.AppendLine($"  ⚠ 오류: {fbErrCount}개");
                foreach (var err in fbResult.Errors.Take(5))
                    sb.AppendLine($"    - {err.Message}");
            }
            else sb.AppendLine("  오류: 없음");
            sb.AppendLine();

            sb.AppendLine("=== FB 인스턴스 목록 ===");
            foreach (var m in fbResult.FBMappings)
            {
                sb.AppendLine($"  ■ {m.InstanceName}  ({m.FBType})");
                sb.AppendLine($"    Work:     {m.WorkName}");
                sb.AppendLine($"    AutoAux:  {m.AutoAuxBitName}");
                sb.AppendLine($"    Running:  {m.RunningBitName}   Done: {m.DoneBitName}");

                var inputs = m.InputMappings.ToArray();
                if (inputs.Length > 0)
                {
                    sb.AppendLine($"    입력 ({inputs.Length}개):");
                    foreach (var p in inputs)
                        sb.AppendLine($"      IN  {p.Item1,-22} → {p.Item2}");
                }

                var outputs = m.OutputMappings.ToArray();
                if (outputs.Length > 0)
                {
                    sb.AppendLine($"    출력 ({outputs.Length}개):");
                    foreach (var p in outputs)
                        sb.AppendLine($"      OUT {p.Item1,-22} → {p.Item2}");
                }
                sb.AppendLine();
            }
        }

        SummaryText.Text = sb.ToString().TrimEnd();
    }

    // ── 저장 ─────────────────────────────────────────────────────────────────

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_generatedXml == null) return;

        var modelName = !string.IsNullOrEmpty(_currentFilePath)
            ? Path.GetFileNameWithoutExtension(_currentFilePath)
            : "plc_output";

        var saveDialog = new SaveFileDialog
        {
            Title  = "PLC 저장",
            Filter = "XG5000 XML Files (*.xml)|*.xml|All Files (*.*)|*.*",
            FileName = $"{modelName}.xml"
        };

        if (saveDialog.ShowDialog(this) != true) return;

        var crlfXml = _generatedXml.Replace("\r\n", "\n").Replace("\n", "\r\n");
        File.WriteAllText(saveDialog.FileName, crlfXml, new System.Text.UTF8Encoding(false));

        SetStatus($"✓ 저장 완료: {Path.GetFileName(saveDialog.FileName)}");
        OpenWithXg5000(saveDialog.FileName);
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
