module Ds2.Store.Editor.Tests.CsvFormatDetectorTests

open Xunit
open Ds2.CSV

// 헤더 자동 판별 테스트.
// 계약: 첫 줄만으로 기본 3열 / 표준 9열 / 표준 8열을 결정적으로 가른다(세 헤더 집합은 서로소).
//       그 외 헤더는 Unknown + 진단문이며, 본문은 파싱하지 않는다.

let private detect (content: string) = CsvFormatDetector.detect content

let private header9 = "Flow,Work,Device,System,Api,InName,InAddress,OutName,OutAddress"
let private header8 = "Flow,Work,Device,Api,InName,InAddress,OutName,OutAddress"

[<Fact>]
let ``기본 3열 헤더를 Basic3 로 판별`` () =
    let info = detect "FLOW,WORK,CALL\n가공,드릴링,드릴.회전시작>드릴.회전정지"
    Assert.Equal(CsvFormat.Basic3, info.Format)
    Assert.True(info.IsRecognized)
    Assert.Equal(3, info.FieldCount)
    Assert.Equal(',', info.Separator)
    Assert.Equal("", info.Diagnostic)

[<Fact>]
let ``표준 9열 헤더를 Standard9 로 판별`` () =
    // 라디오 기본값이 3열로 바뀐 뒤 현장 태그표가 CSV001 로 반려되던 바로 그 입력.
    let info = detect (header9 + "\n이송,공급라인,컨베이어,이송_컨베이어,구동,,M00130,,P00080")
    Assert.Equal(CsvFormat.Standard9, info.Format)
    Assert.Equal(9, info.FieldCount)

[<Fact>]
let ``System 열 없는 표준 8열도 그대로 받는다`` () =
    let info = detect (header8 + "\n이송,공급라인,컨베이어,구동,,M00130,,P00080")
    Assert.Equal(CsvFormat.Standard8, info.Format)
    Assert.Equal(8, info.FieldCount)
    Assert.Contains("System 열 없음", CsvFormatDetector.formatNote info.Format)

[<Fact>]
let ``Excel 붙여넣기(탭 구분)도 같은 규격으로 판별`` () =
    let info = detect (header9.Replace(",", "\t"))
    Assert.Equal(CsvFormat.Standard9, info.Format)
    Assert.Equal('\t', info.Separator)
    Assert.Equal("탭", CsvFormatDetector.separatorName info.Separator)

[<Fact>]
let ``대소문자·공백·언더스코어·addr 별칭은 흡수한다`` () =
    let info = detect "flow, WORK ,device,system,api,IN_Name,in addr,Out-Name,OUTADDR"
    Assert.Equal(CsvFormat.Standard9, info.Format)

[<Fact>]
let ``BOM 과 전각 쉼표가 섞여도 판별한다`` () =
    // LLM 이 뱉은 3열 CSV 의 단골 오염. 두 파서가 같은 전처리를 공유하므로 판별과 파싱이 어긋나지 않는다.
    let info = detect ("\uFEFFFLOW，WORK，CALL\n가공,드릴링,드릴.회전시작>드릴.회전정지")
    Assert.Equal(CsvFormat.Basic3, info.Format)

[<Fact>]
let ``빈 내용은 오류가 아니라 판별 전 상태`` () =
    let info = detect "   \n\n"
    Assert.Equal(CsvFormat.Unknown, info.Format)
    Assert.False(info.IsRecognized)
    Assert.Equal("", info.Diagnostic)

[<Fact>]
let ``열 하나가 빠진 헤더는 가장 가까운 규격과의 차이를 짚는다`` () =
    let info = detect "Flow,Work,Device,System,Api,InName,InAddress,OutName"
    Assert.Equal(CsvFormat.Unknown, info.Format)
    Assert.Contains("표준 9열과 비교", info.Diagnostic)
    Assert.Contains("없는 열: outaddress", info.Diagnostic)
    // 받아들이는 헤더 3종을 항상 같이 보여준다 — 무엇으로 고쳐야 하는지가 오류 안에 있어야 한다.
    Assert.Contains("FLOW,WORK,CALL", info.Diagnostic)
    Assert.Contains(header9, info.Diagnostic)

[<Fact>]
let ``모르는 열이 섞이면 그것도 짚는다`` () =
    let info = detect "Flow,Work,Call,Comment"
    Assert.Equal(CsvFormat.Unknown, info.Format)
    Assert.Contains("모르는 열: Comment", info.Diagnostic)

[<Fact>]
let ``이름은 맞고 순서만 다르면 순서 문제로 알려준다`` () =
    let info = detect "WORK,FLOW,CALL"
    Assert.Equal(CsvFormat.Unknown, info.Format)
    Assert.Contains("순서가 다릅니다", info.Diagnostic)

[<Fact>]
let ``헤더 없이 데이터 행만 있으면 그렇게 말해 준다`` () =
    let info = detect "가공,드릴링,드릴.회전시작>드릴.회전정지"
    Assert.Equal(CsvFormat.Unknown, info.Format)
    Assert.Contains("첫 줄은 헤더여야 합니다", info.Diagnostic)

[<Fact>]
let ``읽은 헤더를 그대로 되비춰 준다`` () =
    // 눈에 안 보이는 오염(여분 공백·오타)이 원인일 때 사용자가 스스로 찾을 수 있어야 한다.
    let info = detect "Flow,Work,Devise,System,Api,InName,InAddress,OutName,OutAddress"
    Assert.Contains("읽은 헤더:", info.Diagnostic)
    Assert.Contains("Devise", info.Diagnostic)

[<Fact>]
let ``판별된 규격은 실제 파서가 받아들인다`` () =
    // 판별과 파싱이 갈라지면 배지는 초록인데 불러오기는 실패하는 최악의 상태가 된다.
    let basic = "FLOW,WORK,CALL\n가공,드릴링,드릴.회전시작=500MS>드릴.회전정지=500MS"
    Assert.Equal(CsvFormat.Basic3, (detect basic).Format)
    Assert.True((BasicCsvParser.parse basic) |> Result.isOk)

    let standard = header9 + "\n이송,공급라인,컨베이어,이송_컨베이어,구동,,M00130,,P00080"
    Assert.Equal(CsvFormat.Standard9, (detect standard).Format)
    Assert.True((CsvParser.parse standard) |> Result.isOk)
