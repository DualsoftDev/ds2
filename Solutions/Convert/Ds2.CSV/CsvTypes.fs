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
    /// Call 이 속한 Active System 이름. 빈칸이면 가져오기 대화상자의 System.
    ActiveSystemName : string
    /// Reference Call 이면 원본 Call 이 있는 Work 의 "Flow.Work". 아니면 빈칸.
    ReferenceOf : string
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
    /// Call 이 속한 Active System 이름. None 이면 가져오기 대상(대화상자) System.
    /// 예전엔 이 열이 없어 여러 Active System 이 하나로 합쳐지고 같은 이름의 Flow·Work 가 섞였다.
    ActiveSystemName : string option
    /// Reference Call 이면 원본 Call 이 있는 Work 의 "Flow.Work"(같은 Active System 안).
    /// 예전엔 Reference 를 일반 행으로 써서 다시 읽으면 유령 디바이스나 원본의 두 번째 ApiCall 이 됐다.
    ReferenceOf : string option
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
    /// ActiveSystem 열에 적힌 System 이름(등장 순). 비어 있으면 모든 행이 대화상자 System 으로 간다.
    ActiveSystemNames  : string list
    /// ReferenceOf 가 채워진 행 수 — 디바이스를 만들지 않고 원본 Call 을 가리키는 Call 이 된다.
    ReferenceCallCount : int
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
    /// ds2-csv-for-ai/v1 — 7열(Kind,Name,Type,Detail,Time,InTag,OutTag).
    | AiModel = 4
    /// 표준 9열 + ActiveSystem,ReferenceOf. 프로젝트 내보내기의 기본 형식.
    | Standard11 = 5

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
