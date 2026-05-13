module DeviceTypeDriftTests

open System.IO
open System.Text.RegularExpressions
open Xunit

/// extend-mcp §5.6 신규 4 — `DevicePresets.KnownNames` (19종) 와 helper 가 발행하는
/// deviceType 값 (`"Unit"` / `"Robot"` / `"Conveyor"` ...) 의 sync 검증.
///
/// drift 시 helper 산출 PassiveSystem.SystemType 이 3D view auto-mapping 영역 밖 → silent 시각 누락.
/// 본 test 는 텍스트 파싱 (ProjectReference 부담 회피) — 회귀 시 KnownNames source 위치 + 본 test
/// expected list 양쪽 동시 갱신.

let private repoRoot = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..") |> Path.GetFullPath

let private knownNamesPath =
    Path.Combine(repoRoot, "Solutions", "View", "Ds2.View3D", "Ds2.View3D.Core", "Types.fs")

let private extractKnownNames () : string Set =
    let txt = File.ReadAllText knownNamesPath
    let m = Regex.Match(txt, @"KnownNames\s*=\s*Set\.ofList\s*\[(.*?)\]", RegexOptions.Singleline)
    Assert.True(m.Success, "KnownNames 정의를 찾을 수 없음 — Types.fs 의 파싱 패턴 점검")
    Regex.Matches(m.Groups.[1].Value, "\"([^\"]+)\"")
    |> Seq.cast<Match>
    |> Seq.map (fun mm -> mm.Groups.[1].Value)
    |> Set.ofSeq

[<Fact>]
let ``Types.fs 의 KnownNames 가 19종 (todo §2_2 검증된 사실)`` () =
    let names = extractKnownNames ()
    Assert.Equal(19, names.Count)

[<Fact>]
let ``helper 가 사용하는 deviceType (Unit / Robot / Conveyor) 모두 KnownNames 에 포함`` () =
    let names = extractKnownNames ()
    // todo §3.3 D1 확정: cylinder/clamp/lifter→"Unit", robot→"Robot", conveyor→"Conveyor".
    // 본 3종이 KnownNames 안에 모두 존재해야 helper PassiveSystem.SystemType 이 3D auto-mapping 작동.
    let expected = Set.ofList ["Unit"; "Robot"; "Conveyor"]
    let missing = Set.difference expected names
    Assert.True(
        Set.isEmpty missing,
        sprintf "KnownNames 에 누락된 helper deviceType: %A (helper SystemType drift)" (Set.toList missing))

[<Fact>]
let ``KnownNames 핵심 19종 sanity (drift 시 19 → 본 test expected 갱신 필요)`` () =
    let names = extractKnownNames ()
    let expected =
        Set.ofList [
            "Robot"; "Conveyor"; "Unit"; "AGV"; "Gripper"; "Lifter"; "Crane"
            "Stacker"; "Sorter"; "Transfer"; "Barrier"; "Door"; "Gate"
            "Elevator"; "Hoist"; "Pusher"; "Rotary"; "Turntable"; "Tilter"
        ]
    Assert.Equal<string Set>(expected, names)
