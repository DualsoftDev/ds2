open System
open System.IO
open System.Text.Json
open Ds2.Core
open Ds2.Editor
open TutorialModels
let removeCondition bp callName kind =
    let c=bp.Store.Calls.Values |> Seq.find(fun x->x.ApiName=callName)
    let rows=c.Conditions |> Seq.filter(fun x->x.Type=Some kind) |> Seq.toArray
    require "one intended condition removed" (rows.Length=1)
    c.Conditions.Remove rows.[0] |> ignore
    bp
let negative bp =
    let changed,description =
        match bp.Case.Id with
        | 22 | 95 ->removeCondition bp "용접" ConditionType.AutoAux,"용접 Call의 잡기 완료 AutoAux 한 개 제거"
        | 35 ->removeCondition bp "이번값받기" ConditionType.AutoAux,"이번 값 도착 AutoAux 한 개 제거"
        | 52 ->
            let c=bp.Store.Calls.Values |> Seq.find(fun x->x.ApiName="사용1")
            let row=c.Conditions |> Seq.find(fun x->x.Type=Some ConditionType.AutoAux)
            c.Conditions.Remove row |> ignore
            bp,"두 번째 사용자 Call의 반납 확인 한 개 제거"
        | 64 ->
            let c=bp.Store.Calls.Values |> Seq.find(fun x->x.ApiName="반복2")
            let row=c.Conditions |> Seq.find(fun x->x.Type=Some ConditionType.SkipAction)
            row.IsInverted<-false
            bp,"두 번째 반복 Call의 SkipAction 방향만 뒤집음"
        | _ ->failwith "No authored negative case"
    {changed with Notes=Array.append changed.Notes [|"반례: "+description|]}
[<EntryPoint>]
let main args =
    Console.OutputEncoding<-System.Text.Encoding.UTF8
    if args.Length=3 && args.[0]="--probes" then TutorialProbes.run args.[1] args.[2]
    elif args.Length<2 then
        eprintfn "Usage: Ds2.TutorialVerification catalog.json output [ids] [--replay saved-root] [--negative]"
        2
    else
        let catalog=args.[0]
        let output=args.[1]
        let requested=if args.Length>2 && not(args.[2].StartsWith("--")) then args.[2].Split(',') |> Array.map int |> Set.ofArray else Set.empty
        let source=Array.tryFindIndex((=)"--replay") args |> Option.map(fun i->args.[i+1])
        let isNegative=Array.contains "--negative" args
        let cases=JsonSerializer.Deserialize<Case array>(File.ReadAllText catalog)
        let mutable ok=true
        for c in cases do
            if requested.IsEmpty || requested.Contains c.Id then
                try
                    let bp=
                        match source with
                        | Some path->TutorialVerify.replay path c
                        | None ->if ["jig";"selection";"repeat";"shared";"transport";"device"] |> List.contains c.Family then create c else TutorialAdvanced.extra c
                    if source.IsNone then
                        for system in bp.Store.Systems.Values |> Seq.toArray do
                            bp.Store.MoveEntities(EditorCanvasLayout.computeAutoLayout bp.Store TabKind.System system.Id) |> ignore
                        // The stock cycle layout can overlap Group members and a Sink.
                        // Preserve its ordering, moving only overlapping boxes down.
                        for system in bp.Store.Systems.Values |> Seq.toArray do
                            let placed=System.Collections.Generic.List<Xywh>()
                            let works=bp.Store.Works.Values |> Seq.filter(fun w->bp.Store.Flows.[w.ParentId].ParentId=system.Id) |> Seq.sortBy(fun w->let p=w.Position.Value in p.Y,p.X,w.Name) |> Seq.toArray
                            for work in works do
                                let p=work.Position.Value
                                let mutable y=p.Y
                                let mutable overlap=true
                                while overlap do
                                    let collisions=placed |> Seq.filter(fun q->p.X<q.X+q.W+24 && q.X<p.X+p.W+24 && y<q.Y+q.H+32 && q.Y<y+p.H+32) |> Seq.toArray
                                    overlap<-collisions.Length>0
                                    if overlap then y<-collisions |> Array.map(fun q->q.Y+q.H+48) |> Array.max
                                let position=Xywh(p.X,y,p.W,p.H)
                                work.Position<-Some position
                                placed.Add position
                        for work in bp.Store.Works.Values |> Seq.toArray do
                            bp.Store.MoveEntities(EditorCanvasLayout.computeAutoLayout bp.Store TabKind.Work work.Id) |> ignore
                    let candidate=if isNegative then negative bp else bp
                    let pass=TutorialVerify.verify output candidate
                    if isNegative then
                        printfn "NEGATIVE %03d rejected=%b" c.Id (not pass)
                        if pass then ok<-false
                    elif not pass then ok<-false
                with e ->
                    ok<-false
                    printfn "ERROR %03d: %s" c.Id (e.ToString())
        if ok then 0 else 1
