using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Promaker.Services;

/// <summary>
/// 임베디드 디폴트 템플릿 문자열을 파싱해 (IW, QW, MW) 패턴 목록을 반환한다.
/// '-' 단독 라인 (또는 공백으로 구분된 다중 '-') = 빈 슬롯 마커 (api/pattern 모두 "-").
/// </summary>
public static class PresetTemplateSeeder
{
    private static readonly Regex SectionRegex = new(@"^\[(IW|QW|MW)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex EntryRegex   = new(@"^([^#:]+):\s*(.+)$",  RegexOptions.Compiled);

    public static (List<(string api, string pattern)> iw,
                   List<(string api, string pattern)> qw,
                   List<(string api, string pattern)> mw) Parse(string content)
    {
        var iw = new List<(string, string)>();
        var qw = new List<(string, string)>();
        var mw = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(content)) return (iw, qw, mw);

        string? section = null;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            var sect = SectionRegex.Match(line);
            if (sect.Success) { section = sect.Groups[1].Value.ToUpperInvariant(); continue; }
            if (section == null) continue;

            // 빈 슬롯 마커: '-' 단독 또는 공백으로 구분된 다중 '-' (각 토큰이 1 비트 예약).
            if (line.IndexOf(':') < 0)
            {
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bool allDashes = tokens.Length > 0;
                foreach (var t in tokens) if (t != "-") { allDashes = false; break; }
                if (allDashes)
                {
                    var gap = ("-", "-");
                    for (int n = 0; n < tokens.Length; n++)
                        switch (section)
                        {
                            case "IW": iw.Add(gap); break;
                            case "QW": qw.Add(gap); break;
                            case "MW": mw.Add(gap); break;
                        }
                    continue;
                }
            }

            var entry = EntryRegex.Match(line);
            if (!entry.Success) continue;
            var pair = (entry.Groups[1].Value.Trim(), entry.Groups[2].Value.Trim());
            switch (section)
            {
                case "IW": iw.Add(pair); break;
                case "QW": qw.Add(pair); break;
                case "MW": mw.Add(pair); break;
            }
        }
        return (iw, qw, mw);
    }
}
