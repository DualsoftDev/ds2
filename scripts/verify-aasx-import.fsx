// 변환된 AASX 가 현재 Promaker 로 정상 import 되는지 검증.
//   dotnet fsi scripts/verify-aasx-import.fsx <AASX 경로>

#r "../Apps/Tutorial/bin/Debug/net9.0/Ds2.Aasx.dll"
#r "../Apps/Tutorial/bin/Debug/net9.0/Ds2.Core.dll"
#r "../Apps/Tutorial/bin/Debug/net9.0/AasCore.Aas3_1.dll"

open System
open System.IO
open System.Diagnostics
open Ds2.Aasx
open Ds2.Core.Store

Trace.Listeners.Add(new ConsoleTraceListener()) |> ignore

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length < 1 then exit 2
let path = args.[0]

let store = DsStore()
AasxImporter.importIntoStoreOrRaise store path

printfn "[Verify] %s" path
printfn "  Projects = %d"   store.Projects.Count
printfn "  Systems  = %d"   store.Systems.Count
printfn "  Flows    = %d"   store.Flows.Count
printfn "  Works    = %d"   store.Works.Count
printfn "  Calls    = %d"   store.Calls.Count
printfn "  ApiCalls = %d"   store.ApiCalls.Count
