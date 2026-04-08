using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Promaker.Services;

/// <summary>
/// 최근에 열었던 파일 목록 관리
/// </summary>
public static class RecentFilesManager
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "RecentFiles", "recent_files.txt");

    private const int MaxRecentFiles = 10;

    /// <summary>
    /// 저장된 최근 파일 목록 로드
    /// </summary>
    public static List<string> LoadRecentFiles()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new List<string>();

            return File.ReadAllLines(SettingsPath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .Where(File.Exists) // 존재하는 파일만
                .Take(MaxRecentFiles)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 파일 경로를 최근 목록에 추가하고 저장
    /// </summary>
    public static void AddRecentFile(string filePath)
    {
        try
        {
            var recentFiles = LoadRecentFiles();

            // 이미 존재하면 제거 (맨 앞으로 이동하기 위해)
            recentFiles.Remove(filePath);

            // 맨 앞에 추가
            recentFiles.Insert(0, filePath);

            // 최대 개수 제한
            if (recentFiles.Count > MaxRecentFiles)
                recentFiles = recentFiles.Take(MaxRecentFiles).ToList();

            SaveRecentFiles(recentFiles);
        }
        catch
        {
            // Ignore failures
        }
    }

    /// <summary>
    /// 최근 파일 목록 저장
    /// </summary>
    private static void SaveRecentFiles(List<string> files)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllLines(SettingsPath, files);
        }
        catch
        {
            // Ignore persistence failures
        }
    }

    /// <summary>
    /// 최근 파일 목록 초기화
    /// </summary>
    public static void ClearRecentFiles()
    {
        try
        {
            if (File.Exists(SettingsPath))
                File.Delete(SettingsPath);
        }
        catch
        {
            // Ignore failures
        }
    }
}
