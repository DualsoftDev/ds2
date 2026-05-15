using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core.Store;
using Ds2.Editor;
using Microsoft.Win32;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class PropertyPanelState
{
    public ObservableCollection<UserTagPanelItem> UserTags { get; } = [];
    public string UserTagsHeader => $"UserTags [{UserTags.Count}]";

    private static readonly string[] CsvHeaderColumns = ["이름", "로그 레벨", "태그 주소", "값 타입"];

    private void RefreshUserTagsPanel(Guid systemId)
    {
        if (!_host.TryRef(() => Store.GetUserTagsForSystem(systemId), out var items))
            return;
        ReplaceAll(UserTags, items);
        OnPropertyChanged(nameof(UserTagsHeader));
    }

    [RelayCommand]
    private void AddSystemUserTag()
    {
        if (!GuardSimulationSemanticEdit("사용자 태그 추가")) return;
        if (!TryGetSelectedNode(EntityKind.System, out var systemNode)) return;

        var existingNames = UserTags.Select(t => t.Name);
        var dialog = new UserTagEditDialog(existingNames);
        if (!ShowOwnedDialog(dialog)) return;

        if (!_host.TryAction(
                () => Store.AddUserTag(systemNode.Id, dialog.TagName, dialog.LogLevel, dialog.TagAddress, dialog.ValueType)))
            return;

        RefreshUserTagsPanel(systemNode.Id);
        _host.SetStatusText($"사용자 태그 '{dialog.TagName}' 추가됨.");
    }

    [RelayCommand]
    private void EditSystemUserTag(UserTagPanelItem? item)
    {
        if (item is null) return;
        if (!GuardSimulationSemanticEdit("사용자 태그 편집")) return;
        if (!TryGetSelectedNode(EntityKind.System, out var systemNode)) return;

        // 중복 검증: 자신을 제외한 다른 태그 이름들
        var existingNames = UserTags
            .Where(t => t.Index != item.Index)
            .Select(t => t.Name);
        var dialog = new UserTagEditDialog(existingNames, item);
        if (!ShowOwnedDialog(dialog)) return;

        if (!_host.TryAction(
                () => Store.UpdateUserTag(systemNode.Id, item.Index, dialog.TagName, dialog.LogLevel, dialog.TagAddress, dialog.ValueType)))
            return;

        RefreshUserTagsPanel(systemNode.Id);
        _host.SetStatusText($"사용자 태그 '{dialog.TagName}' 수정됨.");
    }

    [RelayCommand]
    private void DeleteSystemUserTag(UserTagPanelItem? item)
    {
        if (item is null) return;
        if (!GuardSimulationSemanticEdit("사용자 태그 삭제")) return;
        if (!TryGetSelectedNode(EntityKind.System, out var systemNode)) return;

        if (!_host.TryAction(() => Store.RemoveUserTag(systemNode.Id, item.Index)))
            return;

        RefreshUserTagsPanel(systemNode.Id);
        _host.SetStatusText($"사용자 태그 '{item.Name}' 삭제됨.");
    }

    [RelayCommand]
    private void ExportUserTagsCsv()
    {
        if (!TryGetSelectedNode(EntityKind.System, out var systemNode)) return;
        if (UserTags.Count == 0)
        {
            DialogHelpers.Warn("내보낼 사용자 태그가 없습니다.");
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "사용자 태그 CSV 내보내기",
            FileName = $"{systemNode.Name}_UserTags.csv",
            Filter = "CSV 파일 (*.csv)|*.csv|모든 파일 (*.*)|*.*",
            DefaultExt = "csv"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", CsvHeaderColumns));
            foreach (var t in UserTags)
                sb.AppendLine($"{CsvEscape(t.Name)},{CsvEscape(t.LogLevel)},{CsvEscape(t.TagAddress)},{CsvEscape(t.ValueType)}");

            // UTF-8 BOM (Excel 한글 호환)
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            _host.SetStatusText($"CSV 내보내기 완료: {UserTags.Count}건");
        }
        catch (Exception ex)
        {
            DialogHelpers.Warn($"CSV 내보내기 실패: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ImportUserTagsCsvAppend() => ImportUserTagsCsvCore(replace: false);

    [RelayCommand]
    private void ImportUserTagsCsvReplace() => ImportUserTagsCsvCore(replace: true);

    private void ImportUserTagsCsvCore(bool replace)
    {
        if (!GuardSimulationSemanticEdit(replace ? "사용자 태그 CSV 교체" : "사용자 태그 CSV 추가")) return;
        if (!TryGetSelectedNode(EntityKind.System, out var systemNode)) return;

        if (replace && UserTags.Count > 0
            && !DialogHelpers.Confirm(null, $"기존 사용자 태그 {UserTags.Count}건을 모두 삭제하고 CSV 내용으로 덮어씁니다. 계속하시겠습니까?", "CSV 교체 확인"))
            return;

        var dlg = new OpenFileDialog
        {
            Title = "사용자 태그 CSV 가져오기",
            Filter = "CSV 파일 (*.csv)|*.csv|모든 파일 (*.*)|*.*",
            DefaultExt = "csv"
        };
        if (dlg.ShowDialog() != true) return;

        List<(string Name, string Level, string Addr, string Type)> rows;
        try
        {
            rows = ParseCsv(dlg.FileName);
        }
        catch (Exception ex)
        {
            DialogHelpers.Warn($"CSV 파싱 실패: {ex.Message}");
            return;
        }

        if (rows.Count == 0)
        {
            DialogHelpers.Warn("CSV에 유효한 데이터가 없습니다.");
            return;
        }

        // 중복 검증 (Name 기준, 같은 System 안)
        var existingNamesLower = replace
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(UserTags.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        var filteredRows = new List<(string, string, string, string)>();
        var skipped = 0;
        foreach (var r in rows)
        {
            if (existingNamesLower.Contains(r.Name)) { skipped++; continue; }
            existingNamesLower.Add(r.Name);
            filteredRows.Add((r.Name, r.Level, r.Addr, r.Type));
        }

        if (filteredRows.Count == 0)
        {
            DialogHelpers.Warn(skipped > 0
                ? $"가져올 신규 태그가 없습니다 (중복 {skipped}건 건너뜀)."
                : "CSV에 유효한 데이터가 없습니다.");
            return;
        }

        var systemId = systemNode.Id;
        int applied;
        if (replace)
        {
            if (!_host.TryFunc(() => Store.ReplaceUserTags(systemId, filteredRows), out applied, 0))
                return;
        }
        else
        {
            if (!_host.TryFunc(() => Store.AddUserTagsBatch(systemId, filteredRows), out applied, 0))
                return;
        }

        RefreshUserTagsPanel(systemId);
        var verb = replace ? "교체" : "추가";
        var msg = skipped > 0
            ? $"CSV {verb} 완료: {applied}건 ({skipped}건 중복 건너뜀)"
            : $"CSV {verb} 완료: {applied}건";
        _host.SetStatusText(msg);
    }

    private static List<(string Name, string Level, string Addr, string Type)> ParseCsv(string path)
    {
        var result = new List<(string, string, string, string)>();
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return result;

        var startIdx = 0;
        if (lines[0].Contains("이름") || lines[0].StartsWith("Name", StringComparison.OrdinalIgnoreCase))
            startIdx = 1;

        for (var i = startIdx; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = CsvParseLine(line);
            if (parts.Count < 4) continue;

            var name = parts[0].Trim();
            var level = parts[1].Trim();
            var addr = parts[2].Trim();
            var vt = parts[3].Trim();

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(addr)) continue;
            if (string.IsNullOrWhiteSpace(level)) level = "Info";
            if (string.IsNullOrWhiteSpace(vt)) vt = "Bit";

            result.Add((name, level, addr, vt));
        }
        return result;
    }

    private static List<string> CsvParseLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    { current.Append('"'); i++; }
                    else
                        inQuotes = false;
                }
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { result.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result;
    }

    private static string CsvEscape(string value)
        => value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
