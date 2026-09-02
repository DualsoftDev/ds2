using System.Windows;
using Ds2.Core;
using Ds2.Core.Store;
using Promaker.Dialogs;

namespace Promaker.Services;

/// <summary>
/// 이름 입력 관문(대화형) — 코어 <see cref="NamePolicy"/>(SSOT)를 입력 확정 시점에 적용한다.
///   · Flow/System = 식별자('/' '\' → '-'), Work/Call = 값(" / " 부분열만 무해화).
///   · 변환이 필요하면 "입력 → 적용" 미리보기 경고를 띄우고, 사용자가 수락해야 진행한다
///     (저장 시점 변환 금지 — 화면에 보이던 이름과 저장 결과가 달라지는 조용한 배신 방지).
///   · 무대화형 경로(SystemPackage 임포트)는 코어가 자동 적용 + 요약 보고, 기존 모델은
///     열기 린트(NamePolicyLintService)가 담당 — 세 경로 모두 같은 코어 정책을 공유한다.
/// </summary>
internal static class NameInputPolicy
{
    /// <summary>코어 NamePolicy 역할 구분과 동일: Flow/System 이름만 식별자(URL·저장 키)다.</summary>
    internal static bool IsIdentifierKind(EntityKind kind) =>
        kind == EntityKind.Flow || kind == EntityKind.System;

    internal static string Sanitize(EntityKind kind, string name) =>
        IsIdentifierKind(kind)
            ? NamePolicy.SanitizeIdentifier(name)
            : NamePolicy.SanitizeValue(name);

    /// <summary>
    /// 입력 확정 관문. 변환이 불필요하면 조용히 통과(Trim 만 적용), 필요하면 미리보기 경고 후
    /// 사용자 수락 시 변환된 이름을 <paramref name="finalName"/> 으로 돌려준다.
    /// 반환 false = 사용자가 취소(호출자는 입력을 다시 받거나 중단).
    /// </summary>
    internal static bool TryConfirm(EntityKind kind, string rawName, out string finalName)
    {
        finalName = Sanitize(kind, rawName);

        // 앞뒤 공백 제거는 기존 경로들도 조용히 하던 정규화 — 경고 없이 통과.
        var trimmed = (rawName ?? string.Empty).Trim();
        if (finalName == trimmed)
            return finalName.Length > 0 || trimmed.Length == 0;

        if (finalName.Length == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                "사용할 수 없는 문자를 제거하면 이름이 비어 버립니다.\n다른 이름을 입력해 주세요.",
                "이름 정책", MessageBoxButton.OK, DialogHelpers.IconWarn);
            return false;
        }

        var ruleHint = IsIdentifierKind(kind)
            ? "'/' '\\' 는 Flow/System 이름에 사용할 수 없습니다 ('-' 로 변환)."
            : "\" / \" (공백-슬래시-공백)는 이름에 사용할 수 없습니다 (\" - \" 로 변환).";

        var result = DialogHelpers.ShowThemedMessageBox(
            "이름에 사용할 수 없는 문자가 있어 변환됩니다.\n\n" +
            $"  입력:  {trimmed}\n" +
            $"  적용:  {finalName}\n\n" +
            $"{ruleHint}\n\n이 이름으로 적용할까요?",
            "이름 정책", MessageBoxButton.YesNo, DialogHelpers.IconWarn);

        return result == MessageBoxResult.Yes;
    }
}
