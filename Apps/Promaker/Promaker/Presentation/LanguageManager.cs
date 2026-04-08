using System;
using System.Globalization;
using System.IO;
using System.Windows;

namespace Promaker.Presentation;

/// <summary>
/// 애플리케이션 언어 (Korean/English)
/// Application Language (Korean/English)
/// </summary>
public enum AppLanguage
{
    Korean,
    English
}

/// <summary>
/// 다국어 지원 관리자
/// Manages Korean/English localization for the entire application.
///
/// 주요 기능:
/// - 언어 전환 (Korean ↔ English)
/// - 언어 설정 저장 및 로드 (%AppData%/Promaker/language.txt)
/// - CultureInfo 설정 (ko-KR / en-US)
/// - Strings.Culture 자동 설정
///
/// 사용법:
///   LanguageManager.ApplySavedLanguage();        // 저장된 언어 적용 (앱 시작 시)
///   LanguageManager.ToggleLanguage();            // 언어 전환 (Korean ↔ English)
///   LanguageManager.ApplyLanguage(AppLanguage.English);  // 특정 언어 설정
///
/// TODO: 향후 작업
/// - Phase 1-5 완료 후 MainToolbar에서 언어 버튼 Visibility="Collapsed" 제거
/// - LOCALIZATION_GUIDE.md 및 LOCALIZATION_TODO.md 참고
/// </summary>
public static class LanguageManager
{
    /// <summary>
    /// 언어 설정 저장 경로: %AppData%/Dualsoft/Promaker/Settings/language.txt
    /// </summary>
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Dualsoft", "Promaker", "Settings", "language.txt");

    /// <summary>
    /// 현재 언어 (기본값: Korean)
    /// Current language (default: Korean)
    /// </summary>
    public static AppLanguage CurrentLanguage { get; private set; } = AppLanguage.Korean;

    /// <summary>
    /// 앱 시작 시 저장된 언어를 로드하여 적용
    /// Loads and applies saved language on app startup
    /// </summary>
    public static void ApplySavedLanguage()
    {
        ApplyLanguage(LoadSavedLanguage(), persist: false);
    }

    /// <summary>
    /// 언어 전환 (Korean ↔ English)
    /// Toggles between Korean and English
    /// </summary>
    public static void ToggleLanguage()
    {
        ApplyLanguage(CurrentLanguage == AppLanguage.Korean ? AppLanguage.English : AppLanguage.Korean);
    }

    /// <summary>
    /// 특정 언어를 적용하고 저장
    /// Applies and persists the specified language
    /// </summary>
    /// <param name="language">적용할 언어 (Language to apply)</param>
    /// <param name="persist">파일에 저장 여부 (Whether to save to file)</param>
    public static void ApplyLanguage(AppLanguage language, bool persist = true)
    {
        CurrentLanguage = language;

        // CultureInfo 설정 (Korean: ko-KR, English: en-US)
        var culture = language == AppLanguage.Korean
            ? new CultureInfo("ko-KR")
            : new CultureInfo("en-US");

        // Strings.resx 언어 설정 (리소스 매니저에 적용)
        Resources.Strings.Culture = culture;

        // 현재 스레드의 UI Culture 설정
        CultureInfo.CurrentUICulture = culture;

        // 언어 설정 파일에 저장
        if (persist)
        {
            SaveLanguage(language);
        }
    }

    /// <summary>
    /// 저장된 언어 설정 로드 (language.txt)
    /// Loads saved language setting from file
    /// </summary>
    /// <returns>저장된 언어 또는 기본값(Korean)</returns>
    private static AppLanguage LoadSavedLanguage()
        => AppSettingStore.LoadEnumOrDefault(SettingsPath, AppLanguage.Korean);

    /// <summary>
    /// 언어 설정을 파일에 저장
    /// Saves language setting to file
    /// </summary>
    /// <param name="language">저장할 언어</param>
    private static void SaveLanguage(AppLanguage language)
        => AppSettingStore.SaveEnum(SettingsPath, language);
}
