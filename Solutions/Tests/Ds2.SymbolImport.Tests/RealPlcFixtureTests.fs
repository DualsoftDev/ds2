module Ds2.SymbolImport.Tests.RealPlcFixtureTests

open System
open System.IO
open Ds2.SymbolImport
open Xunit

/// <summary>
/// 실 PLC dump (samples/미쯔비시) 회귀 테스트.
/// dsev2 매칭 룰 (input-matching-config.json 2,874줄) 이 실 현장 데이터에서
/// 정상 동작하는지 *통째 흐름* 검증. parse → mapping → plan → validation 단일 path.
///
/// fixture 위치는 repo 루트 의 samples/ — test 실행 시 AppContext.BaseDirectory 에서
/// Solutions/ 발견할 때까지 거슬러 올라간 후 그 parent 의 samples/ 사용.
/// </summary>
module private RepoPaths =
    let rec findRepoRoot (dir: DirectoryInfo) : DirectoryInfo option =
        if isNull (box dir) then None
        else
            let hasSolutions = Directory.Exists(Path.Combine(dir.FullName, "Solutions"))
            let hasSamples   = Directory.Exists(Path.Combine(dir.FullName, "samples"))
            if hasSolutions && hasSamples then Some dir
            else findRepoRoot dir.Parent

    let repoRoot () : DirectoryInfo =
        match findRepoRoot (DirectoryInfo(AppContext.BaseDirectory)) with
        | Some d -> d
        | None ->
            // CI 환경에서 못 찾을 경우 — test skip 신호로 사용.
            failwith "repo root with Solutions/ + samples/ 찾을 수 없음 (test 환경 점검)"

    let samplePath (relative: string) : string =
        let root = repoRoot ()
        Path.Combine(root.FullName, "samples", relative)

let private fixturePath = RepoPaths.samplePath @"미쯔비시\LSEV_CCS_조립라인_CSV_260408\COMMENT.csv"
let private carbodyPath = RepoPaths.samplePath @"미쯔비시\자동차 차체\COMMENT.csv"
let private xgkCsvPath = RepoPaths.samplePath @"375_4M삽입밴딩기_PLC_160928_up.csv"
let private xgbCsvPath = RepoPaths.samplePath @"변수_설명.csv"

let private skipIfMissing (path: string) : unit =
    if not (File.Exists path) then
        // xUnit 의 skip 은 throw SkipException — 우리는 단순히 통과로 처리. fixture 파일이 깃에 없을 수 있음.
        ()

[<Fact>]
let ``LSEV_CCS COMMENT.csv parse — 23K 줄 / UTF-16 LE / 예외 없음`` () =
    if not (File.Exists fixturePath) then () else
    let result = CsvParser.parseFile Mitsubishi fixturePath
    // 23K 줄 dump 면 entries 도 수천 건 나와야.
    Assert.True(result.Entries.Length > 100,
        sprintf "entries=%d (예상 > 100). warnings=%d" result.Entries.Length result.Warnings.Length)

[<Fact>]
let ``LSEV_CCS COMMENT.csv full flow — parse → map → generate → validate (예외 없음)`` () =
    if not (File.Exists fixturePath) then () else
    let parseResult = CsvParser.parseFile Mitsubishi fixturePath
    let batch = Mapper.map Mitsubishi parseResult.Entries
    let plans = ModelGenerator.generate batch
    let _ = Validation.validate batch plans

    // entries 전부 Mapped 또는 Unmatched 에 보존 (matched ApiCall 의 OutputEntry/InputEntries 와 Unmatched 의 합).
    let mappedNames =
        batch.Mapped
        |> List.collect (fun m ->
            let outName = m.OutputEntry |> Option.map (fun e -> e.Name) |> Option.toList
            outName @ (m.InputEntries |> List.map (fun e -> e.Name)))
        |> Set.ofList
    let unmatchedSet = batch.Unmatched |> List.map (fun e -> e.Name) |> Set.ofList
    // mapped + unmatched union 이 입력 set 을 cover.
    let inputSet = parseResult.Entries |> List.map (fun e -> e.Name) |> Set.ofList
    let coverSet = Set.union mappedNames unmatchedSet
    Assert.True(
        inputSet.IsSubsetOf coverSet,
        sprintf "%d 개 심볼이 Mapped/Unmatched 어디에도 없음 (cover 누락)"
            (Set.difference inputSet coverSet |> Set.count))

[<Fact>]
let ``LSEV_CCS COMMENT.csv — Controller plan + Device plan 적어도 1개씩 생성`` () =
    if not (File.Exists fixturePath) then () else
    let parseResult = CsvParser.parseFile Mitsubishi fixturePath
    let batch = Mapper.map Mitsubishi parseResult.Entries
    let plans = ModelGenerator.generate batch
    // Controller 1개 + Device N개.
    Assert.Contains(plans, fun p -> p.IsActive && p.Name = "Controller")
    // matched 가 있으면 device plan 도 있어야.
    if not batch.Mapped.IsEmpty then
        Assert.Contains(plans, fun p -> not p.IsActive)

[<Fact>]
let ``LSEV_CCS COMMENT.csv — Mapped 결과의 모든 ApiDef 가 v10 ActionType=Real, SensingType=Real 형식`` () =
    if not (File.Exists fixturePath) then () else
    let parseResult = CsvParser.parseFile Mitsubishi fixturePath
    let batch = Mapper.map Mitsubishi parseResult.Entries
    let plans = ModelGenerator.generate batch
    // Device plan 의 모든 ApiDef.ActionType / SensingType 검사.
    let devicePlans = plans |> List.filter (fun p -> not p.IsActive)
    for plan in devicePlans do
        for apiDef in plan.ApiDefs do
            // v10 §3.2 — ActionType/SensingType 은 항상 Real(_, _) 또는 Virtual(_).
            // ModelGenerator.inferActionType/SensingType 은 항상 Real 반환 (Virtual 미사용).
            match apiDef.ActionType with
            | Ds2.Core.ActionType.Real _ -> ()
            | _ -> Assert.Fail(sprintf "ApiDef '%s' ActionType 이 Real 아님" apiDef.Name)
            match apiDef.SensingType with
            | Ds2.Core.SensingType.Real _ -> ()
            | _ -> Assert.Fail(sprintf "ApiDef '%s' SensingType 이 Real 아님" apiDef.Name)

[<Fact>]
let ``자동차 차체 COMMENT.csv parse — 30K 줄 smoke`` () =
    if not (File.Exists carbodyPath) then () else
    let result = CsvParser.parseFile Mitsubishi carbodyPath
    Assert.True(result.Entries.Length > 100,
        sprintf "entries=%d (예상 > 100)" result.Entries.Length)

/// 회귀 가드 — V10 위반 (Call.OutTag/InTag None) 의 직접 검증.
/// Mitsubishi vendor MappingSets 가 매칭 엔진에 합쳐졌다면 ApiCall 의 *상당수* 가 OutTag+InTag 페어를 가져야 한다.
/// 이전 (Common 만 사용) 에는 vendor 특화 룰이 안 돌아서 OutTag/InTag None 비율이 매우 높았음.
/// 본 가드는 페어 비율 임계값을 두진 않고, 적어도 OutTag 또는 InTag 가 채워진 매핑이 다수 존재하는지 확인.
[<Fact>]
let ``LSEV_CCS COMMENT.csv — Mapped 중 OutTag+InTag 둘 다 있는 페어가 다수 존재 (V10 회귀 가드)`` () =
    if not (File.Exists fixturePath) then () else
    let parseResult = CsvParser.parseFile Mitsubishi fixturePath
    let batch = Mapper.map Mitsubishi parseResult.Entries
    let plans = ModelGenerator.generate batch
    // Controller plan 의 모든 Call 을 평탄화.
    let allCalls =
        plans
        |> List.filter (fun p -> p.IsActive)
        |> List.collect (fun p -> p.Flows |> List.collect (fun f -> f.Works |> List.collect (fun w -> w.Calls)))
    let bothTagCount =
        allCalls
        |> List.filter (fun c -> c.OutTag.IsSome && c.InTag.IsSome)
        |> List.length
    let anyTagCount =
        allCalls
        |> List.filter (fun c -> c.OutTag.IsSome || c.InTag.IsSome)
        |> List.length
    // Mapped 가 충분히 있는 fixture 인데 OutTag+InTag 둘 다 채워진 Call 이 0 이면 vendor 룰 회귀.
    Assert.True(
        bothTagCount > 0,
        sprintf "Call 총 %d 건 중 OutTag+InTag 둘 다 있는 페어 0건 — vendor MappingSets 회귀"
            allCalls.Length)
    // 적어도 한쪽 태그라도 있는 Call 이 절반 이상 — Common 만으로는 거의 0 이었음.
    Assert.True(
        anyTagCount * 2 > allCalls.Length,
        sprintf "Call %d 건 중 태그 있는 게 %d 건 (절반 미만) — vendor 매칭 부족"
            allCalls.Length anyTagCount)

[<Fact>]
let ``XGK CSV sample parse — CP949 / QX output / IX input`` () =
    if not (File.Exists xgkCsvPath) then () else
    let result = CsvParser.parseFile XGK xgkCsvPath
    Assert.True(result.Entries.Length > 100, sprintf "entries=%d" result.Entries.Length)
    Assert.Contains(result.Entries, fun e -> e.Name.StartsWith("QX_") && e.Direction = SymbolDirection.Output)
    Assert.Contains(result.Entries, fun e -> e.Name.StartsWith("IX_") && e.Direction = SymbolDirection.Input)
    Assert.Contains(result.Entries, fun e -> e.Name.StartsWith("TL_") && e.Direction = SymbolDirection.Output)
    Assert.Contains(result.Entries, fun e -> e.Name.StartsWith("TS_") && e.Direction = SymbolDirection.Input)

[<Fact>]
let ``XGB variable-description sample parse — CP949 / QX output / IX input`` () =
    if not (File.Exists xgbCsvPath) then () else
    let result = CsvParser.parseFile XGB xgbCsvPath
    Assert.True(result.Entries.Length > 100, sprintf "entries=%d" result.Entries.Length)
    Assert.Contains(result.Entries, fun e -> e.Name.StartsWith("QX_") && e.Direction = SymbolDirection.Output)
    Assert.Contains(result.Entries, fun e -> e.Name.StartsWith("IX_") && e.Direction = SymbolDirection.Input)

[<Fact>]
let ``XGK CSV sample full flow — parse map generate creates active Works and bounded Calls`` () =
    if not (File.Exists xgkCsvPath) then () else
    let config = MappingConfig.loadDefault ()
    let parseResult = CsvParser.parseFile XGK xgkCsvPath
    let batch = Mapper.mapWithConfig XGK config parseResult.Entries
    let plans = ModelGenerator.generateWithConfig config batch
    let works =
        plans
        |> List.filter (fun p -> p.IsActive)
        |> List.collect (fun p -> p.Flows |> List.collect (fun f -> f.Works))
    Assert.NotEmpty(works)
    Assert.Contains(works, fun w -> w.Calls.Length >= 1 && w.Calls.Length <= 20)
    let hmiPairs =
        batch.Mapped
        |> List.filter (fun m ->
            (m.OutputEntry |> Option.exists (fun e -> e.Name.StartsWith("TL_")))
            && (m.InputEntries |> List.exists (fun e -> e.Name.StartsWith("TS_"))))
    Assert.True(hmiPairs.Length > 100, sprintf "TL/TS HMI 페어가 너무 적음: %d" hmiPairs.Length)
