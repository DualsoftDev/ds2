namespace Ds2.IOList

/// Signal record for import/export operations
type SignalRecord = {
    VarName: string
    Address: string
    DataType: string
    IoType: string
    Category: string
    Comment: string option
    FlowName: string
    WorkName: string
    DeviceName: string
    DeviceAlias: string
    CallName: string
}
