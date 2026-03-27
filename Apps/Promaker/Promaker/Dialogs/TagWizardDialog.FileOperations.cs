using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class TagWizardDialog
{
    /// <summary>
    /// 템플릿 파일 목록 로드
    /// </summary>
    private void LoadTemplateFileList()
    {
        try
        {
            TemplateFilesListBox.Items.Clear();

            var templateDir = TemplateManager.TemplatesFolderPath;
            if (!Directory.Exists(templateDir))
            {
                TemplateStatusText.Text = "템플릿 폴더가 존재하지 않습니다.";
                return;
            }

            // 모든 .txt와 .cfg 파일 가져오기
            var files = Directory.GetFiles(templateDir, "*.*")
                .Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                           f.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OrderBy(f => f)
                .ToList();

            if (files.Count == 0)
            {
                TemplateStatusText.Text = "템플릿 파일이 없습니다.";
                return;
            }

            foreach (var file in files)
            {
                TemplateFilesListBox.Items.Add(file);
            }

            // address_config.txt를 기본 선택
            var defaultFile = files.FirstOrDefault(f => f?.Equals("address_config.txt", StringComparison.OrdinalIgnoreCase) == true);
            if (defaultFile != null)
            {
                TemplateFilesListBox.SelectedItem = defaultFile;
            }
            else if (files.Count > 0)
            {
                TemplateFilesListBox.SelectedIndex = 0;
            }

            TemplateStatusText.Text = $"{files.Count}개의 템플릿 파일이 발견되었습니다.";
        }
        catch (Exception ex)
        {
            TemplateStatusText.Text = $"파일 목록 로드 실패: {ex.Message}";
        }
    }

    /// <summary>
    /// 파일 선택 시 내용 로드
    /// </summary>
    private void TemplateFilesListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (TemplateFilesListBox.SelectedItem is string fileName)
        {
            LoadTemplateFile(fileName);
        }
    }

    /// <summary>
    /// 템플릿 파일 로드
    /// </summary>
    private void LoadTemplateFile(string fileName)
    {
        try
        {
            var filePath = Path.Combine(TemplateManager.TemplatesFolderPath, fileName);

            if (!File.Exists(filePath))
            {
                TemplateStatusText.Text = $"파일을 찾을 수 없습니다: {fileName}";
                TemplateEditBox.Text = "";
                TemplateEditBox.IsEnabled = false;
                return;
            }

            _currentTemplateFile = filePath;
            CurrentFileNameText.Text = fileName;
            TemplateEditBox.Text = File.ReadAllText(filePath, Encoding.UTF8);
            TemplateEditBox.IsEnabled = true;

            var fileInfo = new FileInfo(filePath);
            var sizeKb = fileInfo.Length / 1024.0;
            TemplateStatusText.Text = $"✓ 로드 완료 | 크기: {sizeKb:F1} KB | 마지막 수정: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm}";
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"파일 로드 실패:\n\n{ex.Message}",
                "오류",
                MessageBoxButton.OK,
                "✖");

            TemplateStatusText.Text = $"로드 실패: {ex.Message}";
            TemplateEditBox.IsEnabled = false;
        }
    }

    /// <summary>
    /// 템플릿 저장 버튼 클릭
    /// </summary>
    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentTemplateFile))
        {
            TemplateStatusText.Text = "저장할 파일을 선택하세요.";
            return;
        }

        try
        {
            File.WriteAllText(_currentTemplateFile, TemplateEditBox.Text, Encoding.UTF8);

            var fileInfo = new FileInfo(_currentTemplateFile);
            var sizeKb = fileInfo.Length / 1024.0;
            TemplateStatusText.Text = $"✓ 저장 완료 | 크기: {sizeKb:F1} KB | {DateTime.Now:HH:mm:ss}";

            DialogHelpers.ShowThemedMessageBox(
                $"'{Path.GetFileName(_currentTemplateFile)}' 파일이 저장되었습니다.",
                "저장 완료",
                MessageBoxButton.OK,
                "ℹ");
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"템플릿 저장 실패:\n\n{ex.Message}",
                "오류",
                MessageBoxButton.OK,
                "✖");

            TemplateStatusText.Text = $"저장 실패: {ex.Message}";
        }
    }
}
