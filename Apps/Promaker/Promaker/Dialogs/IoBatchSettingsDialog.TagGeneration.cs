using System;
using System.Linq;
using System.Windows;
using Ds2.Store;
using Ds2.Store.DsQuery;

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
            setSymbol(row, Format.expandTagPattern(pattern, row.Flow, row.Device, row.Api));

            var alloc = Format.allocatePlcAddress(addressPrefix, currentWord, currentBit, getDataType(row));
            setAddress(row, alloc.Address);
            currentWord = alloc.NextWord;
            currentBit = alloc.NextBit;
        }

        DialogHelpers.ShowThemedMessageBox(
            $"{selectedRows.Count}개 행에 {direction} 태그가 생성되었습니다.",
            "태그 자동 생성",
            MessageBoxButton.OK,
            "✓");
    }
}
