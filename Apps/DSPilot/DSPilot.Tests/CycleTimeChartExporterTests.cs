// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using System.IO.Compression;
using System.Text;
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// CycleTimeChartExporter.BuildCycleAnalysisExcel 스모크 테스트.
/// 컨트롤러가 받는 화면 모델(CycleExcelModel)을 그대로 넘겨 ClosedXML 렌더가 예외 없이
/// 유효한 .xlsx(시트 2개: "간트차트" + "데이터")를 만드는지 검증한다 — bar/line, 사이클 유/무.
/// (서버 DB 없이 순수 렌더 경로만 검증.)
/// </summary>
public class CycleTimeChartExporterTests
{
    private const string Off = "+09:00";   // 서버 "o" 출력처럼 offset 포함
    private static string Iso(int sec, int ms = 0) => $"2026-06-02T10:00:{sec:00}.{ms:000}{Off}";
    private static CycleExcelInterval Iv(int s0, int s1) => new(Iso(s0), Iso(s1));

    private static CycleExcelModel MakeModel(string viewMode, bool withCycles)
    {
        var lanes = new List<CycleExcelLane>
        {
            new("A", "프레스", "라인1", 0, "I.A", "Q.A",
                new() { Iv(1, 3) }, new() { Iv(1, 2) }, new() { Iv(2, 3) }),
            new("B", "이송", "라인1", 1, "I.B", "Q.B",
                new() { Iv(5, 8) }, new() { Iv(5, 7) }, new() { Iv(7, 8) }),
            new("C", "용접", "라인2", 2, "I.C", "Q.C",
                new() { Iv(10, 12) }, new() { Iv(10, 11) }, new() { Iv(11, 12) }),
            // 데이터 없는 빈 lane 도 표시 대상
            new("D", "검사", "라인2", 3, "I.D", "Q.D",
                new(), new(), new()),
        };

        return new CycleExcelModel(
            FlowName: "테스트Flow",
            ChartStart: Iso(0),
            ChartEnd: Iso(30),
            ViewMode: viewMode,
            HeadCallId: "A",
            TailCallId: "C",
            HeadName: "프레스",
            TailName: "용접",
            AvgCycleMs: 14000,
            AvgActiveMs: 12000,
            Lanes: lanes,
            CycleBoundaries: withCycles ? new List<string> { Iso(1), Iso(15) } : new List<string>(),
            TailEdges: withCycles ? new List<string> { Iso(12) } : new List<string>(),
            TopGaps: new List<CycleExcelGap> { new("B", 7000, 8000, 15000) },
            ShowMaxGap: true,
            SelectedGapIndex: 0);
    }

    private static string ReadZipEntry(byte[] xlsx, string entryPath)
    {
        using var ms = new MemoryStream(xlsx);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry(entryPath);
        if (entry is null) return "";
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("bar", true)]
    [InlineData("line", true)]
    [InlineData("line", false)]   // 사이클 경계 없음 → 리본/band 생략 분기
    [InlineData("bar", false)]
    public void BuildCycleAnalysisExcel_produces_valid_two_sheet_workbook(string viewMode, bool withCycles)
    {
        var bytes = CycleTimeChartExporter.BuildCycleAnalysisExcel(MakeModel(viewMode, withCycles));

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 1000, $"xlsx too small: {bytes.Length} bytes");

        // 유효한 OOXML(zip) + 워크북에 두 시트 존재
        var workbookXml = ReadZipEntry(bytes, "xl/workbook.xml");
        Assert.Contains("간트차트", workbookXml);
        Assert.Contains("데이터", workbookXml);

        // 두 워크시트 파트가 실제로 기록됨
        Assert.NotEqual("", ReadZipEntry(bytes, "xl/worksheets/sheet1.xml"));
        Assert.NotEqual("", ReadZipEntry(bytes, "xl/worksheets/sheet2.xml"));
    }

    [Fact]
    public void BuildCycleAnalysisExcel_handles_empty_lanes_without_throwing()
    {
        var model = MakeModel("line", true) with { Lanes = new List<CycleExcelLane>() };
        var bytes = CycleTimeChartExporter.BuildCycleAnalysisExcel(model);
        Assert.True(bytes.Length > 1000);
        Assert.Contains("간트차트", ReadZipEntry(bytes, "xl/workbook.xml"));
    }
}
