using System;
using System.Linq;
using System.Windows;

namespace Promaker.Dialogs;

public partial class IoBatchSettingsDialog
{
    private void GenerateOutTags_Click(object sender, RoutedEventArgs e) =>
        GenerateTags(
            OutTagPatternBox.Text,
            OutAddressPrefixBox.Text,
            OutAddressStartBox.Text,
            "Out",
            static (row, tag) => row.OutSymbol = tag,
            static row => row.OutDataType,
            static (row, addr) => row.OutAddress = addr);

    private void GenerateInTags_Click(object sender, RoutedEventArgs e) =>
        GenerateTags(
            InTagPatternBox.Text,
            InAddressPrefixBox.Text,
            InAddressStartBox.Text,
            "In",
            static (row, tag) => row.InSymbol = tag,
            static row => row.InDataType,
            static (row, addr) => row.InAddress = addr);

    private void GenerateTags(
        string pattern,
        string addressPrefix,
        string startText,
        string direction,
        Action<IoBatchRow, string> setSymbol,
        Func<IoBatchRow, string> getDataType,
        Action<IoBatchRow, string> setAddress)
    {
        var selectedRows = _rows.Where(row => row.IsSelected).ToList();
        if (selectedRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                "먼저 하나 이상의 행을 선택하세요.", "태그 자동 생성", MessageBoxButton.OK, "ℹ");
            return;
        }

        if (!int.TryParse(startText, out int startAddr))
        {
            DialogHelpers.ShowThemedMessageBox(
                "시작 주소는 숫자여야 합니다.", "태그 자동 생성", MessageBoxButton.OK, "⚠");
            return;
        }

        int currentWord = startAddr;
        int currentBit = 0;

        foreach (var row in selectedRows)
        {
            string tag = pattern
                .Replace("$(F)", row.Flow)
                .Replace("$(D)", row.Device)
                .Replace("$(A)", row.Api);

            setSymbol(row, tag);

            if (getDataType(row).Equals("BOOL", StringComparison.OrdinalIgnoreCase))
            {
                setAddress(row, $"{addressPrefix}{currentWord}.{currentBit}");
                currentBit++;
                if (currentBit >= 16)
                {
                    currentBit = 0;
                    currentWord++;
                }
            }
            else
            {
                setAddress(row, $"{addressPrefix}{currentWord}");
                currentWord++;
                currentBit = 0;
            }
        }

        DialogHelpers.ShowThemedMessageBox(
            $"{selectedRows.Count}개 행에 {direction} 태그가 생성되었습니다.",
            "태그 자동 생성",
            MessageBoxButton.OK,
            "✓");
    }
}
