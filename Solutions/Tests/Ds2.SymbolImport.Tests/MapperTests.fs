module Ds2.SymbolImport.Tests.MapperTests

open Ds2.SymbolImport
open Ds2.SymbolImport.Matching
open Xunit

let private entry name addr direction =
    { Address = addr; Name = name; Direction = direction; Comment = ""; Vendor = Mitsubishi }

[<Fact>]
let ``Mapper.map default config — 빈 입력 → 빈 결과`` () =
    let batch = Mapper.map []
    Assert.Empty(batch.Mapped)
    Assert.Empty(batch.Unmatched)

[<Fact>]
let ``Mapper.mapWithConfig — smoke (예외 없음 + entries 보존)`` () =
    // 합성 fixture 매칭은 dsev2 룰 의존성이 커서 *smoke* 로만. 실 매칭 회귀는 실 PLC fixture 단계.
    let config = MappingConfig.loadDefault ()
    let entries = [
        entry "Clamp1_O_ADV" "Y10" SymbolDirection.Output
        entry "Clamp1_I_ADV" "X10" SymbolDirection.Input
    ]
    let batch = Mapper.mapWithConfig config entries
    // entries 각 항목은 Mapped 의 어딘가 또는 Unmatched 에 보존됨.
    let mappedCount =
        batch.Mapped
        |> List.collect (fun m ->
            let outName = m.OutputEntry |> Option.map (fun e -> e.Name) |> Option.toList
            outName @ (m.InputEntries |> List.map (fun e -> e.Name)))
        |> List.distinct
        |> List.length
    Assert.Equal(entries.Length, mappedCount + batch.Unmatched.Length)

[<Fact>]
let ``ModelGenerator.generate — smoke (Controller plan 항상 생성)`` () =
    let config = MappingConfig.loadDefault ()
    let entries = [
        entry "Clamp1_O_ADV" "Y10" SymbolDirection.Output
        entry "Clamp1_I_ADV" "X10" SymbolDirection.Input
    ]
    let batch = Mapper.mapWithConfig config entries
    let plans = ModelGenerator.generate batch
    let controller = plans |> List.tryFind (fun p -> p.IsActive)
    Assert.True(controller.IsSome, "Controller plan 없음")

[<Fact>]
let ``Mapping 페어 invariant — OutputEntry 또는 InputEntries 중 하나는 있음`` () =
    // dsev2 매칭이 반환한 모든 Mapping 은 *최소 한 쪽* 의 SymbolEntry 를 가져야 의미 있음.
    let config = MappingConfig.loadDefault ()
    let entries = [
        entry "Clamp1_O_ADV" "Y10" SymbolDirection.Output
        entry "Clamp1_I_ADV" "X10" SymbolDirection.Input
    ]
    let batch = Mapper.mapWithConfig config entries
    for m in batch.Mapped do
        Assert.True(
            m.OutputEntry.IsSome || not m.InputEntries.IsEmpty,
            sprintf "Mapping %s/%s/%s/%s 가 Output/Input 둘 다 없음"
                m.FlowName m.WorkName m.DeviceName m.ApiName)

[<Fact>]
let ``MapperRules.toVariable — Direction 매핑 (Output/Input/Memory→Input)`` () =
    let outEntry = entry "Out1" "Y0" SymbolDirection.Output
    let inEntry  = entry "In1"  "X0" SymbolDirection.Input
    let memEntry = entry "Mem1" "M0" SymbolDirection.Memory
    Assert.Equal(IODirection.Output, (MapperRules.toVariable outEntry).Direction)
    Assert.Equal(IODirection.Input,  (MapperRules.toVariable inEntry).Direction)
    Assert.Equal(IODirection.Input,  (MapperRules.toVariable memEntry).Direction)

[<Fact>]
let ``MapperRules.mappingSetsFromConfig — 실 config 로드 시 비어있지 않음`` () =
    let config = MappingConfig.loadDefault ()
    let sets = MapperRules.mappingSetsFromConfig config
    Assert.NotEmpty(sets)
    // 첫 MappingSet 의 DeviceKeywords 와 Apis 가 비어있지 않아야.
    let first = sets |> List.head
    Assert.NotEmpty(first.DeviceKeywords)
    Assert.NotEmpty(first.Apis)
