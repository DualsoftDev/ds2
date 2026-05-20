module Ds2.SymbolImport.Tests.CsvParserTests

open Ds2.SymbolImport
open Xunit

[<Fact>]
let ``Mitsubishi CSV 3 컬럼 (Device/Label/Comment) parse`` () =
    let csv = "Device,Label,Comment\nX10,Cyl1_ADV_LMT,실린더1 전진 리밋\nY20,Cyl1_ADV,실린더1 전진 출력"
    let result = CsvParser.parseMitsubishi csv
    Assert.Equal(2, result.Entries.Length)
    Assert.Equal("X10", result.Entries.[0].Address)
    Assert.Equal("Cyl1_ADV_LMT", result.Entries.[0].Name)
    Assert.Equal(SymbolDirection.Input, result.Entries.[0].Direction)
    Assert.Equal(SymbolDirection.Output, result.Entries.[1].Direction)

[<Fact>]
let ``Mitsubishi CSV 컬럼 부족 — warning + skip`` () =
    let csv = "Device,Label\nX10\nY20,Cyl_ADV"
    let result = CsvParser.parseMitsubishi csv
    Assert.Equal(1, result.Entries.Length)
    Assert.NotEmpty(result.Warnings)

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
let ``vendor dispatch — AB 는 미구현 warning 만`` () =
    let result = CsvParser.parse AB "anything"
    Assert.Empty(result.Entries)
    Assert.NotEmpty(result.Warnings)
