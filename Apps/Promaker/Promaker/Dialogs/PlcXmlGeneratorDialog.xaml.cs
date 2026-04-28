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
        LadderRenderer.DarkMode = theme == AppTheme.Dark;
        LadderCanvas.Background = LadderRenderer.CanvasBackground;
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
            // IOList 파이프라인은 여전히 디렉토리 입력을 받는다 — AASX Preset 에서 일시 디렉토리로 emit.
            using var tempDir = Promaker.Services.PresetToTempTemplateDir.Materialize(_store);
            var ioListResult = IoListPipeline.generate(_store, tempDir.Path);
            var ioCount      = ioListResult.IoSignals.Length;
            var dummyCount   = ioListResult.DummySignals.Length;
            var ioErrCount   = ioListResult.Errors.Length;

            var xgiOpt = !string.IsNullOrEmpty(xgiTemplatePath) && File.Exists(xgiTemplatePath)
                ? Microsoft.FSharp.Core.FSharpOption<string>.Some(xgiTemplatePath)
                : Microsoft.FSharp.Core.FSharpOption<string>.None;

            var fbTagMaps = FBTagMapStore.ToFSharpMap(_store);
            var detailResult = Plc.Xgi.Api.generateXmlWithDetail(_store, fbTagMaps, xgiOpt);

            UpdateSummary(ioListResult, ioCount, dummyCount, ioErrCount, detailResult);

            if (detailResult.IsOk)
            {
                _generatedXml = detailResult.ResultValue.Xml;

                // 생성 결과를 store에 기록 → 다음 AASX 저장 시 SequenceControl 서브모델 포함
                Plc.Xgi.Api.persistFbMappings(_store, detailResult.ResultValue.FBResult);

                SaveButton.IsEnabled = true;
                var fbCount = detailResult.ResultValue.FBResult.FBMappings
                    .Select(m => m.InstanceName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                SetStatus($"✓ 완료 — IO {ioCount + dummyCount}개 신호, FB {fbCount}개 인스턴스");

                LoadLadder(_generatedXml);
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

            // FBMappings 가 비어있지만 Errors 가 없을 때는 경고(FBTagMap 미설정 등)가 원인.
            try
            {
                var fbMaps = FBTagMapStore.ToFSharpMap(_store);
                var probe  = Plc.Xgi.Pipeline.generate(_store, fbMaps);
                // 같은 SystemType 에 여러 Device 가 있어도 사용자가 해야할 작업은 1회 — 중복 제거.
                var uniqueWarns = probe.Warnings
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (uniqueWarns.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"  해결해야 할 설정 ({uniqueWarns.Length}건):");
                    foreach (var w in uniqueWarns.Take(20))
                        sb.AppendLine($"    • {w}");
                    if (uniqueWarns.Length > 20)
                        sb.AppendLine($"    ... 외 {uniqueWarns.Length - 20}건");
                }
            }
            catch { /* probe 실패해도 원래 오류는 표시됨 */ }
        }
        else
        {
            var fbResult   = detailResult.ResultValue.FBResult;
            var fbErrCount = fbResult.Errors.Length;

            // InstanceName 기준으로 그룹화 — 같은 Device 의 여러 API Call 은 하나의 FB 로 표시.
            var deviceGroups = fbResult.FBMappings
                .GroupBy(m => m.InstanceName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var fbCount = deviceGroups.Count;

            sb.AppendLine("=== FB 매핑 생성 ===");
            sb.AppendLine($"  FB 인스턴스: {fbCount}개 (Device 당 1개)");
            if (fbErrCount > 0)
            {
                sb.AppendLine($"  ⚠ 오류: {fbErrCount}개");
                foreach (var err in fbResult.Errors.Take(5))
                    sb.AppendLine($"    - {err.Message}");
            }
            else sb.AppendLine("  오류: 없음");

            // 상세 입출력 포트 목록은 표시하지 않음 — 래더 탭에서 직접 확인.
        }

        SummaryText.Text = sb.ToString().TrimEnd();
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
