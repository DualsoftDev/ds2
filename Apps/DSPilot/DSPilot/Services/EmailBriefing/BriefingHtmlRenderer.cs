// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.Globalization;
using System.Net;
using System.Text;

namespace DSPilot.Services.EmailBriefing;

/// <summary>
/// 브리핑 데이터를 이메일 HTML 본문으로 렌더한다. 이메일 클라이언트 호환을 위해 &lt;table&gt; 레이아웃 +
/// 인라인 CSS 만 사용(외부 CSS/폰트/스크립트·flex/grid 미사용). 순수 함수라 싱글톤.
/// </summary>
public sealed class BriefingHtmlRenderer
{
    private const string Accent = "#2563eb";     // 애저 악센트
    private const string Ink = "#0f172a";
    private const string Muted = "#64748b";
    private const string Border = "#e2e8f0";
    private const string Bg = "#f1f5f9";
    private const string Ok = "#16a34a";
    private const string Warn = "#dc2626";

    /// <summary>제목 줄. 목록/알림에서 한눈에 보이도록 날짜+핵심 수치.</summary>
    public string BuildSubject(BriefingData d)
    {
        var oee = FmtPct(d.Line.Oee);
        return $"[DSPilot] 일일 브리핑 · {d.Day:yyyy-MM-dd} (생산 OEE {oee} / 이상 {d.AbnormalTotal}건)";
    }

    /// <param name="dashboardUrl">
    /// DSPilot 바로가기 버튼 주소 — 전역 외부 접속 주소(<see cref="Services.ExternalAccessService.ResolveUrl"/>,
    /// 사용자 설정 ▸ 설치 주입 폴백)의 유효값. 서버는 자기 외부 주소를 모르므로 호출측이 해석해 넘긴다. 비면 버튼 미출력.
    /// </param>
    public string BuildHtml(BriefingData d, string? dashboardUrl = null)
    {
        var sb = new StringBuilder(8192);
        sb.Append($@"<div style=""margin:0;padding:0;background:{Bg};"">
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:{Bg};padding:24px 0;"">
<tr><td align=""center"">
<table role=""presentation"" width=""640"" cellpadding=""0"" cellspacing=""0"" style=""width:640px;max-width:640px;background:#ffffff;border-radius:12px;overflow:hidden;font-family:'Malgun Gothic','Apple SD Gothic Neo',Arial,sans-serif;color:{Ink};box-shadow:0 1px 3px rgba(0,0,0,.08);"">");

        // ── 헤더 ──
        sb.Append($@"
<tr><td style=""background:{Accent};padding:22px 28px;"">
  <div style=""font-size:12px;letter-spacing:.08em;color:#dbeafe;text-transform:uppercase;"">DSPilot 일일 브리핑</div>
  <div style=""font-size:22px;font-weight:800;color:#ffffff;margin-top:4px;"">{d.Day:yyyy년 M월 d일 (ddd)} 생산·이상 요약</div>
</td></tr>");

        // ── ① 생산 요약 ──
        sb.Append(SectionTitle("① 생산 요약 (OEE)"));
        sb.Append($@"<tr><td style=""padding:0 28px;"">");
        sb.Append(KpiRow(new (string, string, string)[]
        {
            ("설비효율 OEE", FmtPct(d.Line.Oee), Accent),
            ("생산효율 TEEP", FmtPct(d.LineTeep), Accent),
            ("가동률 A", FmtPct(d.Line.Availability), Ink),
            ("성능 P", FmtPct(d.Line.Performance), Ink),
        }));
        sb.Append(KpiRow(new (string, string, string)[]
        {
            ("생산 수량", FmtCount(d.Line.TotalCount), Ink),
            ("정지 시간", FmtDuration(d.Line.DowntimeMs), Warn),
            ("고장 건수", $"{d.Line.FailureCount}건", Ink),
        }));
        sb.Append("</td></tr>");

        // Flow별 표
        if (d.Flows.Count > 0)
        {
            sb.Append($@"<tr><td style=""padding:8px 28px 4px;"">
<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""border-collapse:collapse;font-size:13px;"">
<tr style=""background:{Bg};color:{Muted};"">
  <th align=""left""  style=""padding:8px 10px;border-bottom:1px solid {Border};font-weight:600;"">설비(Flow)</th>
  <th align=""right"" style=""padding:8px 10px;border-bottom:1px solid {Border};font-weight:600;"">OEE</th>
  <th align=""right"" style=""padding:8px 10px;border-bottom:1px solid {Border};font-weight:600;"">생산 수량</th>
  <th align=""right"" style=""padding:8px 10px;border-bottom:1px solid {Border};font-weight:600;"">정지</th>
</tr>");
            foreach (var f in d.Flows)
            {
                sb.Append($@"<tr>
  <td align=""left""  style=""padding:8px 10px;border-bottom:1px solid {Border};"">{Enc(f.Name)}</td>
  <td align=""right"" style=""padding:8px 10px;border-bottom:1px solid {Border};font-weight:700;color:{Accent};"">{FmtPct(f.Oee)}</td>
  <td align=""right"" style=""padding:8px 10px;border-bottom:1px solid {Border};"">{FmtCount(f.Count)}</td>
  <td align=""right"" style=""padding:8px 10px;border-bottom:1px solid {Border};color:{Muted};"">{FmtDuration(f.DowntimeMs)}</td>
</tr>");
            }
            sb.Append("</table></td></tr>");
        }
        else
        {
            sb.Append(EmptyNote("어제 집계된 생산 데이터가 없습니다."));
        }

        // ── ② 이상 요약 ──
        sb.Append(SectionTitle("② 이상 요약"));
        sb.Append($@"<tr><td style=""padding:0 28px;"">");
        if (d.AbnormalTotal == 0)
        {
            sb.Append($@"<div style=""margin:4px 0 8px;padding:14px 16px;background:#f0fdf4;border:1px solid #bbf7d0;border-radius:10px;color:{Ok};font-weight:700;"">✔ 어제 발생한 이상·알람이 없습니다.</div>");
        }
        else
        {
            sb.Append(KpiRow(new (string, string, string)[]
            {
                ("이상 총 건수", $"{d.AbnormalTotal}건", Warn),
                ("경로이탈 감지", $"{d.AbnormalCount}건", Ink),
                ("사용자정의 알람", $"{d.UserTagCount}건", Ink),
            }));
            if (d.TopAbnormal.Count > 0)
            {
                sb.Append($@"<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""border-collapse:collapse;font-size:13px;margin-top:4px;"">
<tr style=""background:{Bg};color:{Muted};"">
  <th align=""left""  style=""padding:8px 10px;border-bottom:1px solid {Border};font-weight:600;"">항목</th>
  <th align=""left""  style=""padding:8px 10px;border-bottom:1px solid {Border};font-weight:600;"">구분</th>
  <th align=""right"" style=""padding:8px 10px;border-bottom:1px solid {Border};font-weight:600;"">건수</th>
</tr>");
                foreach (var t in d.TopAbnormal)
                {
                    sb.Append($@"<tr>
  <td align=""left""  style=""padding:8px 10px;border-bottom:1px solid {Border};"">{Enc(t.Name)}</td>
  <td align=""left""  style=""padding:8px 10px;border-bottom:1px solid {Border};color:{Muted};"">{CategoryLabel(t.Category)}</td>
  <td align=""right"" style=""padding:8px 10px;border-bottom:1px solid {Border};font-weight:700;color:{Warn};"">{t.Count}</td>
</tr>");
                }
                sb.Append("</table>");
            }
        }
        sb.Append("</td></tr>");

        // ── 대시보드 바로가기 (주소 지정 시에만) ──
        if (!string.IsNullOrWhiteSpace(dashboardUrl))
        {
            var href = Enc(dashboardUrl.Trim());
            sb.Append($@"
<tr><td align=""center"" style=""padding:22px 28px 2px;"">
  <a href=""{href}"" target=""_blank"" style=""display:inline-block;background:{Accent};color:#ffffff;text-decoration:none;font-weight:800;font-size:14px;padding:13px 36px;border-radius:10px;"">DSPilot 대시보드 열기</a>
  <div style=""font-size:11px;color:{Muted};margin-top:8px;"">{href}</div>
</td></tr>");
        }

        // ── 푸터 ──
        sb.Append($@"
<tr><td style=""padding:20px 28px 26px;"">
  <div style=""border-top:1px solid {Border};padding-top:14px;font-size:11px;color:{Muted};line-height:1.6;"">
    이 메일은 DSPilot 이 매일 자동 발송하는 브리핑입니다. 수치는 어제(00:00~24:00) 기준이며 대시보드의 OEE·이상 데이터와 동일한 계산을 사용합니다.<br/>
    수신·발송 시각·주소 변경은 DSPilot 설정 › 브리핑 메일에서 할 수 있습니다.
  </div>
</td></tr>");

        sb.Append("</table></td></tr></table></div>");
        return sb.ToString();
    }

    // ── 조각 헬퍼 ──
    private static string SectionTitle(string text) => $@"
<tr><td style=""padding:22px 28px 6px;"">
  <div style=""font-size:15px;font-weight:800;color:{Ink};border-left:4px solid {Accent};padding-left:10px;"">{Enc(text)}</div>
</td></tr>";

    // 2~4개 KPI 타일을 한 줄(테이블 행)로. 이메일 호환 위해 flex 대신 table-cell 균등 분배.
    private static string KpiRow((string Label, string Value, string Color)[] cells)
    {
        var sb = new StringBuilder();
        var w = cells.Length > 0 ? (int)Math.Round(100.0 / cells.Length) : 100; // 타일 수에 맞춰 균등 폭
        sb.Append(@"<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""border-collapse:separate;border-spacing:8px 8px;""><tr>");
        foreach (var c in cells)
        {
            sb.Append($@"<td width=""{w}%"" valign=""top"" style=""background:{Bg};border:1px solid {Border};border-radius:10px;padding:12px 12px;"">
  <div style=""font-size:11px;color:{Muted};margin-bottom:4px;"">{Enc(c.Label)}</div>
  <div style=""font-size:19px;font-weight:800;color:{c.Color};"">{c.Value}</div>
</td>");
        }
        sb.Append("</tr></table>");
        return sb.ToString();
    }

    private static string EmptyNote(string text) =>
        $@"<tr><td style=""padding:4px 28px 8px;""><div style=""padding:14px 16px;background:{Bg};border:1px dashed {Border};border-radius:10px;color:{Muted};"">{Enc(text)}</div></td></tr>";

    private static string CategoryLabel(string category) => category?.ToUpperInvariant() switch
    {
        "ABNORMAL" => "경로이탈 감지",
        "USERTAG" => "사용자정의",
        _ => Enc(category ?? "")
    };

    // ── 포맷 ──
    private static string FmtPct(double? v) =>
        v is double x ? (x * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";

    private static string FmtCount(int? v) =>
        v is int x ? x.ToString("N0", CultureInfo.InvariantCulture) : "—";

    private static string FmtDuration(double ms)
    {
        if (ms <= 0) return "0분";
        var t = TimeSpan.FromMilliseconds(ms);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}시간 {t.Minutes}분";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}분";
        return $"{(int)t.TotalSeconds}초";
    }

    private static string Enc(string s) => WebUtility.HtmlEncode(s ?? string.Empty);
}
