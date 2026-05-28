module Ds2.SymbolImport.Tests.CsvParserTests

open System
open System.IO
open System.Text
open Ds2.SymbolImport
open Xunit

[<Fact>]
let ``Mitsubishi CSV 3 컬럼 (Device/Label/Comment) parse — tab 구분자`` () =
    let csv = "\"Title row\"\n\"Device Name\"\t\"Label\"\t\"Comment\"\n\"X10\"\t\"Cyl1_ADV_LMT\"\t\"실린더1 전진 리밋\"\n\"Y20\"\t\"Cyl1_ADV\"\t\"실린더1 전진 출력\""
    let result = CsvParser.parseMitsubishi csv
    Assert.Equal(2, result.Entries.Length)
    Assert.Equal("X10", result.Entries.[0].Address)
    Assert.Equal("Cyl1_ADV_LMT", result.Entries.[0].Name)
    Assert.Equal(SymbolDirection.Input, result.Entries.[0].Direction)
    Assert.Equal(SymbolDirection.Output, result.Entries.[1].Direction)

[<Fact>]
let ``Mitsubishi CSV 2 컬럼 (Device/Comment) — Comment 가 Name 역할`` () =
    // 실 dump (LSEV_CCS) 패턴: Title + "Device Name"\t"Comment" 헤더 + 데이터.
    let csv = "\"Title\"\n\"Device Name\"\t\"Comment\"\n\"X0\"\t\"QD77 Ready\"\n\"Y1\"\t\"펌프 출력\""
    let result = CsvParser.parseMitsubishi csv
    Assert.Equal(2, result.Entries.Length)
    Assert.Equal("X0", result.Entries.[0].Address)
    Assert.Equal("QD77 Ready", result.Entries.[0].Name)   // Label 없으면 Comment 가 Name
    Assert.Equal("QD77 Ready", result.Entries.[0].Comment)

[<Fact>]
let ``Mitsubishi CSV 컬럼 부족 — warning + skip`` () =
    // 헤더 다음 데이터 라인 중 단일 컬럼 (tab 없음) → warning + skip.
    let csv = "\"Title\"\n\"Device Name\"\t\"Comment\"\nX10\n\"Y20\"\t\"Cyl_ADV\""
    let result = CsvParser.parseMitsubishi csv
    Assert.Equal(1, result.Entries.Length)
    Assert.NotEmpty(result.Warnings)

[<Fact>]
let ``parseFile reads shared-open Mitsubishi CSV`` () =
    let csv = "\"Title\"\n\"Device Name\"\t\"Label\"\t\"Comment\"\n\"X10\"\t\"Cyl1_ADV_LMT\"\t\"실린더1 전진 리밋\"\n\"Y20\"\t\"Cyl1_ADV\"\t\"실린더1 전진 출력\""
    let path = Path.Combine(Path.GetTempPath(), $"ds2-symbol-shared-{Guid.NewGuid():N}.csv")
    try
        File.WriteAllText(path, csv, Encoding.UTF8)
        use _handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)

        let result = CsvParser.parseFile Mitsubishi path

        Assert.Equal(2, result.Entries.Length)
        Assert.Equal("Cyl1_ADV_LMT", result.Entries.[0].Name)
    finally
        if File.Exists(path) then File.Delete(path)

[<Fact>]
let ``XG5000 XML Symbol element (attribute) parse`` () =
    let xml = """<?xml version="1.0"?>
<SymbolTable>
  <Symbol Var="%IX0.0.0" Name="Feeder_LS" Comment="피더 리밋"/>
  <Symbol Var="%QX1.0.0" Name="Feeder_SOL" Comment="피더 솔레노이드"/>
</SymbolTable>"""
    let result = CsvParser.parseXG5000Xml xml
    Assert.Equal(2, result.Entries.Length)
    Assert.Equal(SymbolDirection.Input, result.Entries.[0].Direction)
    Assert.Equal(SymbolDirection.Output, result.Entries.[1].Direction)
    Assert.Equal("Feeder_LS", result.Entries.[0].Name)

[<Fact>]
let ``XG5000 XML Symbol element (child element) parse`` () =
    let xml = """<?xml version="1.0"?>
<SymbolTable>
  <Symbol><Var>%MW100</Var><Name>Cycle_Count</Name><Comment>사이클 카운터</Comment></Symbol>
</SymbolTable>"""
    let result = CsvParser.parseXG5000Xml xml
    Assert.Equal(1, result.Entries.Length)
    Assert.Equal(SymbolDirection.Memory, result.Entries.[0].Direction)

[<Fact>]
let ``XGK CSV export parse — Type Scope Variable Address DataType Comment`` () =
    let csv = """Remark,CPU Type=XGK-CPUE
Type,Scope,Variable,Address,DataType,Property,Comment
Tag,VariableComment,"QX_CV_1_ADV_SV",P00104,"BIT",,"CV1 ADV output"
Tag,VariableComment,"IX_CV_1_ADV_RS",P00028,"BIT",,"CV1 ADV sensor"
Tag,VariableComment,"QX_CV_1_COUNT",P1200,"WORD",,"word skipped"
"""
    let result = CsvParser.parse XGK csv
    Assert.Equal(2, result.Entries.Length)
    Assert.Equal("QX_CV_1_ADV_SV", result.Entries.[0].Name)
    Assert.Equal(SymbolDirection.Output, result.Entries.[0].Direction)
    Assert.Equal(SymbolDirection.Input, result.Entries.[1].Direction)

[<Fact>]
let ``XGK CSV parse — P address uses TL TS and slot direction rules`` () =
    CsvParser.setConfigPath(Path.Combine(AppContext.BaseDirectory, "input-matching-config.json"))
    let csv = """Remark,CPU Type=XGK-CPUE
Type,Scope,Variable,Address,DataType,Property,Comment
Tag,VariableComment,"TL_ASIN_JOGB",P06504,"BIT",,"touch lamp"
Tag,VariableComment,"TS_ASIN_JOGB",P05504,"BIT",,"touch switch"
Tag,VariableComment,"NO_PREFIX_INPUT",P00000,"BIT",,"slot input"
Tag,VariableComment,"NO_PREFIX_OUTPUT",P00120,"BIT",,"slot output"
"""
    let result = CsvParser.parse XGK csv

    Assert.Equal(4, result.Entries.Length)
    Assert.Equal(SymbolDirection.Output, result.Entries.[0].Direction)
    Assert.Equal(SymbolDirection.Input, result.Entries.[1].Direction)
    Assert.Equal(SymbolDirection.Input, result.Entries.[2].Direction)
    Assert.Equal(SymbolDirection.Output, result.Entries.[3].Direction)

[<Fact>]
let ``XGB variable-description CSV parse — variable type device HMI description`` () =
    let csv = """Name,Type,Device,Use,HMI,Description
QX_CV_2_RET_SV,BIT,P00105,1,0,CV2 RET output
IX_CV_2_RET_RS,BIT,P00029,1,0,CV2 RET sensor
IX_CV_2_POS_WORD,WORD,P1072,1,0,word skipped
"""
    let result = CsvParser.parse XGB csv
    Assert.Equal(2, result.Entries.Length)
    Assert.Equal("P00105", result.Entries.[0].Address)
    Assert.Equal("CV2 RET output", result.Entries.[0].Comment)
    Assert.Equal(SymbolDirection.Output, result.Entries.[0].Direction)
    Assert.Equal(SymbolDirection.Input, result.Entries.[1].Direction)

[<Fact>]
let ``XG5000 dispatch accepts LS CSV as well as XML`` () =
    let csv = """Type,Scope,Variable,Address,DataType,Property,Comment
Tag,VariableComment,"QX_UNIT_START_SV",P00100,"BIT",,"start"
Tag,VariableComment,"IX_UNIT_START_RS",P00020,"BIT",,"done"
"""
    let result = CsvParser.parse XG5000 csv
    Assert.Equal(2, result.Entries.Length)
    Assert.Equal(SymbolDirection.Output, result.Entries.[0].Direction)

[<Fact>]
let ``vendor dispatch — AB 는 미구현 warning 만`` () =
    let result = CsvParser.parse AB "anything"
    Assert.Empty(result.Entries)
    Assert.NotEmpty(result.Warnings)
