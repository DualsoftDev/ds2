namespace Ds2.CSV

type CsvRow = {
    FlowName   : string
    WorkName   : string
    DeviceName : string
    SystemName : string
    ApiName    : string
    InName     : string
    InAddress  : string
    OutName    : string
    OutAddress : string
    LineNumber : int
}

type CsvEntry = {
    FlowName    : string
    WorkName    : string
    DeviceName  : string
    DeviceAlias : string
    SystemName  : string
    ApiName     : string
    IsSyntheticApi: bool
    InName      : string option
    InAddress   : string option
    OutName     : string option
    OutAddress  : string option
    SourceLines : int list
}

type CsvDocument = {
    Entries: CsvEntry list
}

type CsvImportPreview = {
    FlowNames          : string list
    WorkNames          : string list
    PassiveSystemNames : string list
    CallNames          : string list
    SyntheticApiCount  : int
}

type ParseError = {
    LineNumber: int
    Message   : string
}

[<RequireQualifiedAccess>]
module ParseError =
    let toString (error: ParseError) =
        if error.LineNumber > 0 then
            $"line {error.LineNumber}: {error.Message}"
        else
            error.Message

/// 첫 줄(헤더)만으로 판별되는 CSV 규격.
/// 세 헤더 집합은 서로소라 판별이 결정적이다 — 사용자가 형식을 고를 필요가 없다.
type CsvFormat =
    | Unknown = 0
    | Basic3 = 1
    | Standard9 = 2
    | Standard8 = 3

/// 헤더 판별 결과. Format = Unknown 이면 Diagnostic 이 사용자에게 그대로 보여줄 오류 전문이다.
type CsvHeaderInfo = {
    Format     : CsvFormat
    Separator  : char
    FieldCount : int
    /// 정규화(BOM 제거·NFC·전각→반각) 후의 헤더 줄 원문. 무엇이 읽혔는지 되비춰 주기 위한 것.
    HeaderText : string
    Diagnostic : string
}
with
    member this.IsRecognized = this.Format <> CsvFormat.Unknown
