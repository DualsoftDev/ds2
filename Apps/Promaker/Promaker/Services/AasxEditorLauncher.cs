using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using log4net;

namespace Promaker.Services;

/// <summary>
/// AASX 에디터(AasxEditor.Desktop) 실행 헬퍼 — 유틸 메뉴 "AASX 에디터 열기" 진입점.
/// exe 해석 우선순위:
///   1. dev 빌드 산출물(<c>Apps/AasxEditor/AasxEditor.Desktop/bin/.../AasxEditor.Desktop.exe</c>) —
///      Promaker 를 솔루션 빌드/디버그로 실행 중일 때 같은 repo 의 빌드본을 우선 사용.
///   2. 설치본 <c>{ProgramFiles}\Dualsoft\AasxEditor\AasxEditor.Desktop.exe</c>
///      (AasxEditor.iss 의 <c>DefaultDirName={autopf}\Dualsoft\AasxEditor</c> + exe명 AasxEditor.Desktop.exe).
///   3. <c>{ProgramFilesX86}\Dualsoft\AasxEditor\AasxEditor.Desktop.exe</c> (32-bit/잔여 설치).
/// 어느 경로도 없으면 미설치로 판단 — 안내 후 종료.
/// </summary>
public static class AasxEditorLauncher
{
    private static readonly ILog Log = LogManager.GetLogger("AasxEditorLauncher");

    private const string ExeName = "AasxEditor.Desktop.exe";

    /// <summary>실행할 AasxEditor.Desktop.exe 의 전체 경로 — 후보 중 처음 존재하는 것. 없으면 null.</summary>
    public static string? ResolveExePath()
    {
        foreach (var candidate in EnumerateExeCandidates())
            if (File.Exists(candidate))
                return candidate;
        return null;
    }

    /// <summary>AASX 에디터를 실행. 미설치(후보 경로 모두 부재)면 안내 메시지 표시 후 종료.</summary>
    public static void Open()
    {
        var exe = ResolveExePath();
        if (exe is null)
        {
            Log.Info("AASX 에디터 미설치 — 실행 건너뜀");
            MessageBox.Show(
                "AASX 에디터가 설치되어 있지 않습니다.\n\n"
              + "AASX 에디터 인스톨러를 실행해 설치한 뒤 다시 시도해 주세요.",
                "AASX 에디터 미설치",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            // WorkingDirectory 를 exe 폴더로 고정 — wwwroot(_content) 등 상대 자산을 안정적으로 로드.
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            });
            Log.Info($"AASX 에디터 실행: {exe}");
        }
        catch (Exception ex)
        {
            Log.Warn($"AASX 에디터 실행 실패 ({exe}): {ex.Message}");
            MessageBox.Show(
                $"AASX 에디터 실행에 실패했습니다.\n\n{ex.Message}",
                "AASX 에디터 실행 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>exe 후보 경로 열거 — dev 빌드본 → ProgramFiles → ProgramFilesX86 순.</summary>
    private static IEnumerable<string> EnumerateExeCandidates()
    {
        var dev = ResolveDevBuildExe();
        if (dev is not null)
            yield return dev;

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf))
            yield return Path.Combine(pf, "Dualsoft", "AasxEditor", ExeName);
        if (!string.IsNullOrEmpty(pfx86) && !string.Equals(pf, pfx86, StringComparison.OrdinalIgnoreCase))
            yield return Path.Combine(pfx86, "Dualsoft", "AasxEditor", ExeName);
    }

    /// <summary>repo 내 dev 빌드 산출물 경로 추정. Promaker 실행 위치에서 상위로 올라가며
    /// <c>Apps</c> 폴더를 찾고, 그 아래 AasxEditor.Desktop 빌드 출력에서 exe 를 탐색.
    /// 설치본(ProgramFiles)에서 실행 중이면 Apps 폴더가 없어 null 반환 → 설치본 경로로 폴백.</summary>
    private static string? ResolveDevBuildExe()
    {
        var appsDir = FindAncestorAppsDir(AppContext.BaseDirectory);
        if (appsDir is null)
            return null;

        var buildRoot = Path.Combine(appsDir, "AasxEditor", "AasxEditor.Desktop", "bin");
        if (!Directory.Exists(buildRoot))
            return null;

        // bin/<Config>/<tfm>/[rid/[publish/]]AasxEditor.Desktop.exe — 가장 최근 수정본 채택.
        // EnumerateFiles(AllDirectories) 는 재귀 열거 중 접근 거부·긴 경로 등 외부 환경 예외를
        // 던질 수 있다. 이 경로는 Open() 의 try 블록 밖(ResolveExePath)에서 호출되므로, 미보호 시
        // 전역 DispatcherUnhandledException 으로 앱이 종료된다 — dev 탐색 실패는 설치본 폴백으로 흡수.
        string? newest = null;
        DateTime newestTime = DateTime.MinValue;
        try
        {
            foreach (var path in Directory.EnumerateFiles(buildRoot, ExeName, SearchOption.AllDirectories))
            {
                var t = File.GetLastWriteTimeUtc(path);
                if (t > newestTime) { newestTime = t; newest = path; }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"AASX 에디터 dev 빌드 탐색 중 파일 열거 실패 — 설치본으로 폴백: {ex.Message}");
            return null;
        }
        return newest;
    }

    /// <summary>start 디렉터리에서 위로 올라가며 자식으로 <c>AasxEditor</c> 를 가진 <c>Apps</c> 폴더를 찾음.</summary>
    private static string? FindAncestorAppsDir(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (string.Equals(dir.Name, "Apps", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(Path.Combine(dir.FullName, "AasxEditor")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
