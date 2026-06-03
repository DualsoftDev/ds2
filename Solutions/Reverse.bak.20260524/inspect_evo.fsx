#r @"C:\ds\ds2\Solutions\Core\Ds2.Core\bin\Debug\net9.0\Ds2.Core.dll"
let s : Ds2.Core.Store.DsStore = Ds2.Serialization.JsonConverter.loadFromFile @"D:\dstest\kwangmyeongEVO\out_v18_fsharp\EVO_v18.sdf"
printfn "Flows=%d Works=%d Calls=%d arrowCalls=%d arrowWorks=%d"
    s.Flows.Count s.Works.Count s.Calls.Count s.ArrowCalls.Count s.ArrowWorks.Count
printfn ""
printfn "=== Flows ==="
for kv in s.Flows do printfn "  %s" kv.Value.Name
printfn ""
printfn "=== Works (sample 10) ==="
for kv in s.Works |> Seq.truncate 10 do printfn "  %s" kv.Value.Name
printfn ""
let arrowsByType = s.ArrowWorks.Values |> Seq.groupBy (fun a -> a.ArrowType) |> Seq.toList
printfn "=== arrowWorks by type ==="
for (t, arrs) in arrowsByType do printfn "  %A: %d" t (Seq.length arrs)
let acByType = s.ArrowCalls.Values |> Seq.groupBy (fun a -> a.ArrowType) |> Seq.toList
printfn ""
printfn "=== arrowCalls by type ==="
for (t, arrs) in acByType do printfn "  %A: %d" t (Seq.length arrs)
