using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Ds2.Core.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;

namespace DSPilot.AasxSimulator;

public partial class MainWindow : Window
{
    private const int LogMaxLines = 5000;
    private const int LogTrimChunk = 1000;
    private static readonly TimeSpan LogFlushInterval = TimeSpan.FromMilliseconds(100);

    private readonly PlcConnectionSettings _plc;
    private readonly ConcurrentQueue<string> _logQueue = new();
    private readonly DispatcherTimer _logFlushTimer;
    private int _logLineCount;
    private CancellationTokenSource? _cts;
    private DsStore? _loadedStore;
    private string? _loadedAasxPath;

    public MainWindow()
    {
        InitializeComponent();

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        _plc = PlcConnectionSettings.FromConfig(config);
        TxtAasxPath.Text = config["AasxPath"] ?? @"C:\ds\ds2\Apps\DSPilot\DsCSV_0318_C.aasx";

        CmbPlcType.SelectedIndex = _plc.PlcType.Equals("LS", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        CmbPlcModel.SelectedIndex = _plc.LS.PlcModel switch { "XGK" => 1, "XGT" => 2, _ => 0 };
        CmbMxProtocol.SelectedIndex = _plc.Mitsubishi.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        ApplyPlcTypeToInputs();

        _logFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = LogFlushInterval,
        };
        _logFlushTimer.Tick += (_, _) => FlushLogQueue();
        _logFlushTimer.Start();

        Closing += OnWindowClosing;
        Closed += (_, _) =>
        {
            _logFlushTimer.Stop();
            // 외부 PLC dll 이 non-background thread 를 띄우면 process 가 살아남으므로 강제 종료.
            Application.Current?.Shutdown();
        };
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 시뮬 실행 중에 창을 닫으면 cancel 안 해주면 워커 루프가 계속 돌고 PLC dispose 도 안 됨.
        _cts?.Cancel();
    }

    private void ApplyPlcTypeToInputs()
    {
        var isLs = CmbPlcType.SelectedIndex == 0;
        if (isLs)
        {
            TxtIp.Text = _plc.LS.IpAddress;
            TxtPort.Text = _plc.LS.Port.ToString();
        }
        else
        {
            TxtIp.Text = _plc.Mitsubishi.IpAddress;
            TxtPort.Text = _plc.Mitsubishi.Port.ToString();
        }
        LblPlcModel.Visibility = isLs ? Visibility.Visible : Visibility.Collapsed;
        CmbPlcModel.Visibility = isLs ? Visibility.Visible : Visibility.Collapsed;
        LblMxProtocol.Visibility = isLs ? Visibility.Collapsed : Visibility.Visible;
        CmbMxProtocol.Visibility = isLs ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPlcTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyPlcTypeToInputs();
    }

    private void OnBrowseAasx(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "AASX 파일 (*.aasx)|*.aasx|모든 파일|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
        {
            TxtAasxPath.Text = dlg.FileName;
            _loadedStore = null;
        }
    }

    private void OnAasxDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnAasxDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            TxtAasxPath.Text = files[0];
            _loadedStore = null;
        }
    }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        while (_logQueue.TryDequeue(out _)) { }
        TxtLog.Clear();
        _logLineCount = 0;
    }

    private void OnValidate(object sender, RoutedEventArgs e)
    {
        if (!TryCommitPlcInputs(out var error))
        {
            MessageBox.Show(this, error, "설정 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var service = new FlowSimulationService { Log = AppendLog };
        var result = service.LoadAndValidate(TxtAasxPath.Text, out var store);

        if (!result.Success)
        {
            AppendLog($"❌ {result.ErrorMessage}");
            LblStatus.Text = "로드 실패";
            _loadedStore = null;
            return;
        }

        _loadedStore = store;
        _loadedAasxPath = TxtAasxPath.Text;
        AppendLog($"✅ 로드 성공: {Path.GetFileName(_loadedAasxPath)}");

        if (result.Issues.Count == 0)
        {
            AppendLog("✅ v10 검증 위반 없음");
            LblStatus.Text = "검증 통과";
        }
        else
        {
            AppendLog($"⚠️  v10 검증 위반 {result.Issues.Count} 건:");
            foreach (var issue in result.Issues)
                AppendLog($"   [{issue.Rule}] {issue.Message}");
            LblStatus.Text = $"v10 위반 {result.Issues.Count} 건";
        }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (!TryCommitPlcInputs(out var error))
        {
            MessageBox.Show(this, error, "설정 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var service = new FlowSimulationService
        {
            Log = AppendLog,
            CycleChanged = c => Dispatcher.BeginInvoke(() => LblCycle.Text = $"Cycle: {c}"),
        };

        if (_loadedStore is null || !string.Equals(_loadedAasxPath, TxtAasxPath.Text, StringComparison.OrdinalIgnoreCase))
        {
            var result = service.LoadAndValidate(TxtAasxPath.Text, out var store);
            if (!result.Success)
            {
                AppendLog($"❌ {result.ErrorMessage}");
                return;
            }
            if (result.Issues.Count > 0)
            {
                var preview = string.Join("\n", result.Issues.Take(10).Select(i => $"[{i.Rule}] {i.Message}"));
                var suffix = result.Issues.Count > 10 ? $"\n... (총 {result.Issues.Count}건)" : "";
                var ans = MessageBox.Show(
                    this,
                    $"v10 검증 위반 {result.Issues.Count} 건:\n\n{preview}{suffix}\n\n그래도 진행하시겠습니까?",
                    "v10 검증 경고",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (ans != MessageBoxResult.Yes)
                {
                    AppendLog("중단됨.");
                    return;
                }
            }
            _loadedStore = store;
            _loadedAasxPath = TxtAasxPath.Text;
        }

        _cts = new CancellationTokenSource();
        BtnStart.IsEnabled = false;
        BtnStop.IsEnabled = true;
        BtnValidate.IsEnabled = false;
        LblStatus.Text = "실행 중";

        try
        {
            await Task.Run(() => service.RunAsync(_loadedStore, _plc, _cts.Token));
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            BtnStart.IsEnabled = true;
            BtnStop.IsEnabled = false;
            BtnValidate.IsEnabled = true;
            LblStatus.Text = "중지됨";
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private bool TryCommitPlcInputs(out string error)
    {
        error = "";
        var isLs = CmbPlcType.SelectedIndex == 0;
        _plc.PlcType = isLs ? "LS" : "Mitsubishi";

        if (!int.TryParse(TxtPort.Text, out var port) || port <= 0 || port > 65535)
        {
            error = "Port 가 올바르지 않습니다.";
            return false;
        }

        if (isLs)
        {
            _plc.LS.IpAddress = TxtIp.Text.Trim();
            _plc.LS.Port = port;
            _plc.LS.PlcModel = (CmbPlcModel.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "XGI";
        }
        else
        {
            _plc.Mitsubishi.IpAddress = TxtIp.Text.Trim();
            _plc.Mitsubishi.Port = port;
            _plc.Mitsubishi.Protocol = (CmbMxProtocol.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "UDP";
        }
        return true;
    }

    private void AppendLog(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        _logQueue.Enqueue($"[{stamp}] {line}\n");
    }

    private void FlushLogQueue()
    {
        if (_logQueue.IsEmpty) return;

        var batch = new StringBuilder();
        int batched = 0;
        while (_logQueue.TryDequeue(out var entry))
        {
            batch.Append(entry);
            batched++;
        }
        if (batched == 0) return;

        TxtLog.AppendText(batch.ToString());
        _logLineCount += batched;

        if (_logLineCount > LogMaxLines)
        {
            var text = TxtLog.Text;
            int trimLines = _logLineCount - (LogMaxLines - LogTrimChunk);
            int idx = 0;
            for (int i = 0; i < trimLines && idx < text.Length; i++)
            {
                int nl = text.IndexOf('\n', idx);
                if (nl < 0) { idx = text.Length; break; }
                idx = nl + 1;
            }
            if (idx > 0)
            {
                TxtLog.Text = text.Substring(idx);
                _logLineCount -= trimLines;
            }
        }

        TxtLog.CaretIndex = TxtLog.Text.Length;
        TxtLog.ScrollToEnd();
    }
}
