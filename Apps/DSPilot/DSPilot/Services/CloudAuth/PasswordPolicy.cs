// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Services.CloudAuth;

/// <summary>Pi5 설치마법사와 동일한 관리자 계정 비밀번호 규칙.</summary>
public static class PasswordPolicy
{
    public static IReadOnlyList<string> GetIssues(string password)
    {
        password ??= "";
        var issues = new List<string>();
        if (password.Length < 8) issues.Add("8자 이상");
        if (!password.Any(c => c is >= 'A' and <= 'Z')) issues.Add("대문자");
        if (!password.Any(c => c is >= 'a' and <= 'z')) issues.Add("소문자");
        if (!password.Any(c => c is >= '0' and <= '9')) issues.Add("숫자");
        if (!password.Any(c => !IsAsciiLetterOrDigit(c))) issues.Add("특수문자");
        return issues;
    }

    public static bool IsValid(string password) => GetIssues(password).Count == 0;

    private static bool IsAsciiLetterOrDigit(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
