using System.IO;
using System.Windows;
using System.Windows.Controls;
using Ds2.Core.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;

namespace DSPilot.TestConsole;

public partial class MainWindow : Window
{
    private readonly PlcConnectionSettings _plc;
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
        ApplyPlcTypeToInputs();
        CmbPlcModel.SelectedIndex = _plc.LS.PlcModel switch { "XGK" => 1, "XGT" => 2, _ => 0 };
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

    private void OnClearLog(object sender, RoutedEventArgs e) => TxtLog.Clear();

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
            CycleChanged = c => Dispatcher.Invoke(() => LblCycle.Text = $"Cycle: {c}"),
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
        }
        return true;
    }

    private void AppendLog(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(line));
            return;
        }
        var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        TxtLog.AppendText($"[{stamp}] {line}\n");
        TxtLog.ScrollToEnd();
    }
}
