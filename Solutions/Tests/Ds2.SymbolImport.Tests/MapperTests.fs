module Ds2.SymbolImport.Tests.MapperTests

open Ds2.SymbolImport
open Ds2.SymbolImport.Matching
open Xunit

let private entry name addr direction =
    { Address = addr; Name = name; Direction = direction; Comment = ""; Vendor = Mitsubishi }

[<Fact>]
let ``Mapper.map — vendor 인자 시그니처 + 빈 입력 → 빈 결과`` () =
    let batch = Mapper.map Mitsubishi []
    Assert.Empty(batch.Mapped)
    Assert.Empty(batch.Unmatched)

[<Fact>]
let ``Mapper.mapWithConfig — Mitsubishi smoke (entries 전부 mapped 또는 unmatched 에 보존)`` () =
    let config = MappingConfig.loadDefault ()
    let entries = [
        entry "Clamp1_O_ADV" "Y10" SymbolDirection.Output
        entry "Clamp1_I_ADV" "X10" SymbolDirection.Input
    ]
    let batch = Mapper.mapWithConfig Mitsubishi config entries
    let mappedNames =
        batch.Mapped
        |> List.collect (fun m ->
            let outName = m.OutputEntry |> Option.map (fun e -> e.Name) |> Option.toList
            outName @ (m.InputEntries |> List.map (fun e -> e.Name)))
        |> List.distinct
        |> List.length
    Assert.Equal(entries.Length, mappedNames + batch.Unmatched.Length)

[<Fact>]
let ``Mapping 페어 invariant — OutputEntry 또는 InputEntries 중 하나는 있음`` () =
    let config = MappingConfig.loadDefault ()
    let entries = [
        entry "Clamp1_O_ADV" "Y10" SymbolDirection.Output
        entry "Clamp1_I_ADV" "X10" SymbolDirection.Input
    ]
    let batch = Mapper.mapWithConfig Mitsubishi config entries
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

/// 회귀 가드 — bReduceCode 브랜치에서 mappingSetsFromConfig 가 Common.MappingSets 만 보고
/// Vendors.<vendor>.MappingSets 를 통째 무시했던 버그. 결과: 미쯔비시 SOL/CYL/MELFA 룰이 안 돌아서
/// AASX 의 ApiCall OutTag/InTag 가 모두 None — DSPilot 검증 V10 위반 602건.
/// 본 가드는 Common-only 와 Mitsubishi 합산 결과의 *set 개수* 가 달라지는 것을 직접 확인.
[<Fact>]
let ``mappingSetsFromConfig — Mitsubishi 인자가 Common 보다 set 수가 더 많아야 (vendor MappingSets 가 합쳐졌나)`` () =
    let config = MappingConfig.loadDefault ()
    let mitsubishiSets = MapperRules.mappingSetsFromConfig Mitsubishi config
    let commonOnly =
        if isNull (box config.Common) || isNull config.Common.MappingSets then 0
        else config.Common.MappingSets.Length
    // Vendors.Mitsubishi.MappingSets 에 3개 (디바이스 출력 / 실린더-SOL / 로봇 MELFA) 가 정의됨.
    Assert.True(
        mitsubishiSets.Length > commonOnly,
        sprintf "Mitsubishi 합산=%d, Common=%d — vendor MappingSets 가 안 합쳐졌음" mitsubishiSets.Length commonOnly)

/// 회귀 가드 — vendor-level OutputAddressPatterns ("Y*") / InputAddressPatterns ("X*") 가
/// vendor MappingSets 의 각 set 에 inject 돼야 dsev2 매칭 엔진이 Y/X 주소를 Output/Input 으로 식별.
/// 이게 없으면 vendor MappingSet 에 OutputAddressPatterns 가 비어있어 주소 기반 매칭이 실패.
[<Fact>]
let ``mappingSetsFromConfig — Mitsubishi 결과 중 vendor set 들에 Y* 패턴 inject 됨`` () =
    let config = MappingConfig.loadDefault ()
    let mitsubishiSets = MapperRules.mappingSetsFromConfig Mitsubishi config
    // Vendor MappingSets 의 Name 은 "Mitsubishi 전용 - ..." prefix.
    let vendorSets =
        mitsubishiSets
        |> List.filter (fun s -> s.Name.StartsWith("Mitsubishi 전용"))
    Assert.NotEmpty(vendorSets)
    for s in vendorSets do
        Assert.Contains("Y*", s.OutputAddressPatterns)
        Assert.Contains("X*", s.InputAddressPatterns)
