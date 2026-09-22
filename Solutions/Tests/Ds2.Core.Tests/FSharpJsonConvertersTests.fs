module FSharpJsonConvertersTests

open System
open System.Text.Json
open Xunit
open Ds2.Core
open Ds2.Core.StandardSubmodels

// =============================================================================
// F# 컨버터(option/list/tuple/DU/record)의 **와이어 포맷**을 못 박는다.
// record/DU/tuple 생성·분해가 FSharpValue.PreCompute* 델리게이트로 바뀌었고
// (리플렉션을 타입당 1회로 — FSharpJsonConverters.fs 주석 참조) 그 최적화가
// 바이트 단위로 무해하다는 것이 이 파일의 주장이다. 여기가 깨지면 기존 .sdf/.aasx 를
// 못 읽거나 조용히 다르게 쓰는 것이므로, 포맷을 바꿀 의도가 없다면 고치지 말 것.
// =============================================================================

type private Inner = { Label: string; Count: int }

type private Sample = {
    Name: string
    Note: string option
    Tags: string list
    Inner: Inner
    Pair: int * string
}

/// 실제 모델에 존재하는 형태 — struct record (예: SimCapacityAnalysis).
[<Struct>]
type private StructRec = {
    Id: Guid
    Ratio: float
    Items: string array
}

type private Shape =
    | Dot
    | Circle of radius: float
    | Box of w: int * label: string

let private deepCopyOpts = JsonOptions.createDeepCopyOptions ()
let private fileOpts = JsonOptions.createProjectSerializationOptions ()

// box 필수 — 제네릭 'T 를 그대로 넘기면 F# 이 Serialize(Stream, ...) 오버로드를 고른다.
let private ser (o: JsonSerializerOptions) (v: 'T) : string = JsonSerializer.Serialize(box v, typeof<'T>, o)
let private de (o: JsonSerializerOptions) (json: string) : 'T = JsonSerializer.Deserialize(json, typeof<'T>, o) :?> 'T

// ── 와이어 포맷 고정 ─────────────────────────────────────────────

[<Fact>]
let ``DU is adjacent-tag Case plus Fields`` () =
    Assert.Equal("""{"Case":"Dot"}""", ser deepCopyOpts Dot)
    Assert.Equal("""{"Case":"Circle","Fields":[2.5]}""", ser deepCopyOpts (Circle 2.5))
    Assert.Equal("""{"Case":"Box","Fields":[3,"wide"]}""", ser deepCopyOpts (Box(3, "wide")))

[<Fact>]
let ``tuple is a JSON array`` () =
    Assert.Equal("""[7,"x"]""", ser deepCopyOpts (7, "x"))

[<Fact>]
let ``option is the bare value or null, list is an array`` () =
    Assert.Equal("""{"Label":"a","Count":1}""", ser deepCopyOpts { Label = "a"; Count = 1 })
    Assert.Equal("null", ser deepCopyOpts (None: string option))
    Assert.Equal("\"v\"", ser deepCopyOpts (Some "v"))
    Assert.Equal("""["a","b"]""", ser deepCopyOpts [ "a"; "b" ])

[<Fact>]
let ``record uses camelCase and drops None under the file options`` () =
    let v = { Name = "n"; Note = None; Tags = []; Inner = { Label = "l"; Count = 2 }; Pair = (1, "p") }
    let json = ser fileOpts v
    Assert.Contains("\"name\": \"n\"", json)
    Assert.DoesNotContain("note", json)   // WhenWritingNull — None 은 나가지 않는다
    Assert.Contains("\"label\": \"l\"", json)

// ── 왕복 ─────────────────────────────────────────────────────────

[<Fact>]
let ``record round-trips through both option sets`` () =
    let v = { Name = "n"; Note = Some "note"; Tags = [ "a"; "b" ]
              Inner = { Label = "l"; Count = 2 }; Pair = (1, "p") }
    let viaDeepCopy : Sample = de deepCopyOpts (ser deepCopyOpts v)
    let viaFile : Sample = de fileOpts (ser fileOpts v)
    Assert.Equal(v, viaDeepCopy)
    Assert.Equal(v, viaFile)

[<Fact>]
let ``DU round-trips every case shape`` () =
    for v in [ Dot; Circle 2.5; Box(3, "wide") ] do
        let back : Shape = de deepCopyOpts (ser deepCopyOpts v)
        Assert.Equal(v, back)

[<Fact>]
let ``struct record round-trips`` () =
    let v = { Id = Guid.NewGuid(); Ratio = 0.885; Items = [| "a"; "b" |] }
    let back : StructRec = de deepCopyOpts (ser deepCopyOpts v)
    Assert.Equal(v.Id, back.Id)
    Assert.Equal(v.Ratio, back.Ratio)
    Assert.Equal<string array>(v.Items, back.Items)

/// 구버전 파일에 없던 필드는 타입 기본값으로 채운다 — option 은 None, value type 은 default.
[<Fact>]
let ``missing fields fall back to type defaults`` () =
    let partial = """{"name":"only"}"""
    let back : Sample = de fileOpts partial
    Assert.Equal("only", back.Name)
    Assert.True(back.Note.IsNone)
    // ⚠ 기존부터의 날: 없는 **F# list** 필드는 [] 가 아니라 null 이 된다. 폴백이
    // Deserialize("null", …) 인데 STJ 가 JSON null 을 컨버터에 넘기지 않고 바로 null 을
    // 돌려주기 때문. 이 최적화와 무관한 선재 동작이라 여기서는 고치지 않고 못 박아만 둔다.
    Assert.True(isNull (box back.Tags))

    let backStruct : StructRec = de fileOpts """{"ratio":1.5}"""
    Assert.Equal(Guid.Empty, backStruct.Id)
    Assert.Equal(1.5, backStruct.Ratio)

// ── 오류 경로 ────────────────────────────────────────────────────

[<Fact>]
let ``unknown union case is rejected`` () =
    let ex = Assert.ThrowsAny<exn>(fun () -> (de deepCopyOpts """{"Case":"Triangle"}""" : Shape) |> ignore)
    Assert.Contains("Triangle", ex.Message)

[<Fact>]
let ``union without the Case property is rejected`` () =
    let ex = Assert.ThrowsAny<exn>(fun () -> (de deepCopyOpts """{"Fields":[1.0]}""" : Shape) |> ignore)
    Assert.Contains("Case", ex.Message)

// ── 실제 모델 타입 (AID) ─────────────────────────────────────────

/// 저장 파일에 실제로 실리는 모양 — record + DU + option + list + struct 가 한 그래프에 섞인다.
[<Fact>]
let ``AID binding round-trips byte-identically`` () =
    let aid = AssetInterfacesDescription()
    let request =
        AidXgtConnectionInfo("", "LsXgi", "tcp", "192.168.0.10", 2004, "", false, 0uy, 0uy, 1000, 100, None)
    AidXgtEndpointSettings.ensureBindingForSystem
        (aid, Guid.NewGuid(), request, [ "%QX0.1.13"; "%IX0.1.2" ]) |> ignore

    for opts in [ deepCopyOpts; fileOpts ] do
        let json = ser opts aid
        let back : AssetInterfacesDescription = de opts json
        Assert.Equal(json, ser opts back)
        let interactions =
            back.Interfaces
            |> Seq.sumBy (fun b -> match b with Xgt (_, xs) -> List.length xs | _ -> 0)
        Assert.Equal(2, interactions)
