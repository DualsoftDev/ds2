#r @"C:\ds\ds2\Solutions\Core\Ds2.Core\bin\Debug\net9.0\Ds2.Core.dll"
let s : Ds2.Core.Store.DsStore = Ds2.Serialization.JsonConverter.loadFromFile @"D:\dstest\demoKit\out_v18_fsharp\DEMO_v18.sdf"
printfn "Flows=%d Works=%d Calls=%d arrowCalls=%d arrowWorks=%d"
    s.Flows.Count s.Works.Count s.Calls.Count s.ArrowCalls.Count s.ArrowWorks.Count
printfn ""
printfn "=== arrowWorks ==="
for kv in s.ArrowWorks do
    let a = kv.Value
    let sw = if s.Works.ContainsKey a.SourceId then s.Works.[a.SourceId].Name else "?"
    let tw = if s.Works.ContainsKey a.TargetId then s.Works.[a.TargetId].Name else "?"
    printfn "  %s -> %s [type=%A]" sw tw a.ArrowType
printfn ""
printfn "=== arrowCalls (work별) ==="
let byWork = s.ArrowCalls.Values |> Seq.groupBy (fun a -> a.ParentId)
for (wid, arrs) in byWork do
    let wname = if s.Works.ContainsKey wid then s.Works.[wid].Name else "?"
    let arrows = arrs |> List.ofSeq
    printfn "  [%s] %d arrows" wname arrows.Length
    for a in arrows do
        let sn = if s.Calls.ContainsKey a.SourceId then s.Calls.[a.SourceId].Name else "?"
        let tn = if s.Calls.ContainsKey a.TargetId then s.Calls.[a.TargetId].Name else "?"
        printfn "    %s -> %s [type=%A]" sn tn a.ArrowType
