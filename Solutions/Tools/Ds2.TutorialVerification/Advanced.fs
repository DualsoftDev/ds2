module TutorialAdvanced
open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open TutorialModels

let setting (c:Case) =
    let b=Builder c
    let s=b.Store
    let line=b.System("설정후가공",true)
    let f=b.Flow("제품",line)
    let a=b.Work("설정과가공",f,None)
    let sink=b.Work("제품끝",f,ms 2)
    s.Works.[a].TokenRole<-TokenRole.Source;s.Works.[sink].TokenRole<-TokenRole.Sink
    b.Edge(a,sink,ArrowType.StartReset);b.Edge(a,sink,ArrowType.Reset)
    let req=b.System("제품정보",false)
    let rf=b.Flow("정보받기",req)
    let fp,cf=b.Operation(a,req,rf,"정보준비",None,false,ActionType.Virtual)
    let get,cg=b.Operation(a,req,rf,"정보받기",ms 15,true,ActionType.Virtual)
    b.Edge(fp,get,ArrowType.ResetReset)
    let dev=b.System((if c.Id=82 then "도포장치" else "설정가공장치"),false)
    let df=b.Flow("설정상태와가공",dev)
    let dp,cd=b.Operation(a,dev,df,"가공준비",None,false,ActionType.Virtual)
    let proc,cp=b.Operation(a,dev,df,(if c.Id=82 then "도포실행" else "가공실행"),ms 40,true,ActionType.Normal None)
    let s21,c21=b.Operation(a,dev,df,"설정21",ms 20,true,ActionType.Normal None)
    let s22,c22=b.Operation(a,dev,df,"설정22",ms 20,true,ActionType.Normal None)
    s.UpdateWorkIsFinished(s22,true) |> ignore
    b.Edge(dp,proc,ArrowType.ResetReset);b.Edge(s21,s22,ArrowType.ResetReset)
    for x,y in [cf,cd;cd,cg;cg,c21;c21,c22;c22,cp] do b.Edge(x,y,ArrowType.Start)
    let wanted=b.Fact(cg,req,"요구값",get)
    wanted.InputSpec<-Int32Value(Single 21)
    let ready=b.Fact(cg,req,"정보도착",get)
    b.Channel(wanted,[|"21";"21";"22";"22"|],"0",25,"이번 제품에 적용할 설정 번호")
    b.Channel(ready,[|"true"|],"false",25,"이번 정보가 도착함")
    b.Condition(cg,ConditionType.AutoAux,false,[ready],false)
    for call,k,w in [c21,21,s21;c22,22,s22] do
        let mismatch=wanted.DeepCopy()
        mismatch.Id<-wanted.Id;mismatch.InputSpec<-Int32Value(Single k);mismatch.ContactKind<-ContactKind.NcContact
        let current=b.Fact(call,dev,sprintf "현재%d" k,w)
        b.Condition(call,ConditionType.SkipAction,false,[mismatch;current],false)
        let row=s.Calls.[call].Conditions |> Seq.find(fun x->x.Type=Some ConditionType.SkipAction)
        row.IsOR<-true
    b.Count(s21,1,"처음 21로 바꿀 때만 실제 설정")
    b.Count(s22,1,"22로 바꿀 때만 실제 설정")
    b.Count(proc,4,"설정이 같아도 실제 가공은 제품마다 실행")
    b.Rule("setting-at-start",proc,s21,0,"가공 시작 당시 실제 설정은 21 21 22 22")
    b.Note "요구 설정값과 새 정보 도착 신호를 시험기가 공급합니다. 현재 설정은 두 Work의 상태로 모델 안에 보존하며, 같은 값의 연속 제품에서 설정만 생략하고 가공은 반복합니다."
    b.Finish([a],[sink],[f])

let variable (c:Case) =
    let b=Builder c
    let s=b.Store
    let line=b.System("횟수별작업",true)
    let f=b.Flow("제품한개",line)
    let a=b.Work("횟수받고작업",f,None)
    let sink=b.Work("제품끝",f,ms 2)
    s.Works.[a].TokenRole<-TokenRole.Source;s.Works.[sink].TokenRole<-TokenRole.Sink
    b.Edge(a,sink,ArrowType.StartReset);b.Edge(a,sink,ArrowType.Reset)
    let dev=b.System("작업장치",false)
    let df=b.Flow("최대세회작업",dev)
    let prep,cp=b.Operation(a,dev,df,"다음준비",None,false,ActionType.Virtual)
    let read,cr=b.Operation(a,dev,df,"횟수받기",ms 10,true,ActionType.Virtual)
    b.Edge(prep,read,ArrowType.ResetReset);b.Edge(cp,cr,ArrowType.Start)
    let count=b.Fact(cr,dev,"목표횟수",read)
    count.InputSpec<-Int32Value(Single 1)
    let ready=b.Fact(cr,dev,"횟수도착",read)
    b.Channel(count,[|"1";"2";"3"|],"0",20,"이번 제품의 목표 횟수")
    b.Channel(ready,[|"true"|],"false",20,"목표 횟수 준비 완료")
    b.Condition(cr,ConditionType.AutoAux,false,[ready],false)
    let mutable previous=cr
    for n in 1..3 do
        let w,call=b.Operation(a,dev,df,sprintf "반복%d" n,ms 20,true,ActionType.Normal None)
        b.Edge(previous,call,ArrowType.Start);b.Edge(prep,w,ArrowType.Reset)
        let fact=count.DeepCopy()
        fact.Id<-count.Id;fact.InputSpec<-Int32Value(Multiple [n..3])
        b.Condition(call,ConditionType.SkipAction,false,[fact],true)
        b.Count(w,4-n,sprintf "%d번째 동작의 실제 실행 수" n)
        previous<-call
    b.Note "실행 가능한 횟수를 1~3으로 제한해 Call 세 개와 SkipAction으로 표현합니다. 임의로 큰 N을 처리하는 범용 카운터·루프 구현이라고 주장하지 않습니다. 입력 N만 외부에서 공급하며 수행 횟수 선택은 모델 내부입니다."
    b.Finish([a],[sink],[f])

let routing (c:Case) =
    let b=Builder c
    let s=b.Store
    let line=b.System("분기이송",true)
    let f=b.Flow("분기전제품",line)
    let lf=b.Flow("왼쪽배출",line)
    let rf=b.Flow("오른쪽배출",line)
    let a=b.Work("판정과경로준비",f,None)
    let left=b.Work("왼쪽제품끝",lf,ms 10)
    let right=b.Work("오른쪽제품끝",rf,ms 10)
    s.Works.[a].TokenRole<-TokenRole.Source
    for w in [left;right] do
        s.Works.[w].TokenRole<-TokenRole.Sink
        s.UpdateWorkIsFinished(w,true) |> ignore
        b.Edge(a,w,ArrowType.Start);b.Edge(w,a,ArrowType.Reset)
    let ctrlFlow=b.Flow("받을경로준비",line)
    let reset=b.Work("경로선택준비",ctrlFlow,None)
    let openL=b.Work("왼쪽열기",ctrlFlow,None)
    let openR=b.Work("오른쪽열기",ctrlFlow,None)
    for w,t in [openL,left;openR,right] do
        b.Edge(w,t,ArrowType.Reset);b.Edge(reset,w,ArrowType.Reset);b.Edge(w,reset,ArrowType.Reset)
    let selector=b.System("경로선택기능",false)
    let sf=b.Flow("사양에따른선택",selector)
    let prep,cp=b.Operation(a,selector,sf,"선택기준비",None,false,ActionType.Virtual)
    let select,cs=b.Operation(a,selector,sf,"이번경로선택",None,false,ActionType.Virtual)
    b.Edge(prep,select,ArrowType.ResetReset);b.Edge(cp,cs,ArrowType.Start)
    let mk owner name target =
        b.Call(owner,b.Def(line,name,Some target,target,ActionType.Virtual,SensingType.Normal None),false)
    let resetCall=mk prep "경로준비" reset
    let callL=mk select "왼쪽허용" openL
    let callR=mk select "오른쪽허용" openR
    b.Edge(callL,callR,ArrowType.Start)
    let fact=b.Fact(resetCall,line,"왼쪽선택",reset)
    let ready=b.Fact(resetCall,line,"판정도착",reset)
    b.Channel(fact,(if c.Family="rework" then [|"true"|] else [|"true";"true";"false";"false"|]),"false",20,"이번 제품의 배출 방향")
    b.Channel(ready,[|"true"|],"false",20,"배출 판정 도착")
    b.Condition(cs,ConditionType.AutoAux,false,[ready],false)
    b.Condition(callL,ConditionType.SkipAction,false,[fact],true)
    b.Condition(callR,ConditionType.SkipAction,false,[fact],false)
    b.Count(openL,(if c.Family="rework" then c.Rounds else 2),"왼쪽 경로를 요청마다 새로 열기")
    b.Count(openR,(if c.Family="rework" then c.Rounds else 2),"오른쪽 경로를 요청마다 새로 열기")
    if c.Family="rework" then
        s.Works.[left].TokenRole<-TokenRole.None
        s.Works.[left].LocalName<-"재작업후돌아오기"
        b.Edge(left,a,ArrowType.Start)
        b.Rule("rework-switch",left,fact.Id,c.Rounds,"재작업 완료 뒤 이번 판정을 통과로 갱신")
        b.Rule("token-transfer",left,a,c.Rounds,"새 번호 없이 같은 제품이 재작업에서 돌아옴")
    else b.Rule("destination-sequence",left,right,0,"한 제품은 선택한 한 목적지에서만 종료")
    b.Note "두 배출 Work는 처음에 닫힌 완료 상태입니다. 이번 선택이 한 목적지만 Reset해 받을 수 있게 하며, 제품 전달 전에 경로 준비 요청이 완료됩니다. 방향 값과 응답 도착은 외부 입력입니다."
    b.Finish([a],(if c.Family="rework" then [right] else [left;right]),[f;lf;rf])

let rotation (c:Case) =
    let b=Builder c
    let s=b.Store
    let line=b.System("회전설비",true)
    let dev=b.System("공동회전축",false)
    let fa=b.Flow("받침A",line)
    let fb=b.Flow("받침B",line)
    let df=b.Flow("축과준비",dev)
    let a0=b.Work("A구동역할",fa,None)
    let b0=b.Work("B관찰역할",fb,None)
    let a1=b.Work("B구동역할",fb,None)
    let b1=b.Work("A관찰역할",fa,None)
    s.Works.[a0].TokenRole<-TokenRole.Source;s.Works.[b0].TokenRole<-TokenRole.Source
    for x,y in [a0,b1;b0,a1;a1,b0;b1,a0] do b.Edge(x,y,ArrowType.Start)
    for x,y in [a1,a0;a1,b0;a0,a1;a0,b1] do b.Edge(x,y,ArrowType.Reset)
    let prep=b.Work("축다음준비",df,None)
    let axis=b.Work("한번회전",df,ms 40)
    let r0=b.Work("양쪽준비0",df,ms 5)
    let r1=b.Work("양쪽준비1",df,ms 5)
    b.Edge(prep,axis,ArrowType.ResetReset);b.Edge(r0,r1,ArrowType.ResetReset)
    let pd=b.Def(dev,"축준비",Some prep,prep,ActionType.Virtual,SensingType.Normal None)
    let md=b.Def(dev,"회전",Some axis,axis,ActionType.Pulse(Some 5),SensingType.Normal None)
    let od=b.Def(dev,"회전관찰",None,axis,ActionType.Virtual,SensingType.Normal None)
    let rd0=b.Def(dev,"준비0",Some r0,r0,ActionType.Virtual,SensingType.Normal None)
    let rd1=b.Def(dev,"준비1",Some r1,r1,ActionType.Virtual,SensingType.Normal None)
    let c0p=b.Call(a0,pd,false)
    let c0m=b.Call(a0,md,true)
    let c0r=b.Call(b0,rd0,false)
    let c0o=b.Call(b0,od,true)
    let c1p=b.Call(a1,pd,false)
    let c1m=b.Call(a1,md,true)
    let c1r=b.Call(b1,rd1,false)
    let c1o=b.Call(b1,od,true)
    for call in [c0o;c1o] do s.UpdateCallType(call,CallType.SkipIfCompleted) |> ignore
    for x,y in [c0p,c0m;c0r,c0o;c1p,c1m;c1r,c1o] do b.Edge(x,y,ArrowType.Start)
    let permit=b.Fact(c0m,dev,"회전허가",prep)
    b.Condition(c0p,ConditionType.AutoAux,false,[b.Fact(c0m,dev,"양쪽도착0",r0);permit],false)
    b.Condition(c1p,ConditionType.AutoAux,false,[b.Fact(c0m,dev,"양쪽도착1",r1);permit],false)
    b.Condition(c0o,ConditionType.AutoAux,false,[b.Fact(c0m,line,"A구동완료",a0)],false)
    b.Condition(c1o,ConditionType.AutoAux,false,[b.Fact(c0m,line,"B구동완료",a1)],false)
    for w in [axis;a1;b1] do s.UpdateWorkIsFinished(w,true) |> ignore
    b.Channel(permit,[|"true"|],"false",1,"회전 외부 허가")
    b.Count(axis,4,"같은 두 제품으로 공동 축 네 번 실행")
    b.Rule("rotation-permit",axis,permit.Id,4,"두 회 뒤 대기하고 재개한 뒤 네 회에서 멈춤")
    b.Note "받침 A/B가 각 제품을 계속 보유하는 Flow입니다. 시험기가 두 제품을 한 번만 투입하고, 두 회 뒤 허가를 닫았다 열며 네 회 뒤 닫습니다. 외부 정지 횟수를 모델 내부 카운터로 주장하지 않습니다."
    {b.Finish([a0;b0],[],[fa;fb]) with EndWithTokens=true}

let assembly (c:Case) =
    let b=Builder c
    let s=b.Store
    let line=b.System("조립단위관리",true)
    let fa=b.Flow("원부품A",line)
    let fb=b.Flow("원부품B",line)
    let fc=b.Flow("새조립품C",line)
    let a=b.Work("A받기",fa,ms 10)
    let ae=b.Work("A단위끝",fa,ms 2)
    let bb=b.Work("B받기",fb,ms 15)
    let be=b.Work("B단위끝",fb,ms 2)
    let cc=b.Work("C조립작업",fc,None)
    let ce=b.Work("C단위끝",fc,ms 2)
    for first,last in [a,ae;bb,be;cc,ce] do
        s.Works.[first].TokenRole<-TokenRole.Source;s.Works.[last].TokenRole<-TokenRole.Sink
        b.Edge(first,last,ArrowType.StartReset);b.Edge(first,last,ArrowType.Reset)
    let dev=b.System("조립장치",false)
    let df=b.Flow("조립동작",dev)
    let run,cr=b.Operation(cc,dev,df,"조립",ms 40,true,ActionType.Normal None)
    let prep,cp=b.Operation(cc,dev,df,"다음준비",None,false,ActionType.Virtual)
    b.Edge(run,prep,ArrowType.ResetReset);b.Edge(cp,cr,ArrowType.Start)
    b.Count(run,c.Rounds,"새 조립품마다 실제 조립 동작")
    for i in 1..c.Rounds do
        s.Projects.[b.Project].TokenSpecs.Add {Id=3000+i;Label=sprintf "C%02d" i;WorkId=Some cc;Fields=Map.ofList ["원부품A",string(1000+i);"원부품B",string(2000+i)]}
    b.Note "A/B 종료 후 C 번호를 투입하고 원부품 대응을 기록하는 일은 시험 공급기가 맡습니다. SDF에는 Source/Sink와 TokenSpecs 대응 정보가 있으나 엔진 자체의 자동 병합·새 번호 생성으로 검증한 것은 아닙니다."
    b.Rule("assembly-lineage",cc,Guid.Empty,0,"C의 공급은 해당 A/B 종료 뒤이며 원부품 번호를 보존")
    {b.Finish([a;bb;cc],[ae;be;ce],[fa;fb;fc]) with SupplyRequirements=[|{Source=a;Dependency=cc;Lag=1};{Source=bb;Dependency=cc;Lag=1};{Source=cc;Dependency=a;Lag=0};{Source=cc;Dependency=bb;Lag=0}|]}

let batch c =
    let bp=repeatModel {c with Variant="two"}
    let p=bp.Store.Projects.Values |> Seq.head
    for i in 1..c.Rounds do
        p.TokenSpecs.Add {Id=1000+i;Label=sprintf "운반대%d" i;WorkId=Some bp.Sources.[0];Fields=Map.ofList ["구성제품",sprintf "P%d01,P%d02,P%d03,P%d04" i i i i;"구성수","4"]}
    let rule={Kind="batch-members";A=bp.Sources.[0];B=Guid.Empty;N=4;Label="운반대 세 개와 구성 제품 열두 개를 구분"}
    {bp with Case=c;Rules=Array.append bp.Rules [|rule|];Notes=Array.append bp.Notes [|"네 부품을 실은 운반대를 한 추적 단위로 정했습니다. 구성 부품은 TokenSpecs 정보이며 별도 제품표 네 개를 자동 생성하거나 종료하는 모델은 아닙니다."|]}

let lineCell c =
    let bp=jig c
    let s=bp.Store
    let sink=bp.Sinks.[0]
    let active=s.Flows.[s.Works.[sink].ParentId].ParentId
    let next=s.AddFlow("검사배출자리",active)
    s.Works.[sink].ParentId<-next;s.Works.[sink].FlowPrefix<-"검사배출자리"
    let sys=s.AddSystem("검사장치",(s.Projects.Keys |> Seq.head),false)
    s.Systems.[sys].SystemType<-Some "Tutorial"
    let df=s.AddFlow("검사동작",sys)
    let prep=s.AddWork("검사준비",df)
    let inspect=s.AddWork("검사",df)
    s.Works.[inspect].Duration<-ms 25
    s.ConnectSelectionInOrder([prep;inspect],ArrowType.ResetReset) |> ignore
    let call name target physical =
        let d=s.AddApiDefWithProperties(name,sys)
        s.ApiDefs.[d].TxGuid<-Some target;s.ApiDefs.[d].RxGuid<-Some target
        s.ApiDefs.[d].ActionType<-ActionType.Virtual;s.ApiDefs.[d].SensingType<-SensingType.Normal None
        let id=s.AddCallWithLinkedApiDefs(sink,"검사장치",name,[d])
        let ac=s.Calls.[id].ApiCalls.[0]
        ac.InputSpec<-BoolValue(Single true)
        if physical then ac.InTag<-Some(IOTag("검사완료","SIM_INSPECT","모의 검사 완료"))
        id,ac.Id
    let cp,ap=call "준비" prep false
    let ci,_=call "검사" inspect true
    s.ConnectSelectionInOrder([cp;ci],ArrowType.Start) |> ignore
    let rule={Kind="work-count";A=inspect;B=Guid.Empty;N=c.Rounds;Label="다음 자리에서 모든 제품을 새로 검사"}
    {bp with Rules=Array.append bp.Rules [|rule|];LogicalApis=Array.append bp.LogicalApis [|ap|];ProductFlows=Array.append bp.ProductFlows [|next|];Notes=Array.append bp.Notes [|"용접 자리에서 검사·배출 자리로 같은 제품 번호를 전달합니다. 이번 시험은 제품별 직렬 공급이며 두 자리의 최대 처리량을 측정하지 않습니다."|]}

let combined c =
    let bp=lineCell c
    let s=bp.Store
    let weld=s.Calls.Values |> Seq.find(fun x->x.ApiName="용접")
    let owner=weld.ParentId
    let sys=s.AddSystem("자세회전장치",(s.Projects.Keys |> Seq.head),false)
    s.Systems.[sys].SystemType<-Some "Tutorial"
    let f=s.AddFlow("한제품자세",sys)
    let first=s.AddWork("가공자세",f)
    let home=s.AddWork("배출자세",f)
    s.Works.[first].Duration<-ms 20;s.Works.[home].Duration<-ms 15
    s.ConnectSelectionInOrder([first;home],ArrowType.ResetReset) |> ignore
    let call name w =
        let d=s.AddApiDefWithProperties(name,sys)
        s.ApiDefs.[d].TxGuid<-Some w;s.ApiDefs.[d].RxGuid<-Some w
        s.ApiDefs.[d].ActionType<-ActionType.Normal None;s.ApiDefs.[d].SensingType<-SensingType.Normal None
        let ca=s.AddCallWithLinkedApiDefs(owner,"자세회전장치",name,[d])
        let ac=s.Calls.[ca].ApiCalls.[0]
        ac.InTag<-Some(IOTag(name+"완료","SIM_"+name+"_IN","모의 입력"));ac.OutTag<-Some(IOTag(name+"요청","SIM_"+name+"_OUT","모의 출력"))
        ac.InputSpec<-BoolValue(Single true);ac.OutputSpec<-BoolValue(Single true)
        ca
    let cf=call "회전" first
    let ch=call "원위치" home
    s.ConnectSelectionInOrder([cf;weld.Id],ArrowType.Start) |> ignore
    s.ConnectSelectionInOrder([weld.Id;ch],ArrowType.Start) |> ignore
    let clamp=s.Calls.Values |> Seq.find(fun x->x.ApiName="잡기")
    let release=s.Calls.Values |> Seq.find(fun x->x.ApiName="놓기")
    s.AddConditionWithApiCalls(cf,ConditionType.AutoAux,[clamp.ApiCalls.[0].Id]) |> ignore
    s.AddConditionWithApiCalls(release.Id,ConditionType.AutoAux,[s.Calls.[ch].ApiCalls.[0].Id]) |> ignore
    let rules=[|{Kind="work-count";A=first;B=Guid.Empty;N=c.Rounds;Label="매 제품 가공 자세로 이동"};{Kind="work-count";A=home;B=Guid.Empty;N=c.Rounds;Label="매 제품 배출 자세로 돌아옴"}|]
    {bp with Rules=Array.append (bp.Rules |> Array.filter(fun r->r.Kind<>"finish-before-start" || s.Works.[r.B].LocalName<>"놓기")) rules;Notes=Array.append bp.Notes [|"패널 잡기·자세 회전·용접·원위치·놓기·검사를 연결했습니다. 재작업과 공유 로봇의 선택은 연결된 050·052 모델에서 별도로 검증합니다."|]}

let emptyCarrier c =
    let b=Builder c
    let s=b.Store
    let line=b.System("받침운반",true)
    let f=b.Flow("받침회차",line)
    let a=b.Work("받침작업과이동",f,None)
    let last=b.Work("받침회차끝",f,ms 2)
    s.Works.[a].TokenRole<-TokenRole.Source;s.Works.[last].TokenRole<-TokenRole.Sink
    b.Edge(a,last,ArrowType.StartReset);b.Edge(a,last,ArrowType.Reset)
    let dev=b.System("운반장치",false)
    let df=b.Flow("감지작업이동",dev)
    let prep,cp=b.Operation(a,dev,df,"다음준비",None,false,ActionType.Virtual)
    let read,cr=b.Operation(a,dev,df,"제품유무확인",ms 5,true,ActionType.Virtual)
    let processWork,cc=b.Operation(a,dev,df,"제품작업",ms 20,true,ActionType.Normal None)
    let move,cm=b.Operation(a,dev,df,"받침이동",ms 30,true,ActionType.Normal None)
    for w in [read;processWork;move] do b.Edge(prep,w,ArrowType.Reset)
    b.Edge(move,prep,ArrowType.Reset)
    for x,y in [cp,cr;cr,cc;cc,cm] do b.Edge(x,y,ArrowType.Start)
    let presence=b.Fact(cr,dev,"제품있음",read)
    let ready=b.Fact(cr,dev,"감지정보도착",read)
    b.Channel(presence,[|"false";"true";"false"|],"false",15,"빈 받침 / 제품 있음 / 빈 받침")
    b.Channel(ready,[|"true"|],"false",15,"이번 받침의 유무 확인")
    b.Condition(cr,ConditionType.AutoAux,false,[ready],false)
    b.Condition(cc,ConditionType.SkipAction,false,[presence],true)
    b.Count(processWork,1,"실제 제품이 있는 회차에서만 제품 작업")
    b.Count(move,3,"빈 받침도 포함해 세 회차 모두 이동")
    for i in 1..3 do
        s.Projects.[b.Project].TokenSpecs.Add {Id=1000+i;Label=sprintf "C1회차%d" i;WorkId=Some a;Fields=Map.ofList ["받침번호","C1";"제품번호",(if i=2 then "P01" else "")]}
    b.Note "여기서 Source의 추적 단위는 제품이 아니라 받침의 운반 회차입니다. 제품 없는 두 회차에 가짜 제품 번호를 발행하지 않으며, 세 회차 완료를 제품 세 개의 생산량으로 세지 않습니다. 제품은 한 개입니다."
    b.Finish([a],[last],[f])

let extra c =
    match c.Family with
    | "empty-carrier" ->emptyCarrier c
    | "batch" ->batch c
    | "assembly" ->assembly c
    | "line" ->lineCell c
    | "combined" ->combined c
    | "contrast" ->jig c
    | "rework" ->routing c
    | "setting" -> setting c
    | "variable" -> variable c
    | "routing" when c.Id=48 -> routing c
    | "routing" ->
        let bp=shared {c with Variant="two"}
        let s=bp.Store
        let line=s.Flows.[bp.ProductFlows.[0]].ParentId
        let common=s.AddFlow("한개만받는합류자리",line)
        for id in bp.Sinks do
            s.Works.[id].ParentId<-common;s.Works.[id].FlowPrefix<-"한개만받는합류자리"
        {bp with ProductFlows=Array.append bp.ProductFlows [|common|];Notes=Array.append bp.Notes [|"합류 자리는 Flow 하나이며 출발 방향별 받기 Work가 있습니다. 반납과 공유 사용 허가로 한 제품씩 도착하는지 같은 Flow의 동시 제품 수를 검증합니다."|]}
    | "rotation" -> rotation c
    | "logical" ->repeatModel c
    | "priority" ->shared c
    | "sensing" ->
        let bp=device c
        let ad=bp.Store.ApiDefs.Values |> Seq.find(fun d->d.Name="동작")
        ad.SensingType<-SensingType.Latch 10
        {bp with Notes=Array.append bp.Notes [|"이 파일은 Latch 감지의 대상 완료 입력을 검증합니다. Virtual 감지와 실제 Control 입력 변화는 별도 비교 시험 결과를 함께 확인합니다."|]}
    | _ ->failwithf "Advanced family not implemented: %s" c.Family
