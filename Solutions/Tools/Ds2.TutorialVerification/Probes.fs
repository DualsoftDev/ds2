module TutorialProbes
open System
open System.IO
open System.Text.Json
open Ds2.Core
open Ds2.Runtime.Model
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core
open TutorialModels
open TutorialVerify

// Bounded, in-memory mode comparisons. No PLC adapter or network is created.
let run source output =
    Directory.CreateDirectory output |> ignore
    let cases=JsonSerializer.Deserialize<Case array>(File.ReadAllText(Path.Combine(source,"..","catalog.json")))
    let results=ResizeArray<obj>()
    for name,caseId,mode,policy,inputs in [
        "latch-false-control",79,RuntimeMode.Control,"Latch",[5L,"false"]
        "latch-true-control",79,RuntimeMode.Control,"Latch",[5L,"true"]
        "normal-chatter-control",79,RuntimeMode.Control,"Normal",[5L,"true";8L,"false";20L,"true"]
        "virtual10-simulation",79,RuntimeMode.Simulation,"Virtual",[]
        "virtual10-control",79,RuntimeMode.Control,"Virtual",[]
        "virtual100-simulation",79,RuntimeMode.Simulation,"Virtual100",[]
        "virtual100-control",79,RuntimeMode.Control,"Virtual100",[]
        "logical-noio-control",80,RuntimeMode.Control,"Original",[]
        "logical-noio-simulation",80,RuntimeMode.Simulation,"Original",[] ] do
        let bp=replay source (cases |> Array.find(fun c->c.Id=caseId))
        let s=bp.Store
        let call=if caseId=79 then s.Calls.Values |> Seq.find(fun c->c.ApiName="동작") else s.Calls.Values |> Seq.find(fun c->c.ApiName="다음준비")
        let ac=call.ApiCalls.[0]
        let ad=s.ApiDefs.[ac.ApiDefId.Value]
        if policy="Virtual" then ad.SensingType<-SensingType.Virtual 10;ac.InTag<-None
        if policy="Virtual100" then ad.SensingType<-SensingType.Virtual 100;ac.InTag<-None
        if policy="Normal" then ad.SensingType<-SensingType.Normal(Some 10)
        s.SaveToFile(Path.Combine(output,name+".ds2.sdf"))
        let index=SimIndex.build s 10
        use en=new EventDrivenEngine(index,mode) :> ISimulationEngine
        let events=ResizeArray<Event>()
        let record kind node state value =
            events.Add {Seq=events.Count+1;Time=en.CurrentTimeMs;Kind=kind;Node=node;Name=(if kind="call" then s.Calls.[node].Name elif kind="work" then s.Works.[node].Name else "external input");State=state;Product=value;Skipped=false;Target=Guid.Empty}
        en.WorkStateChanged.AddHandler(fun _ e->record "work" e.WorkGuid (string e.NewState) "")
        en.CallStateChanged.AddHandler(fun _ e->record "call" e.CallGuid (string e.NewState) "")
        en.ApplyInitialStates();en.SetAllFlowStates FlowTag.Drive
        en.SeedToken(bp.Sources.[0],IntToken 1001)
        en.StartSourceWork bp.Sources.[0]
        for time in 0L..200L do
            en.AdvanceSimulationTo time
            for due,value in inputs do
                if due=time then
                    en.InjectIOValue(ac.Id,value)
                    record "input" ac.Id value ""
        let target=ad.RxGuid.Value
        let points kind node state=events |> Seq.filter(fun e->e.Kind=kind && e.Node=node && e.State=state) |> Seq.map(fun e->e.Time) |> Seq.toArray
        let report= {|Name=name;Mode=string mode;BaseCase=caseId;Call=call.Name;Target=s.Works.[target].Name;Policy=policy;Inputs=inputs;CallStarts=points "call" call.Id "Going";CallFinishes=points "call" call.Id "Finish";TargetStarts=points "work" target "Going";TargetFinishes=points "work" target "Finish";EndState=string(en.GetCallState call.Id);WindowMs=200;Events=events.ToArray()|}
        results.Add(box report)
        File.WriteAllText(Path.Combine(output,name+".json"),json report)
        printfn "PROBE %s CallFinish=%A TargetFinish=%A" name report.CallFinishes report.TargetFinishes
    File.WriteAllText(Path.Combine(output,"summary.json"),json(results.ToArray()))
    0
