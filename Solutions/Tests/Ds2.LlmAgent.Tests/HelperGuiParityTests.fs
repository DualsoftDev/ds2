module HelperGuiParityTests

open System
open System.IO
open System.Text.Json
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.LlmAgent

/// extend-mcp §5.6 신규 2 — `add_cylinder` helper 가 Promaker GUI 의 cylinder default cascade
/// (`Solutions/Tests/Ds2.LlmAgent.Tests/Fixtures/WithCyl.json`) 과 *구조 parity* 인지 검증.
///
/// 비교 범위 = naming 외 cascade 구조 (System/Flow/Work/ApiDef/Arrow 갯수·관계).
///   - PassiveSystem.Name 자체는 helper(`Cyl1`) ≠ GUI(`NewFlow_cyl`) → 비교 제외 (todo §9.6 / M11)
///   - SystemType / Work.Duration / ApiDef.TxGuid=RxGuid=Work.Id / Arrow ResetReset / parent 관계만 검증
///   - GUI fixture 의 ApiCall 부분은 helper 책임 외 (rev 4 helper 책임 일관화) → 비교 제외

let private fixturePath =
    Path.Combine(AppContext.BaseDirectory, "Fixtures", "WithCyl.json")

[<Fact>]
let ``Fixture WithCyl.json 가 build output 에 존재`` () =
    Assert.True(File.Exists fixturePath, sprintf "fixture missing: %s (Ds2.LlmAgent.Tests.fsproj 의 CopyToOutputDirectory 점검)" fixturePath)

/// GUI fixture 에서 cylinder cascade 의 카운트/관계 추출.
type private GuiCascadeShape = {
    PassiveSysCount: int
    PassiveSysType: string option
    InternalFlowCount: int  // PassiveSys 의 자식 Flow
    WorkCount: int           // 그 Flow 의 자식 Work
    ApiDefCount: int         // PassiveSys 의 자식 ApiDef
    ResetResetArrowCount: int  // PassiveSys 안 ArrowBetweenWorks (arrowType=4)
    AllWorksHaveDuration500ms: bool
    AllApiDefsBindWorkSelf: bool  // TxGuid = RxGuid = 같은 apiName 의 Work.Id
}

let private parseGuiShape (json: string) : GuiCascadeShape =
    let doc = JsonDocument.Parse(json)
    let root = doc.RootElement
    // PassiveSystem = systems 안 systemType 필드 보유 (Active 는 systemType 키 자체 부재)
    let systems = root.GetProperty("systems")
    let mutable passiveSysId = ""
    let mutable passiveSysType : string option = None
    let mutable passiveSysCount = 0
    for prop in systems.EnumerateObject() do
        let mutable st = JsonElement()
        if prop.Value.TryGetProperty("systemType", &st) && st.ValueKind = JsonValueKind.String then
            passiveSysCount <- passiveSysCount + 1
            passiveSysId <- prop.Name
            passiveSysType <- Some (st.GetString())
    // Internal Flow = parent = passiveSysId
    let flows = root.GetProperty("flows")
    let mutable internalFlowCount = 0
    let mutable internalFlowId = ""
    for prop in flows.EnumerateObject() do
        let parent = prop.Value.GetProperty("parentId").GetString()
        if parent = passiveSysId then
            internalFlowCount <- internalFlowCount + 1
            internalFlowId <- prop.Name
    // Work = parent = internalFlowId
    let works = root.GetProperty("works")
    let mutable workCount = 0
    let workIdsByApiName = System.Collections.Generic.Dictionary<string, string>()
    let mutable allDur500 = true
    for prop in works.EnumerateObject() do
        let parent = prop.Value.GetProperty("parentId").GetString()
        if parent = internalFlowId then
            workCount <- workCount + 1
            let local = prop.Value.GetProperty("localName").GetString()
            workIdsByApiName.[local] <- prop.Name
            let mutable dur = JsonElement()
            if prop.Value.TryGetProperty("duration", &dur) && dur.ValueKind = JsonValueKind.String then
                let s = dur.GetString()
                if s <> "00:00:00.5000000" then allDur500 <- false
            else
                allDur500 <- false
    // ApiDef = parent = passiveSysId
    let apiDefs = root.GetProperty("apiDefs")
    let mutable apiDefCount = 0
    let mutable allBindSelf = true
    for prop in apiDefs.EnumerateObject() do
        let parent = prop.Value.GetProperty("parentId").GetString()
        if parent = passiveSysId then
            apiDefCount <- apiDefCount + 1
            let nm = prop.Value.GetProperty("name").GetString()
            let tx = prop.Value.GetProperty("txGuid").GetString()
            let rx = prop.Value.GetProperty("rxGuid").GetString()
            let mutable expected = ""
            if not (workIdsByApiName.TryGetValue(nm, &expected)) || tx <> expected || rx <> expected then
                allBindSelf <- false
    // ArrowWorks = parent = passiveSysId, arrowType=4
    let arrowWorks = root.GetProperty("arrowWorks")
    let mutable resetResetCount = 0
    for prop in arrowWorks.EnumerateObject() do
        let parent = prop.Value.GetProperty("parentId").GetString()
        let aType = prop.Value.GetProperty("arrowType").GetInt32()
        if parent = passiveSysId && aType = 4 then
            resetResetCount <- resetResetCount + 1
    {
        PassiveSysCount = passiveSysCount
        PassiveSysType = passiveSysType
        InternalFlowCount = internalFlowCount
        WorkCount = workCount
        ApiDefCount = apiDefCount
        ResetResetArrowCount = resetResetCount
        AllWorksHaveDuration500ms = allDur500
        AllApiDefsBindWorkSelf = allBindSelf
    }

let private helperShape () : GuiCascadeShape =
    let store = DsStore()
    store.AddProject("M1") |> ignore
    let plan = ImportPlanBuilder()
    let sysId, _ = ToolOperations.queueAddCylinder plan store "Cyl1" [] None
    let sysType =
        plan.Operations
        |> Seq.tryPick (function
            | AddSystem s when s.Id = sysId -> Some s.SystemType
            | _ -> None)
        |> Option.defaultValue None
    let flow =
        plan.Operations
        |> Seq.pick (function
            | AddFlow f when f.ParentId = sysId -> Some f
            | _ -> None)
    let works =
        plan.Operations
        |> Seq.choose (function
            | AddWork w when w.ParentId = flow.Id -> Some w
            | _ -> None)
        |> List.ofSeq
    let workIdsByApiName =
        works
        |> List.map (fun w -> w.LocalName, w.Id)
        |> Map.ofList
    let apiDefs =
        plan.Operations
        |> Seq.choose (function
            | AddApiDef d when d.ParentId = sysId -> Some d
            | _ -> None)
        |> List.ofSeq
    let allBind =
        apiDefs
        |> List.forall (fun d ->
            match Map.tryFind d.Name workIdsByApiName with
            | Some wid -> d.TxGuid = Some wid && d.RxGuid = Some wid
            | None -> false)
    let arrowResets =
        plan.Operations
        |> Seq.filter (function
            | AddArrowWork a when a.ParentId = sysId && a.ArrowType = ArrowType.ResetReset -> true
            | _ -> false)
        |> Seq.length
    let allDur =
        works |> List.forall (fun w -> w.Duration = Some (TimeSpan.FromMilliseconds 500.))
    {
        PassiveSysCount = 1
        PassiveSysType = sysType
        InternalFlowCount = 1
        WorkCount = works.Length
        ApiDefCount = apiDefs.Length
        ResetResetArrowCount = arrowResets
        AllWorksHaveDuration500ms = allDur
        AllApiDefsBindWorkSelf = allBind
    }

[<Fact>]
let ``helper cylinder cascade 가 GUI canonical (WithCyl.json) 과 구조 parity`` () =
    let json = File.ReadAllText fixturePath
    let gui = parseGuiShape json
    let helper = helperShape ()
    Assert.Equal(gui.PassiveSysCount, helper.PassiveSysCount)
    Assert.Equal(gui.PassiveSysType, helper.PassiveSysType)
    Assert.Equal(gui.InternalFlowCount, helper.InternalFlowCount)
    Assert.Equal(gui.WorkCount, helper.WorkCount)
    Assert.Equal(gui.ApiDefCount, helper.ApiDefCount)
    Assert.Equal(gui.ResetResetArrowCount, helper.ResetResetArrowCount)
    Assert.True(gui.AllWorksHaveDuration500ms, "GUI fixture: all works duration=500ms")
    Assert.True(helper.AllWorksHaveDuration500ms, "helper: all works duration=500ms")
    Assert.True(gui.AllApiDefsBindWorkSelf, "GUI fixture: ApiDef.Tx/Rx = own Work.Id")
    Assert.True(helper.AllApiDefsBindWorkSelf, "helper: ApiDef.Tx/Rx = own Work.Id")
