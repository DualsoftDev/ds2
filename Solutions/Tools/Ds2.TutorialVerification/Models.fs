module TutorialModels
open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor

type Case = { Id:int; Title:string; Family:string; Variant:string; Rounds:int; Delay:int; Unit:string }
type Rule = { Kind:string; A:Guid; B:Guid; N:int; Label:string }
type Channel = { Id:Guid; Values:string array; Initial:string; Delay:int; Name:string }
type SupplyRequirement = { Source:Guid; Dependency:Guid; Lag:int }
type Blueprint = { Store:DsStore; Case:Case; Sources:Guid array; Sinks:Guid array; Rules:Rule array; Channels:Channel array; LogicalApis:Guid array; Notes:string array; ProductFlows:Guid array; EndWithTokens:bool; SupplyRequirements:SupplyRequirement array }
let ms x = Some(TimeSpan.FromMilliseconds(float x))
let require label ok = if not ok then failwith label

type Builder(c:Case) =
    let s=DsStore()
    let p=s.AddProject(sprintf "%03d_%s" c.Id c.Title)
    let rules=ResizeArray<Rule>()
    let logical=ResizeArray<Guid>()
    let channels=ResizeArray<Channel>()
    let notes=ResizeArray<string>()
    let counts=Dictionary<Guid,int>()
    member _.Store=s
    member _.Project=p
    member _.System(name,active)=
        let g=s.AddSystem(name,p,active)
        s.Systems.[g].SystemType<-Some "Tutorial"
        g
    member _.Flow(name,sys)=s.AddFlow(name,sys)
    member _.Work(name,flow,duration)=
        let g=s.AddWork(name,flow)
        let n=match counts.TryGetValue flow with true,v->v |_->0
        counts.[flow]<-n+1
        s.Works.[g].Position<-Some(Xywh(80+(n%4)*250,80+(n/4)*150,190,85))
        s.Works.[g].Duration<-duration
        g
    member _.Edge(a,b,t)=require (sprintf "Arrow rejected: %A %A %A" a b t) (s.ConnectSelectionInOrder([a;b],t)=1)
    member this.Def(sys,name,tx,rx,action,sensing)=
        let d=s.AddApiDefWithProperties(name,sys)
        let v=s.ApiDefs.[d]
        v.TxGuid<-tx;v.RxGuid<-Some rx;v.ActionType<-action;v.SensingType<-sensing
        v.Description<-Some "학습용 모델. 실제 I/O 연결과 설비 운전은 별도 확인합니다."
        d
    member _.Call(owner,def,physical)=
        let ad=s.ApiDefs.[def]
        let g=s.AddCallWithLinkedApiDefs(owner,s.Systems.[ad.ParentId].Name,ad.Name,[def])
        let ac=s.Calls.[g].ApiCalls.[0]
        ac.InputSpec<-BoolValue(Single true)
        ac.OutputSpec<-BoolValue(Single true)
        let index=s.Calls.Count
        s.Calls.[g].Position<-Some(Xywh(80+((index-1)%4)*230,80+((index-1)/4)*130,185,75))
        if physical then
            match ad.ActionType with
            | ActionType.Virtual -> ()
            | _ -> ac.OutTag<-Some(IOTag("요청_"+ad.Name,sprintf "SIM_OUT_%03d" index,"모의 장치 요청"))
            match ad.SensingType with
            | SensingType.Virtual _ -> ()
            | _ -> ac.InTag<-Some(IOTag("완료_"+ad.Name,sprintf "SIM_IN_%03d" index,"모의 장치 완료"))
        else logical.Add ac.Id
        g
    member this.Operation(owner,sys,flow,name,duration,physical,action)=
        let w=this.Work(name,flow,duration)
        let d=this.Def(sys,name,Some w,w,action,SensingType.Normal None)
        let call=this.Call(owner,d,physical)
        w,call
    member this.Fact(sample:Guid,sys,name,rx)=
        let d=this.Def(sys,name,None,rx,ActionType.Virtual,SensingType.Normal None)
        let a=s.Calls.[sample].ApiCalls.[0].DeepCopy()
        a.Id<-Guid.NewGuid();a.Name<-name;a.ApiDefId<-Some d
        a.InTag<-None;a.OutTag<-None;a.OutputSpec<-UndefinedValue;a.OriginFlowId<-None
        a
    member _.Condition(owner,kind,work,facts:ApiCall list,invert)=
        if work then s.AddWorkCondition(owner,kind) else s.AddCallCondition(owner,kind)
        let rows=if work then s.Works.[owner].Conditions else s.Calls.[owner].Conditions
        let r=rows |> Seq.findBack(fun x->x.Type=Some kind)
        r.IsInverted<-invert
        for fact in facts do
            let ac=fact.DeepCopy()
            ac.Id<-fact.Id
            r.ApiCalls.Add ac
    member _.Rule(kind,a,b,n,label)=rules.Add {Kind=kind;A=a;B=b;N=n;Label=label}
    member this.Count(w,n,label)=this.Rule("work-count",w,Guid.Empty,n,label)
    member this.Before(a,b,label)=this.Rule("finish-before-start",a,b,0,label)
    member _.Channel(a:ApiCall,values,initial,delay,name)=channels.Add {Id=a.Id;Values=values;Initial=initial;Delay=delay;Name=name}
    member _.Note(v)=notes.Add v
    member _.Finish(sources,sinks,flows)=
        {Store=s;Case=c;Sources=List.toArray sources;Sinks=List.toArray sinks;Rules=rules.ToArray();Channels=channels.ToArray();LogicalApis=logical.ToArray();Notes=notes.ToArray();ProductFlows=List.toArray flows;EndWithTokens=false;SupplyRequirements=[||]}

let jig (c:Case) =
    let b=Builder c
    let s=b.Store
    let line=b.System("패널공정",true)
    let f=b.Flow("패널한장",line)
    let entry=b.Work("제품받기",f,None)
    let clamp=b.Work("잡고놓기",f,None)
    let weld=b.Work((if c.Id=85 then "눌러끼우기" else "용접하기"),f,None)
    let sink=b.Work("제품끝",f,None)
    s.Works.[entry].TokenRole<-TokenRole.Source;s.Works.[sink].TokenRole<-TokenRole.Sink;s.Works.[weld].TokenRole<-TokenRole.Ignore
    b.Edge(entry,clamp,ArrowType.Start);b.Edge(clamp,weld,ArrowType.Group);b.Edge(clamp,sink,ArrowType.Start)
    for w in [entry;clamp;weld] do b.Edge(sink,w,ArrowType.Reset)
    b.Edge(entry,sink,ArrowType.Reset)
    let cyl=b.System("잡는장치",false)
    let cf=b.Flow("앞뒤동작",cyl)
    let adv,ca=b.Operation(clamp,cyl,cf,"잡기",ms 30,true,ActionType.Latch)
    let ret,cr=b.Operation(clamp,cyl,cf,"놓기",ms 25,true,ActionType.Virtual)
    b.Edge(adv,ret,ArrowType.ResetReset);b.Edge(ca,cr,ArrowType.Start)
    let tool=b.System((if c.Id=85 then "압입장치" else "용접장치"),false)
    let tf=b.Flow("작업동작",tool)
    let run,cc=b.Operation(weld,tool,tf,(if c.Id=85 then "눌러끼우기" else "용접"),ms 45,true,ActionType.Normal None)
    let prep,cp=b.Operation(weld,tool,tf,"다음준비",None,false,ActionType.Virtual)
    b.Edge(run,prep,ArrowType.ResetReset);b.Edge(cp,cc,ArrowType.Start)
    let af=s.Calls.[ca].ApiCalls.[0]
    let wf=s.Calls.[cc].ApiCalls.[0]
    b.Condition(cc,ConditionType.AutoAux,false,[af],false)
    b.Condition(cr,ConditionType.AutoAux,false,[wf],false)
    b.Before(adv,run,"이번 잡기 완료 뒤 가공")
    b.Before(run,ret,"이번 가공 완료 뒤 놓기")
    b.Rule("same-start",clamp,weld,0,"같은 제품의 두 Work 동시 시작")
    for w in [adv;ret;run;prep] do b.Count(w,c.Rounds,"매 제품에서 새 장치 실행")
    if c.Variant="two-clamps" then
        let cyl2=b.System("두번째잡는장치",false)
        let f2=b.Flow("앞뒤동작",cyl2)
        let av2,ac2=b.Operation(clamp,cyl2,f2,"잡기2",ms 20,true,ActionType.Latch)
        let rv2,rc2=b.Operation(clamp,cyl2,f2,"놓기2",ms 15,true,ActionType.Virtual)
        b.Edge(av2,rv2,ArrowType.ResetReset);b.Edge(ca,ac2,ArrowType.Start);b.Edge(ac2,rc2,ArrowType.Start)
        b.Condition(rc2,ConditionType.AutoAux,false,[wf],false)
        b.Condition(cc,ConditionType.AutoAux,false,[s.Calls.[ac2].ApiCalls.[0]],false)
        b.Count(av2,c.Rounds,"두 번째 집게도 매번 잡음");b.Count(rv2,c.Rounds,"두 번째 집게도 매번 놓음");b.Before(av2,run,"두 집게 모두 잡힌 뒤 용접")
    if c.Variant="two-robots" then
        let peer=b.Work("반대쪽용접",f,None)
        s.Works.[peer].TokenRole<-TokenRole.Ignore
        b.Edge(clamp,peer,ArrowType.Group);b.Edge(sink,peer,ArrowType.Reset)
        let sys2=b.System("두번째용접장치",false)
        let fl2=b.Flow("동작",sys2)
        let r2,c2=b.Operation(peer,sys2,fl2,"다른면용접",ms 35,true,ActionType.Normal None)
        let p2,cp2=b.Operation(peer,sys2,fl2,"다음준비",None,false,ActionType.Virtual)
        b.Edge(r2,p2,ArrowType.ResetReset);b.Edge(cp2,c2,ArrowType.Start)
        b.Condition(c2,ConditionType.AutoAux,false,[af],false)
        b.Condition(cr,ConditionType.AutoAux,false,[s.Calls.[c2].ApiCalls.[0]],false)
        b.Count(r2,c.Rounds,"두 로봇 각각 새 용접");b.Before(r2,ret,"두 로봇 모두 끝난 뒤 놓기")
    if c.Delay>0 then
        let permission=b.Fact(ca,cyl,"외부준비",adv)
        b.Condition(cc,ConditionType.AutoAux,false,[permission],false)
        b.Channel(permission,[|"true"|],"false",c.Delay,"제품마다 늦게 도착하는 준비 신호")
        b.Rule("admission-delay",run,Guid.Empty,c.Delay,"준비 신호 도착 전에는 용접하지 않음")
    b.Note "제품은 이전 제품 종료와 전체 제품표 비움을 확인한 시험 공급기가 투입합니다. 장치 센서값은 Simulation이 생성합니다."
    b.Finish([entry],[sink],[f])

let selection (c:Case) =
    let b=Builder c
    let s=b.Store
    let line=b.System("조건별공정",true)
    let f=b.Flow("같은제품",line)
    let entry=b.Work("제품받기",f,None)
    let read=b.Work("사양판정받기",f,None)
    let carrier=b.Work("제품표전달",f,ms 1)
    let sink=b.Work("제품끝",f,None)
    s.Works.[entry].TokenRole<-TokenRole.Source;s.Works.[sink].TokenRole<-TokenRole.Sink
    b.Edge(entry,read,ArrowType.Start);b.Edge(read,carrier,ArrowType.Start);b.Edge(carrier,sink,ArrowType.Start)
    for w in [entry;read;carrier] do b.Edge(sink,w,ArrowType.Reset)
    b.Edge(entry,sink,ArrowType.Reset)
    let sys=b.System("검사설정장치",false)
    let df=b.Flow("값과처리",sys)
    let prep,cp=b.Operation(read,sys,df,"다음준비",None,false,ActionType.Virtual)
    let accept,cc=b.Operation(read,sys,df,"이번값받기",ms 10,true,ActionType.Virtual)
    b.Edge(prep,accept,ArrowType.ResetReset);b.Edge(cp,cc,ArrowType.Start)
    let fact=b.Fact(cc,sys,"이번값",accept)
    let ready=b.Fact(cc,sys,"이번값도착",accept)
    b.Channel(fact,[|"true";"true";"false";"false"|],"false",max 20 c.Delay,"제품별 사양 또는 OK/NG 판정")
    b.Channel(ready,[|"true"|],"false",max 20 c.Delay,"이번 응답의 도착 여부")
    b.Condition(cc,ConditionType.AutoAux,false,[ready],false)
    let callMode=c.Variant="call" || c.Variant="mixed" || c.Variant="setting"
    let a=if callMode then carrier else b.Work("A기능",f,None)
    let bw=if callMode then carrier else b.Work("B기능",f,None)
    if callMode then s.Works.[carrier].Duration<-None
    else
        for w in [a;bw] do
            s.Works.[w].TokenRole<-TokenRole.Ignore
            b.Edge(carrier,w,ArrowType.Group);b.Edge(sink,w,ArrowType.Reset)
    let wa,ca=b.Operation(a,sys,df,"A처리",ms 20,true,ActionType.Normal None)
    let wb,cb=b.Operation(bw,sys,df,"B처리",ms 30,true,ActionType.Normal None)
    for w in [wa;wb] do b.Edge(prep,w,ArrowType.Reset)
    b.Condition((if callMode then ca else a),ConditionType.SkipAction,not callMode,[fact],true)
    b.Condition((if callMode then cb else bw),ConditionType.SkipAction,not callMode,[fact],false)
    if callMode then b.Edge(ca,cb,ArrowType.Start)
    if c.Id=86 then
        let release,releaseCall=b.Operation(carrier,sys,df,"공통해제",ms 15,true,ActionType.Virtual)
        b.Edge(cb,releaseCall,ArrowType.Start);b.Edge(prep,release,ArrowType.Reset)
        b.Count(release,c.Rounds,"OK와 NG 모두 공통 해제를 실제 실행")
    b.Count(accept,c.Rounds,"값을 받는 단계는 모든 제품에서 실행")
    b.Count(wa,2,"같은 A 또는 OK가 연속되어도 두 번 실제 실행")
    b.Count(wb,c.Rounds-2,"B 또는 NG 제품에서만 실제 실행")
    b.Rule("selected-sequence",wa,wb,0,"A A B B 결과와 실제 대상 실행 일치")
    b.Before(accept,carrier,"이번 응답을 받은 뒤에만 선택 시작")
    if c.Variant="mixed" then
        let extra=b.Work("사양별추가기능",f,None)
        s.Works.[extra].TokenRole<-TokenRole.Ignore
        b.Edge(carrier,extra,ArrowType.Group);b.Edge(sink,extra,ArrowType.Reset)
        b.Condition(extra,ConditionType.SkipAction,true,[fact],true)
        let wx,cx=b.Operation(extra,sys,df,"추가검사",ms 15,true,ActionType.Normal None)
        b.Edge(prep,wx,ArrowType.Reset)
        b.Count(wx,2,"Work 선택과 내부 Call 선택 함께 실행")
    b.Note "시험 입력은 A A B B 또는 OK OK NG NG입니다. 제품마다 도착 플래그를 먼저 지우고 지정 지연 뒤 값과 도착을 주입합니다. 미확정은 선택 앞에서 대기합니다."
    b.Finish([entry],[sink],[f])

let repeatModel (c:Case) =
    let b=Builder c
    let s=b.Store
    let line=b.System("제품공정",true)
    let f=b.Flow("제품한개",line)
    let entry=b.Work("전체작업",f,None)
    let sink=b.Work("제품끝",f,ms 2)
    s.Works.[entry].TokenRole<-TokenRole.Source;s.Works.[sink].TokenRole<-TokenRole.Sink
    b.Edge(entry,sink,ArrowType.StartReset);b.Edge(entry,sink,ArrowType.Reset)
    let routine=b.System("반복기능",false)
    let rf=b.Flow("전체동작",routine)
    let prep,cp=b.Operation(entry,routine,rf,"다음준비",None,false,ActionType.Virtual)
    let whole,cc=b.Operation(entry,routine,rf,"모든위치작업",None,false,ActionType.Virtual)
    b.Edge(prep,whole,ArrowType.ResetReset);b.Edge(cp,cc,ArrowType.Start)
    let tool=b.System("작업장치",false)
    let tf=b.Flow("위치별동작",tool)
    let n=if c.Variant="two" then 2 else 3
    let ops=[for i in 1..n -> b.Operation(whole,tool,tf,sprintf "위치%d" i,ms(15+i*10),true,ActionType.Normal None)]
    for (w1,c1),(w2,c2) in List.pairwise ops do
        b.Edge(c1,c2,ArrowType.Start);b.Edge(w1,w2,ArrowType.ResetReset)
        b.Before(w1,w2,"앞 위치 완료 뒤 다음 위치")
    for w,_ in ops do b.Count(w,c.Rounds,"제품마다 해당 위치를 새로 실행")
    b.Count(whole,c.Rounds,"전체 동작도 새로 시작")
    b.Note "고정된 위치 수를 모델 안의 Call 순서로 표현합니다. 제품 공급만 외부 시험기가 맡고 위치별 횟수와 완료는 모델이 처리합니다."
    b.Finish([entry],[sink],[f])

let shared (c:Case) =
    let b=Builder c
    let s=b.Store
    let line=b.System("여러제품",true)
    let dev=b.System("공유로봇",false)
    let df=b.Flow("공용동작",dev)
    let n=if c.Variant="two" then 2 else 3
    let flows=[for i in 1..n -> b.Flow(sprintf "제품자리%d" i,line)]
    let req=flows |> List.mapi(fun i f->b.Work(sprintf "제품받기%d" i,f,ms 1))
    let useW=flows |> List.mapi(fun i f->b.Work(sprintf "로봇사용%d" i,f,None))
    let doneW=flows |> List.mapi(fun i f->b.Work(sprintf "반납끝%d" i,f,None))
    let res=[for i in 1..n -> b.Work(sprintf "요청%d가공" i,df,ms (if c.Id=54 then 60 else 40+i*5))]
    let release=b.Work("공동반납",df,ms (if c.Id=54 then 199 else 25))
    s.UpdateWorkIsFinished(release,true) |> ignore
    let turns=if c.Id=57 then [for i in 0..n-1 -> b.Work(sprintf "차례%d" i,df,ms 1)] else []
    if not turns.IsEmpty then
        s.UpdateWorkIsFinished(turns.[0],true) |> ignore
        for i in 0..n-1 do for j in i+1..n-1 do b.Edge(turns.[i],turns.[j],ArrowType.ResetReset)
    for i in 0..n-1 do
        s.Works.[req.[i]].TokenRole<-TokenRole.Source;s.Works.[doneW.[i]].TokenRole<-TokenRole.Sink
        b.Edge(req.[i],useW.[i],ArrowType.Start);b.Edge(useW.[i],doneW.[i],ArrowType.Start)
        b.Edge(doneW.[i],req.[i],ArrowType.Reset);b.Edge(doneW.[i],useW.[i],ArrowType.Reset);b.Edge(useW.[i],doneW.[i],ArrowType.Reset)
        b.Edge(release,res.[i],ArrowType.Reset);b.Edge(res.[i],release,ArrowType.Reset)
    for i in 0..n-1 do for j in i+1..n-1 do b.Edge(res.[i],res.[j],ArrowType.ResetReset)
    for i in 0..n-1 do
        let rd=b.Def(dev,sprintf "사용%d" i,Some res.[i],res.[i],ActionType.Virtual,SensingType.Normal None)
        let fd=b.Def(dev,sprintf "반납%d" i,Some release,release,ActionType.Virtual,SensingType.Normal None)
        let rc=b.Call(useW.[i],rd,false)
        let fc=b.Call(doneW.[i],fd,false)
        if not turns.IsEmpty then
            let td=b.Def(dev,sprintf "다음차례%d" i,Some turns.[(i+1)%n],turns.[(i+1)%n],ActionType.Virtual,SensingType.Normal None)
            let tc=b.Call(doneW.[i],td,false)
            b.Edge(fc,tc,ArrowType.Start)
            b.Condition(rc,ConditionType.AutoAux,false,[b.Fact(rc,dev,sprintf "내차례%d" i,turns.[i])],false)
        let fact=b.Fact(rc,dev,sprintf "반납확인%d" i,release)
        b.Condition(rc,ConditionType.AutoAux,false,[fact],false)
        b.Count(res.[i],c.Rounds,"제품별 독립 요청 수만큼 로봇 새 실행")
        b.Rule("resource-exclusion",res.[i],release,0,"다른 가공과 반납 중에는 새 가공 금지")
    b.Count(release,n*c.Rounds,"각 사용 뒤 반납을 새로 실행")
    if c.Id=57 then b.Rule("fixed-order",Guid.Empty,Guid.Empty,0,"모델 차례 Work가 1 2 3 순서를 반복")
    b.Rule("max-products",Guid.Empty,Guid.Empty,n,"세 제품 자리의 동시 보유")
    b.Note "각 Flow의 이전 제품 종료와 비움을 시험 공급기가 확인합니다. 초기 반납 완료는 새 물리 실행으로 세지 않습니다. 자원 차례는 관찰 결과이며 일반 FIFO 보장은 아닙니다."
    b.Finish(req,doneW,flows)

let transport (c:Case) =
    let b=Builder c
    let s=b.Store
    let line=b.System("제품이송",true)
    let f1=b.Flow("출발자리",line)
    let f2=b.Flow("도착자리",line)
    let a=b.Work("제품받기",f1,ms 2)
    let move=b.Work("제품넘기기",f1,None)
    let receive=b.Work("받은제품확인",f2,ms 10)
    let sink=b.Work("제품끝",f2,ms 2)
    s.Works.[a].TokenRole<-TokenRole.Source;s.Works.[sink].TokenRole<-TokenRole.Sink
    b.Edge(a,move,ArrowType.Start);b.Edge(move,receive,ArrowType.Start);b.Edge(receive,sink,ArrowType.Start)
    for w in [a;move;receive] do b.Edge(sink,w,ArrowType.Reset)
    b.Edge(a,sink,ArrowType.Reset)
    let device=b.System((if c.Id=44 then "승강대" else "이송장치"),false)
    let df=b.Flow("이동동작",device)
    let go,cg=b.Operation(move,device,df,"이동",ms 45,true,ActionType.Normal None)
    let ret,cr=b.Operation(move,device,df,"다음준비",None,false,ActionType.Virtual)
    b.Edge(go,ret,ArrowType.ResetReset);b.Edge(cr,cg,ArrowType.Start)
    b.Count(go,c.Rounds,"제품마다 새 이송")
    b.Before(go,receive,"실제 이송 완료 뒤 도착 Work 시작")
    b.Rule("token-transfer",move,receive,c.Rounds,"같은 제품 번호를 출발에서 도착으로 전달")
    if c.Id=19 then
        for i in 1..c.Rounds do
            s.Projects.[b.Project].TokenSpecs.Add {Id=1000+i;Label=sprintf "패널P%02d" i;WorkId=Some a;Fields=Map.ofList ["제품번호",sprintf "P%02d" i;"캐리어번호","C1";"로트번호","L7"]}
        b.Rule("identity-fields",a,Guid.Empty,0,"제품 번호와 같은 운반대·로트 번호를 따로 저장")
    b.Note "두 자리 사이의 같은 제품 전달을 검증합니다. 시험 공급은 전체 경로가 빈 뒤 수행하므로 연속 제품의 파이프라인 중첩 처리량을 주장하지 않습니다."
    b.Finish([a],[sink],[f1;f2])

let device (c:Case) =
    let b=Builder c
    let s=b.Store
    let line=b.System("장치요청",true)
    let f=b.Flow("제품",line)
    let a=b.Work("이번제품동작",f,None)
    let sink=b.Work("제품끝",f,ms 1)
    s.Works.[a].TokenRole<-TokenRole.Source;s.Works.[sink].TokenRole<-TokenRole.Sink
    b.Edge(a,sink,ArrowType.StartReset);b.Edge(a,sink,ArrowType.Reset)
    let dev=b.System("피제어장치",false)
    let df=b.Flow("실제동작",dev)
    let action=match c.Variant with "pulse"->ActionType.Pulse(Some 5) | "latch" | "spring" ->ActionType.Latch | "normal-delay"->ActionType.Normal(Some 8) | _->ActionType.Normal None
    let run,cr=b.Operation(a,dev,df,"동작",ms 40,true,action)
    let ret,ct=b.Operation(a,dev,df,"복귀",ms 20,true,(if c.Variant="spring" then ActionType.Virtual else ActionType.Normal None))
    b.Edge(run,ret,ArrowType.ResetReset);b.Edge(cr,ct,ArrowType.Start)
    if c.Variant="sensing-delay" then
        let ad=s.Calls.[cr].ApiCalls.[0].ApiDefId.Value
        s.ApiDefs.[ad].SensingType<-SensingType.Normal(Some 10)
    b.Count(run,c.Rounds,"제품마다 실제 동작 새 시작")
    b.Count(ret,c.Rounds,"제품마다 실제 복귀 새 시작")
    b.Before(run,ret,"동작 완료 뒤 복귀")
    b.Rule("output-observed",s.Calls.[cr].ApiCalls.[0].Id,Guid.Empty,0,"요청 출력의 켜짐과 꺼짐 기록")
    let api=s.Calls.[cr].ApiCalls.[0].Id
    if c.Variant="pulse" then b.Rule("output-duration",api,Guid.Empty,5,"요청 출력 폭은 5 ms, 실제 동작은 40 ms")
    if c.Variant="normal-delay" then b.Rule("output-duration",api,Guid.Empty,48,"40 ms 완료 뒤 8 ms를 더 유지")
    if c.Variant="sensing-delay" then b.Rule("sense-delay",cr,run,10,"대상 완료 입력이 10 ms 안정된 뒤 Call 완료")
    b.Note "주소는 실제 PLC에 연결되지 않은 모의 입출력입니다. Simulation의 대상 완료와 메모리 출력 변화를 검증하며 실제 회로와 Control 운전은 별도입니다."
    b.Finish([a],[sink],[f])

let create c =
    match c.Family with
    | "jig" -> jig c
    | "selection" -> selection c
    | "repeat" -> repeatModel c
    | "shared" -> shared c
    | "transport" -> transport c
    | "device" -> device c
    | x -> failwithf "Unknown family: %s" x
