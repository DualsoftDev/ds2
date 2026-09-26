module TutorialVerify
open System
open System.IO
open System.Collections.Generic
open System.Security.Cryptography
open System.Text.Json
open Ds2.Core
open Ds2.Core.Store
open Ds2.Runtime.Model
open Ds2.Runtime.Engine
open Ds2.Runtime.Engine.Core
open TutorialModels

type Event = { Seq:int; Time:int64; Kind:string; Node:Guid; Name:string; State:string; Product:string; Skipped:bool; Target:Guid }
type Check = { Name:string; Passed:bool; Actual:string }
let json x=JsonSerializer.Serialize(x,JsonSerializerOptions(WriteIndented=true))
let sha p=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes p)).ToLowerInvariant()
type Scenario = { Case:Case; Sources:Guid array; Sinks:Guid array; Rules:Rule array; Channels:Channel array; LogicalApis:Guid array; Notes:string array; ProductFlows:Guid array; EndWithTokens:bool; SupplyRequirements:SupplyRequirement array }
let replay source (c:Case) =
    let num=sprintf "%03d" c.Id
    let data=JsonSerializer.Deserialize<Scenario>(File.ReadAllText(Path.Combine(source,"scenarios",num+".json")))
    let store=DsStore()
    store.LoadFromFile(Path.Combine(source,"models",num+".ds2.sdf"))
    {Store=store;Case=data.Case;Sources=data.Sources;Sinks=data.Sinks;Rules=data.Rules;Channels=data.Channels;LogicalApis=data.LogicalApis;Notes=data.Notes;ProductFlows=data.ProductFlows;EndWithTokens=data.EndWithTokens;SupplyRequirements=data.SupplyRequirements}
let verify output (bp:Blueprint) =
    let num=sprintf "%03d" bp.Case.Id
    let modelDir=Path.Combine(output,"models")
    let evidence=Path.Combine(output,"evidence",num)
    Directory.CreateDirectory modelDir |> ignore
    Directory.CreateDirectory evidence |> ignore
    let scenarioDir=Path.Combine(output,"scenarios")
    Directory.CreateDirectory scenarioDir |> ignore
    let data:Scenario={Case=bp.Case;Sources=bp.Sources;Sinks=bp.Sinks;Rules=bp.Rules;Channels=bp.Channels;LogicalApis=bp.LogicalApis;Notes=bp.Notes;ProductFlows=bp.ProductFlows;EndWithTokens=bp.EndWithTokens;SupplyRequirements=bp.SupplyRequirements}
    File.WriteAllText(Path.Combine(scenarioDir,num+".json"),json data)
    let path=Path.Combine(modelDir,num+".ds2.sdf")
    bp.Store.SaveToFile path
    let s=DsStore()
    s.LoadFromFile path
    s.SaveToFile(Path.Combine(evidence,"roundtrip.ds2.sdf"))
    let checks=ResizeArray<Check>()
    let check name ok actual=checks.Add {Name=name;Passed=ok;Actual=actual}
    check "정식 SDF 압축 형식" (File.ReadAllBytes(path).[0..1]=[|0x1fuy;0x8buy|]) "gzip"
    check "저장 후 재읽기 요소 수" (s.Works.Count=bp.Store.Works.Count && s.Calls.Count=bp.Store.Calls.Count && s.ApiDefs.Count=bp.Store.ApiDefs.Count && s.ArrowWorks.Count=bp.Store.ArrowWorks.Count && s.ArrowCalls.Count=bp.Store.ArrowCalls.Count) (sprintf "%d Work / %d Call" s.Works.Count s.Calls.Count)
    check "Call을 소유한 Work에 물리 시간 중복 없음" (s.Works.Values |> Seq.forall(fun w->Queries.callsOf w.Id s |> Seq.isEmpty || w.Duration.IsNone)) "leaf Duration only"
    let systemOf w=s.Flows.[s.Works.[w].ParentId].ParentId
    check "API의 대상은 해당 시스템 내부 Work" (s.ApiDefs.Values |> Seq.forall(fun d->[d.TxGuid;d.RxGuid] |> List.choose id |> List.forall(fun w->systemOf w=d.ParentId))) "peer systems"
    check "실행 Call은 다른 시스템을 호출" (s.Calls.Values |> Seq.forall(fun c->c.ApiCalls |> Seq.forall(fun a->systemOf c.ParentId<>s.ApiDefs.[a.ApiDefId.Value].ParentId))) "peer API calls"
    let validation=V10ValidationBatch.validateStore s
    let index=SimIndex.build s 10
    let graph= {|Unreset=GraphValidator.findUnresetWorks index |> List.map(fun(_,sys,w)->sys+"/"+w) |> List.toArray;SourcePred=GraphValidator.findSourcesWithPredecessors index |> List.map string |> List.toArray;GroupIgnore=GraphValidator.findGroupWorksWithoutIgnore index |> List.map string |> List.toArray;Unreachable=GraphValidator.findTokenUnreachableWorks index |> List.map string |> List.toArray|}
    use en=new EventDrivenEngine(index,RuntimeMode.Simulation) :> ISimulationEngine
    let events=ResizeArray<Event>()
    let mutable serial=0
    let mutable generation=0
    let mutable lastGeneration= -1
    let tokens=Dictionary<TokenValue,int>()
    let starts=Dictionary<TokenValue,int64>()
    let completed=Array.zeroCreate<int> bp.Sources.Length
    let admitted=Array.zeroCreate<int> bp.Sources.Length
    let pending=ResizeArray<int64*Guid*string*string>()
    let outputs=ResizeArray<int64*Guid*string>()
    let snapshots=ResizeArray<obj>()
    let admissions=ResizeArray<int64*int*int>()
    let mutable oldOutputs=Map.empty<Guid,string>
    let mutable maxProducts=0
    let mutable maxPerFlow=0
    let mutable maxResource=0
    let resourceWorks=bp.Rules |> Array.filter(fun r->r.Kind="resource-exclusion") |> Array.map(fun r->r.A)
    let releaseWorks=bp.Rules |> Array.filter(fun r->r.Kind="resource-exclusion") |> Array.map(fun r->r.B) |> Array.distinct
    let observeOutput()=
        for KeyValue(k,v) in en.State.OutputValues do
            if Map.tryFind k oldOutputs<>Some v then outputs.Add(en.CurrentTimeMs,k,v)
        oldOutputs<-en.State.OutputValues
    let record kind node name state product skipped target=
        serial<-serial+1;generation<-generation+1
        events.Add {Seq=serial;Time=en.CurrentTimeMs;Kind=kind;Node=node;Name=name;State=state;Product=product;Skipped=skipped;Target=target}
        observeOutput()
    en.WorkStateChanged.AddHandler(fun _ e->
        record "work" e.WorkGuid s.Works.[e.WorkGuid].Name (string e.NewState) (en.GetWorkToken e.WorkGuid |> Option.map string |> Option.defaultValue "") false Guid.Empty
        for r in bp.Rules do
            if r.Kind="rework-switch" && e.WorkGuid=r.A && e.NewState=Status4.Finish && (events |> Seq.exists(fun x->x.Kind="work" && x.Node=r.A && x.State="Going")) then
                en.InjectIOValue(r.B,"false")
                record "fixture" r.B "재작업 뒤 통과 판정" "false" "" false Guid.Empty
            if r.Kind="rotation-permit" && e.WorkGuid=r.A && e.NewState=Status4.Going then
                let n=events |> Seq.filter(fun x->x.Kind="work" && x.Node=r.A && x.State="Going") |> Seq.length
                if n=2 || n=r.N then
                    en.InjectIOValue(r.B,"false")
                    record "fixture" r.B "외부 회전 허가 닫기" "false" "" false Guid.Empty
                    if n=2 then pending.Add(en.CurrentTimeMs+180L,r.B,"true","외부 회전 허가 다시 열기"))
    en.CallStateChanged.AddHandler(fun _ e->record "call" e.CallGuid s.Calls.[e.CallGuid].Name (string e.NewState) "" e.IsSkipped Guid.Empty)
    en.TokenEvent.AddHandler(fun _ e->
        record "token" e.WorkGuid e.WorkName (string e.Kind) (string e.Token) false (e.TargetWorkGuid |> Option.defaultValue Guid.Empty)
        if e.Kind=TokenEventKind.Complete && Array.contains e.WorkGuid bp.Sinks then
            match tokens.TryGetValue e.Token with true,i->completed.[i]<-completed.[i]+1 | _ -> ())
    en.ApplyInitialStates()
    en.SetAllFlowStates FlowTag.Drive
    let slots()=s.Works.Values |> Seq.choose(fun w->en.GetWorkToken w.Id |> Option.map(fun tok->w.ParentId,w.Id,tok)) |> Seq.toArray
    let snapshot()=
        let ss=slots()
        let products=ss |> Array.map(fun(_,_,t)->t) |> Array.distinct |> Array.length
        let perFlow=ss |> Array.groupBy(fun(f,_,_)->f) |> Array.map(fun(_,xs)->xs |> Array.map(fun(_,_,t)->t) |> Array.distinct |> Array.length)
        maxProducts<-max maxProducts products
        maxPerFlow<-max maxPerFlow (if perFlow.Length=0 then 0 else Array.max perFlow)
        let activeResource=Array.append resourceWorks releaseWorks |> Array.filter(fun w->en.GetWorkState w=Some Status4.Going) |> Array.length
        maxResource<-max maxResource activeResource
        if generation<>lastGeneration then
            snapshots.Add(box {|Time=en.CurrentTimeMs;Products=products;ResourceRunning=activeResource;Slots=ss |> Array.map(fun(f,w,t)-> {|Flow=s.Flows.[f].Name;Work=s.Works.[w].Name;Token=string t|})|})
            lastGeneration<-generation
        observeOutput()
    let sourceReady w=en.GetWorkState w=Some Status4.Ready && (en.GetWorkToken w).IsNone && StepSemantics.primableSourceGuids index en.State (fun g->en.GetWorkState g |> Option.defaultValue Status4.Ready) false w=[w]
    let mutable lastActivity=0L
    let mutable finished=false
    let mutable clock=0L
    while not finished && clock<=20000L do
        en.AdvanceSimulationTo clock
        for due,id,value,name in pending |> Seq.filter(fun(t,_,_,_)->t<=clock) |> Seq.toArray do
            en.InjectIOValue(id,value)
            record "fixture" id name value "" false Guid.Empty
        pending.RemoveAll(fun(t,_,_,_)->t<=clock) |> ignore
        for i in 0..bp.Sources.Length-1 do
            let src=bp.Sources.[i]
            let ownFlow=s.Works.[src].ParentId
            let clear=
                if bp.Sources.Length=1 then (slots()).Length=0
                else slots() |> Array.forall(fun(f,_,_)->f<>ownFlow)
            let supplyReady=bp.SupplyRequirements |> Array.filter(fun r->r.Source=src) |> Array.forall(fun r->let dep=Array.findIndex((=)r.Dependency) bp.Sources in completed.[dep]>=admitted.[i]+1-r.Lag)
            if admitted.[i]<bp.Case.Rounds && completed.[i]=admitted.[i] && clear && supplyReady && sourceReady src then
                let ordinal=admitted.[i]+1
                let token=IntToken((i+1)*1000+ordinal)
                tokens.[token]<-i;starts.[token]<-clock
                for ch in bp.Channels do
                    en.InjectIOValue(ch.Id,ch.Initial)
                    record "fixture" ch.Id ch.Name ch.Initial (string token) false Guid.Empty
                    pending.Add(clock+int64 ch.Delay,ch.Id,ch.Values.[(ordinal-1)%ch.Values.Length],ch.Name)
                admissions.Add(clock,i,ordinal)
                record "admission" src s.Works.[src].Name "Ready+Empty" (string token) false Guid.Empty
                en.SeedToken(src,token)
                en.StartSourceWork src
                admitted.[i]<-ordinal
        en.AdvanceSimulationTo clock
        let before=lastGeneration
        snapshot()
        if before<>lastGeneration then lastActivity<-clock
        let boundaryReached=
            if bp.EndWithTokens then bp.Rules |> Array.filter(fun r->r.Kind="work-count") |> Array.forall(fun r->events |> Seq.filter(fun e->e.Kind="work" && e.Node=r.A && e.State="Going") |> Seq.length = r.N)
            else completed |> Array.forall((=) bp.Case.Rounds)
        if boundaryReached then
            if clock-lastActivity>=100L && en.NextEventTimeMs.IsNone && pending.Count=0 then finished<-true
        clock<-clock+1L
    let workEvents w state=
        if state<>"Finish" then events |> Seq.filter(fun e->e.Kind="work" && e.Node=w && e.State=state) |> Seq.toArray
        else
            let mutable started=false
            [| for e in events do
                if e.Kind="work" && e.Node=w then
                    if e.State="Going" then started<-true
                    elif e.State="Finish" && started then
                        started<-false
                        yield e
                    elif e.State="Ready" || e.State="Homing" then started<-false |]
    let callEvents w state=events |> Seq.filter(fun e->e.Kind="call" && e.Node=w && e.State=state) |> Seq.toArray
    let safeName g=match s.Works.TryGetValue g with true,w->w.Name |_->string g
    check "시간 한도 안에 정한 처리 경계 도달" finished (json {|Admitted=admitted;Completed=completed;Time=en.CurrentTimeMs|})
    check "제품 충돌과 강제 버림 없음" (events |> Seq.exists(fun e->e.Kind="token" && (e.State="Conflict" || e.State="Discard")) |> not) "Conflict=0, Discard=0"
    if bp.EndWithTokens then
        check "같은 두 제품을 보유한 채 정상 대기" (slots() |> Array.map(fun(_,_,t)->t) |> Set.ofArray = Set.ofSeq tokens.Keys) (string(slots().Length))
    else check "완료 뒤 제품표가 남지 않음" ((slots()).Length=0) (string(slots().Length))
    check "동일 Flow의 안정 시점 제품 수는 최대 1" (maxPerFlow<=1) (string maxPerFlow)
    let actualComplete=events |> Seq.filter(fun e->e.Kind="token" && e.State="Complete" && Array.contains e.Node bp.Sinks) |> Seq.map(fun e->e.Product) |> Seq.toArray
    if bp.EndWithTokens then check "회전 횟수를 제품 완료로 세지 않음" (actualComplete.Length=0) (string actualComplete.Length)
    else check "제품 번호별 종료 한 번" (actualComplete.Length=tokens.Count && (actualComplete |> Array.distinct |> Array.length)=tokens.Count) (string actualComplete.Length)
    for r in bp.Rules do
        match r.Kind with
        | "work-count" ->let n=(workEvents r.A "Going").Length in check r.Label (n=r.N) (sprintf "%s: %d / expected %d" (safeName r.A) n r.N)
        | "finish-before-start" ->
            let a=workEvents r.A "Finish"
            let bb=workEvents r.B "Going"
            let ok=a.Length=bb.Length && Array.forall2(fun x y->x.Seq<y.Seq && x.Time<=y.Time) a bb
            check r.Label ok (sprintf "%s -> %s (%d,%d)" (safeName r.A) (safeName r.B) a.Length bb.Length)
        | "same-start" ->
            let a=workEvents r.A "Going" |> Array.map(fun e->e.Time)
            let bb=workEvents r.B "Going" |> Array.map(fun e->e.Time)
            check r.Label (a=bb && a.Length=bp.Case.Rounds) (sprintf "%A / %A" a bb)
        | "admission-delay" ->
            let a=workEvents r.A "Going"
            let ats=admissions |> Seq.map(fun(t,_,_)->t) |> Seq.toArray
            check r.Label (a.Length=ats.Length && Array.forall2(fun e t->e.Time>=t+int64 r.N) a ats) (sprintf "wait >= %d ms" r.N)
        | "selected-sequence" ->
            let chosen=events |> Seq.filter(fun e->e.Kind="work" && e.State="Going" && (e.Node=r.A || e.Node=r.B)) |> Seq.map(fun e->if e.Node=r.A then "A" else "B") |> Seq.toArray
            check r.Label (chosen=[|"A";"A";"B";"B"|]) (String.concat "," chosen)
        | "rework-switch" ->
            check r.Label ((workEvents r.A "Going").Length=r.N) (string ((workEvents r.A "Going").Length))
        | "identity-fields" ->
            let specs=s.Projects.Values |> Seq.collect(fun p->p.TokenSpecs) |> Seq.toArray
            check r.Label (specs.Length=bp.Case.Rounds && (specs |> Array.map(fun t->t.Fields.["제품번호"]) |> Array.distinct |> Array.length)=bp.Case.Rounds && (specs |> Array.forall(fun t->t.Fields.["캐리어번호"]="C1" && t.Fields.["로트번호"]="L7"))) (sprintf "%d different products; carrier C1; lot L7" specs.Length)
        | "batch-members" ->
            let specs=s.Projects.Values |> Seq.collect(fun p->p.TokenSpecs) |> Seq.toArray
            let members=specs |> Array.collect(fun t->t.Fields.["구성제품"].Split(','))
            check r.Label (specs.Length=bp.Case.Rounds && members.Length=bp.Case.Rounds*r.N && (Array.distinct members).Length=members.Length) (sprintf "%d batches / %d members" specs.Length members.Length)
        | "assembly-lineage" ->
            let specs=s.Projects.Values |> Seq.collect(fun p->p.TokenSpecs) |> Seq.toArray
            let ok=specs |> Array.forall(fun spec->
                let admit=events |> Seq.tryFind(fun e->e.Kind="admission" && e.Product=string(IntToken spec.Id))
                match admit with
                | None->false
                | Some a->["원부품A";"원부품B"] |> List.forall(fun k->events |> Seq.exists(fun e->e.Kind="token" && e.State="Complete" && e.Product=string(IntToken(int spec.Fields.[k])) && e.Seq<a.Seq)))
            check r.Label (ok && specs.Length=bp.Case.Rounds) (sprintf "%d explicit parent maps; creation by supply fixture" specs.Length)
        | "setting-at-start" ->
            let second=s.Works.Values |> Seq.find(fun w->w.LocalName="설정22") |> fun w->w.Id
            let last node seq=events |> Seq.filter(fun e->e.Kind="work" && e.Node=node && e.Seq<seq) |> Seq.tryLast |> Option.map(fun e->e.State)
            let actual=workEvents r.A "Going" |> Array.map(fun e->if last r.B e.Seq=Some "Finish" then 21 elif last second e.Seq=Some "Finish" then 22 else -1)
            check r.Label (actual=[|21;21;22;22|]) (sprintf "%A" actual)
        | "destination-sequence" ->
            let actual=events |> Seq.filter(fun e->e.Kind="token" && e.State="Complete" && (e.Node=r.A || e.Node=r.B)) |> Seq.map(fun e->if e.Node=r.A then "L" else "R") |> Seq.toArray
            check r.Label (actual=[|"L";"L";"R";"R"|]) (String.concat "," actual)
        | "rotation-permit" ->
            let times=workEvents r.A "Going" |> Array.map(fun e->e.Time)
            check r.Label (times.Length=r.N && times.[2]>=times.[1]+180L) (sprintf "%A" times)
        | "fixed-order" ->
            let actual=events |> Seq.filter(fun e->e.Kind="work" && e.State="Going" && Array.contains e.Node resourceWorks) |> Seq.map(fun e->Array.findIndex((=) e.Node) resourceWorks) |> Seq.toArray
            let expected=[|for _ in 1..bp.Case.Rounds do yield! [|0..resourceWorks.Length-1|]|]
            check r.Label (actual=expected) (sprintf "%A" actual)
        | "output-duration" ->
            let vs=outputs |> Seq.filter(fun(_,g,_)->g=r.A) |> Seq.toArray
            let widths=vs |> Array.pairwise |> Array.choose(fun((a,_,v),(bb,_,v2))->if v.Equals("true",StringComparison.OrdinalIgnoreCase) && v2.Equals("false",StringComparison.OrdinalIgnoreCase) then Some(int(bb-a)) else None)
            check r.Label (widths.Length=bp.Case.Rounds && widths |> Array.forall((=)r.N)) (sprintf "%A ms" widths)
        | "sense-delay" ->
            let cs=callEvents r.A "Finish"
            let ws=workEvents r.B "Finish"
            let delays=if cs.Length=ws.Length then Array.map2(fun c w->int(c.Time-w.Time)) cs ws else [||]
            check r.Label (delays.Length=bp.Case.Rounds && delays |> Array.forall((=)r.N)) (sprintf "%A ms" delays)
        | "resource-exclusion" ->check r.Label (maxResource<=1) (sprintf "max concurrent resource Works=%d" maxResource)
        | "max-products" ->check r.Label (maxProducts=r.N) (sprintf "max=%d, expected=%d" maxProducts r.N)
        | "token-transfer" ->
            let shifts=events |> Seq.filter(fun e->e.Kind="token" && e.Node=r.A && e.Target=r.B) |> Seq.map(fun e->e.Product) |> Seq.distinct |> Seq.length
            check r.Label (shifts=r.N) (string shifts)
        | "output-observed" ->
            let values=outputs |> Seq.filter(fun(_,g,_)->g=r.A) |> Seq.map(fun(_,_,v)->v.ToLowerInvariant()) |> Seq.toArray
            check r.Label (Array.contains "true" values && Array.contains "false" values) (String.concat "," values)
        | _ ->check ("지원하지 않는 검사:"+r.Kind) false "unknown rule"
    let errors=validation |> List.filter(fun x->x.Severity=V10Validation.Error)
    check "명시한 논리 API 외의 정적 오류 없음" (errors |> List.forall(fun x->x.Rule="V2") && errors.Length=(bp.LogicalApis |> Array.distinct |> Array.length)) (sprintf "V2=%d logicalApi=%d" errors.Length bp.LogicalApis.Length)
    let result={|Id=bp.Case.Id;Title=bp.Case.Title;Family=bp.Case.Family;Variant=bp.Case.Variant;Mode="Simulation";Unit=bp.Case.Unit;Rounds=bp.Case.Rounds;Model=num+".ds2.sdf";ModelSHA256=sha path;RuntimePassed=checks |> Seq.forall(fun x->x.Passed);StaticValidationPassed=errors.IsEmpty;Validation=validation |> List.map(fun x->{|Rule=x.Rule;Severity=string x.Severity;Message=x.Message|}) |> List.toArray;Graph=graph;Checks=checks.ToArray();TimeMs=en.CurrentTimeMs;Admitted=admitted;Completed=completed;MaxProducts=maxProducts;MaxProductsPerFlow=maxPerFlow;Works=s.Works.Count;Calls=s.Calls.Count;Systems=s.Systems.Count;Notes=bp.Notes;EventCount=events.Count;InitialFinish=events |> Seq.filter(fun e->e.Kind="work" && e.State="Finish" && e.Time=0L) |> Seq.map(fun e->e.Name) |> Seq.toArray;Inputs=bp.Channels;SupplyRequirements=bp.SupplyRequirements|}
    File.WriteAllText(Path.Combine(evidence,"result.json"),json result)
    File.WriteAllText(Path.Combine(evidence,"events.json"),json(events.ToArray()))
    File.WriteAllText(Path.Combine(evidence,"states.json"),json(snapshots.ToArray()))
    File.WriteAllText(Path.Combine(evidence,"outputs.json"),json(outputs |> Seq.map(fun(t,g,v)-> {|Time=t;ApiCall=g;Value=v|}) |> Seq.toArray))
    printfn "RESULT %03d %s runtime=%b static=%b products=%A" bp.Case.Id bp.Case.Family result.RuntimePassed result.StaticValidationPassed completed
    for x in checks do if not x.Passed then printfn "FAIL %s: %s" x.Name x.Actual
    result.RuntimePassed
