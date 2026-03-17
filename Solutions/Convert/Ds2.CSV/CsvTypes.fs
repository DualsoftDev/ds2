namespace Ds2.CSV

type CsvRow = {
    FlowName   : string
    WorkName   : string
    DeviceName : string
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
