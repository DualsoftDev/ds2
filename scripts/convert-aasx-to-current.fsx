// AASX 호환성 변환: 레거시(예: AAS 3.0 / SequenceControlSubmodel) 파일을
// 현재 Promaker 가 내보내는 형식(AAS 3.1 / SequenceModel + 도메인 서브모델)으로 재저장.
//
// 실행:
//   dotnet fsi scripts/convert-aasx-to-current.fsx "<입력.aasx>" "<출력.aasx>"

#r "../Apps/Tutorial/bin/Debug/net9.0/Ds2.Aasx.dll"
#r "../Apps/Tutorial/bin/Debug/net9.0/Ds2.Core.dll"
#r "../Apps/Tutorial/bin/Debug/net9.0/AasCore.Aas3_1.dll"

open System
open System.IO
open System.Diagnostics
open Ds2.Aasx
open Ds2.Core.Store

// SimpleLog 의 Trace.WriteLine 출력을 콘솔로 라우팅 (import 진단용)
Trace.Listeners.Add(new ConsoleTraceListener()) |> ignore

let args = fsi.CommandLineArgs |> Array.skip 1
if args.Length < 2 then
    eprintfn "사용법: dotnet fsi convert-aasx-to-current.fsx <입력.aasx> <출력.aasx>"
    exit 2

let inPath  = args.[0]
let outPath = args.[1]

if not (File.Exists inPath) then
    eprintfn "입력 파일을 찾을 수 없습니다: %s" inPath
    exit 2

printfn "[입력] %s (%d bytes)" inPath (FileInfo(inPath).Length)

let store = DsStore()
AasxImporter.importIntoStoreOrRaise store inPath

let projectCount = store.Projects.Count
let systemCount  = store.Systems.Count
let flowCount    = store.Flows.Count
let workCount    = store.Works.Count
let callCount    = store.Calls.Count
printfn "[Import OK] Projects=%d Systems=%d Flows=%d Works=%d Calls=%d"
    projectCount systemCount flowCount workCount callCount

let outDir = Path.GetDirectoryName(Path.GetFullPath outPath)
if not (String.IsNullOrEmpty outDir) && not (Directory.Exists outDir) then
    Directory.CreateDirectory outDir |> ignore

// IRI prefix: 입력 파일의 shell id 가 'http://your-company.com/shell/...' 였으므로 동일 prefix 사용.
let iriPrefix = "http://your-company.com/"
AasxExporter.exportFromStoreOrRaise store outPath iriPrefix false true

printfn "[출력] %s (%d bytes)" outPath (FileInfo(outPath).Length)
printfn "[완료] 현재 Promaker 형식으로 재저장 완료."
