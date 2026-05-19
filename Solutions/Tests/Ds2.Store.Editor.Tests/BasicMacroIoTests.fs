module Ds2.Store.Editor.Tests.BasicMacroIoTests

open System
open Ds2.Editor
open Xunit

let private flowBase (name, iw, qw, mw) : BasicMacroIo.FlowBase = {
    FlowName = name
    IwBase = iw
    QwBase = qw
    MwBase = mw
}

let private ctx (callId, apiCallId, apiCallName, flow, device, api) : BasicMacroIo.ApiCallContext = {
    ParentCallId = callId
    ApiCallId = apiCallId
    ApiCallName = apiCallName
    FlowName = flow
    DeviceAlias = device
    ApiName = api
}

let private noSignalCount _ _ = None

[<Fact>]
let ``expand replaces F D A tokens`` () =
    let result = BasicMacroIo.expand "W_$(F)_WRS_$(D)_$(A)" "F1" "Cyl1" "ADV"
    Assert.Equal("W_F1_WRS_Cyl1_ADV", result)

[<Fact>]
let ``expand with null macro returns empty string`` () =
    let result = BasicMacroIo.expand null "F1" "D" "A"
    Assert.Equal("", result)

[<Fact>]
let ``expand with null replacement uses empty string`` () =
    let result = BasicMacroIo.expand "$(F)_$(D)_$(A)" null null null
    Assert.Equal("__", result)

[<Fact>]
let ``generate with empty input yields empty result`` () =
    let input : BasicMacroIo.Input = {
        IwMacro = ""
        QwMacro = ""
        MwMacro = ""
        FlowBases = []
    }
    let result = BasicMacroIo.generate noSignalCount input []
    Assert.Empty(result)

[<Fact>]
let ``single context emits one row with macro-expanded symbols and base addresses`` () =
    let callId = Guid.NewGuid()
    let apiCallId = Guid.NewGuid()
    let input : BasicMacroIo.Input = {
        IwMacro = "W_$(F)_WRS_$(D)_$(A)"
        QwMacro = "W_$(F)_SOL_$(D)_$(A)"
        MwMacro = ""
        FlowBases = [ flowBase ("F1", 100, 200, 300) ]
    }
    let contexts = [
        ctx (callId, apiCallId, "Cyl1.ADV", "F1", "Cyl1", "ADV")
    ]

    let result = BasicMacroIo.generate noSignalCount input contexts
    Assert.Equal(1, result.Length)
    let row = result.[0]
    Assert.Equal(callId, row.CallId)
    Assert.Equal(apiCallId, row.ApiCallId)
    Assert.Equal("F1", row.Flow)
    Assert.Equal("Cyl1", row.Device)
    Assert.Equal("ADV", row.Api)
    Assert.Equal("W_F1_WRS_Cyl1_ADV", row.InSymbol)
    Assert.Equal("W_F1_SOL_Cyl1_ADV", row.OutSymbol)
    Assert.Equal("%IW100.0", row.InAddress)
    Assert.Equal("%QW200.0", row.OutAddress)

[<Fact>]
let ``IW address rolls into next word after 16 bits`` () =
    let callId = Guid.NewGuid()
    let input : BasicMacroIo.Input = {
        IwMacro = "$(A)"
        QwMacro = ""
        MwMacro = ""
        FlowBases = [ flowBase ("F1", 10, 0, 0) ]
    }
    // 17개 ApiCall — IW 인덱스 0..16, 16번째에서 word 1 로 롤
    let contexts =
        [ for i in 0 .. 16 ->
            ctx (callId, Guid.NewGuid(), sprintf "D%d.A%d" i i, "F1", sprintf "D%d" i, sprintf "A%d" i) ]

    let result = BasicMacroIo.generate noSignalCount input contexts
    Assert.Equal(17, result.Length)
    Assert.Equal("%IW10.0", result.[0].InAddress)
    Assert.Equal("%IW10.15", result.[15].InAddress)
    Assert.Equal("%IW11.0", result.[16].InAddress)

[<Fact>]
let ``signal count above threshold skips additional ApiCalls`` () =
    let callId = Guid.NewGuid()
    let input : BasicMacroIo.Input = {
        IwMacro = "$(A)"
        QwMacro = ""
        MwMacro = ""
        FlowBases = [ flowBase ("F1", 0, 0, 0) ]
    }
    // 같은 (callId, "ADV") 3번 — count=2 면 nth=3 skip
    let contexts = [
        ctx (callId, Guid.NewGuid(), "Cyl1.ADV", "F1", "Cyl1", "ADV")
        ctx (callId, Guid.NewGuid(), "Cyl1.ADV", "F1", "Cyl1", "ADV")
        ctx (callId, Guid.NewGuid(), "Cyl1.ADV", "F1", "Cyl1", "ADV")
    ]
    let lookup cid api =
        if cid = callId && api = "ADV" then Some 2 else None

    let result = BasicMacroIo.generate lookup input contexts
    Assert.Equal(2, result.Length)

[<Fact>]
let ``QW address and symbol are shared across same (Call, ApiName) duplicate ApiCalls`` () =
    let callId = Guid.NewGuid()
    let input : BasicMacroIo.Input = {
        IwMacro = "$(D)_$(A)"
        QwMacro = "$(D)_$(A)"
        MwMacro = ""
        FlowBases = [ flowBase ("F1", 0, 0, 0) ]
    }
    // 같은 (call, ADV) 2개 — QW 캐시 hit. IW 는 device 다르면 새 주소.
    let contexts = [
        ctx (callId, Guid.NewGuid(), "Cyl1.ADV", "F1", "Cyl1", "ADV")
        ctx (callId, Guid.NewGuid(), "Cyl2.ADV", "F1", "Cyl1", "ADV")  // deviceAlias 는 parent 의 Cyl1
    ]

    let result = BasicMacroIo.generate noSignalCount input contexts
    Assert.Equal(2, result.Length)
    // QW: 둘 다 동일 주소/심볼 (캐시 hit)
    Assert.Equal(result.[0].OutAddress, result.[1].OutAddress)
    Assert.Equal(result.[0].OutSymbol, result.[1].OutSymbol)
    Assert.Equal("%QW0.0", result.[0].OutAddress)
    // IW: device 가 ApiCall.Name 의 prefix 라 Cyl1, Cyl2 로 다름 → 주소도 다름
    Assert.Equal("%IW0.0", result.[0].InAddress)
    Assert.Equal("%IW0.1", result.[1].InAddress)
    Assert.Equal("Cyl1_ADV", result.[0].InSymbol)
    Assert.Equal("Cyl2_ADV", result.[1].InSymbol)

[<Fact>]
let ``context with unknown flow yields empty addresses but row still emitted`` () =
    let callId = Guid.NewGuid()
    let input : BasicMacroIo.Input = {
        IwMacro = "$(A)"
        QwMacro = "$(A)"
        MwMacro = ""
        FlowBases = []  // FlowBases 비어있음
    }
    let contexts = [
        ctx (callId, Guid.NewGuid(), "Cyl1.ADV", "F1", "Cyl1", "ADV")
    ]

    let result = BasicMacroIo.generate noSignalCount input contexts
    Assert.Equal(1, result.Length)
    Assert.Equal("", result.[0].InAddress)
    Assert.Equal("", result.[0].OutAddress)
    // InSymbol 은 매크로 expansion 결과 그대로 (flow 매칭 안 돼도 set).
    // OutSymbol 은 flow 매칭 안 되면 set 안 됨 (C# 원본 동작 동일).
    Assert.Equal("ADV", result.[0].InSymbol)
    Assert.Equal("", result.[0].OutSymbol)

[<Fact>]
let ``empty FlowName context is skipped entirely`` () =
    let input : BasicMacroIo.Input = {
        IwMacro = "$(A)"
        QwMacro = ""
        MwMacro = ""
        FlowBases = [ flowBase ("F1", 0, 0, 0) ]
    }
    let contexts = [
        ctx (Guid.NewGuid(), Guid.NewGuid(), "Cyl1.ADV", "", "Cyl1", "ADV")
    ]

    let result = BasicMacroIo.generate noSignalCount input contexts
    Assert.Empty(result)

[<Fact>]
let ``ApiCall name without dot falls back to parent device alias`` () =
    let callId = Guid.NewGuid()
    let input : BasicMacroIo.Input = {
        IwMacro = "$(D)_$(A)"
        QwMacro = ""
        MwMacro = ""
        FlowBases = [ flowBase ("F1", 0, 0, 0) ]
    }
    // ApiCall.Name 에 '.' 없음 → parent 의 DeviceAlias 사용
    let contexts = [
        ctx (callId, Guid.NewGuid(), "noprefix", "F1", "ParentDev", "ADV")
    ]

    let result = BasicMacroIo.generate noSignalCount input contexts
    Assert.Equal(1, result.Length)
    Assert.Equal("ParentDev", result.[0].Device)
    Assert.Equal("ParentDev_ADV", result.[0].InSymbol)
