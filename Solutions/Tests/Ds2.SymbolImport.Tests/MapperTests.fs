module Ds2.SymbolImport.Tests.MapperTests

open Ds2.SymbolImport
open Ds2.SymbolImport.Matching
open Xunit

let private entry name addr direction =
    { Address = addr; Name = name; Direction = direction; Comment = ""; Vendor = Mitsubishi }

let private vendorEntry vendor name addr direction =
    { Address = addr; Name = name; Direction = direction; Comment = ""; Vendor = vendor }

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

[<Fact>]
let ``mappingSetsFromConfig — XGB and XGK use LS vendor rules`` () =
    let config = MappingConfig.loadDefault ()
    let lsSets = MapperRules.mappingSetsFromConfig XG5000 config
    let xgbSets = MapperRules.mappingSetsFromConfig XGB config
    let xgkSets = MapperRules.mappingSetsFromConfig XGK config
    Assert.Equal(lsSets.Length, xgbSets.Length)
    Assert.Equal(lsSets.Length, xgkSets.Length)
    Assert.Contains(xgbSets, fun s -> s.OutputAddressPatterns |> List.contains "%QX*")
    Assert.Contains(xgkSets, fun s -> s.InputAddressPatterns |> List.contains "%IX*")

[<Fact>]
let ``Mapper.mapWithConfig — LS QX SV to IX RS maps and keeps CV_1~6 in one CV Work`` () =
    let config = MappingConfig.loadDefault ()
    let entries = [
        vendorEntry XGK "QX_CV_1_ADV_SV" "P00104" SymbolDirection.Output
        vendorEntry XGK "IX_CV_1_ADV_RS" "P00028" SymbolDirection.Input
        vendorEntry XGK "QX_CV_2_ADV_SV" "P00105" SymbolDirection.Output
        vendorEntry XGK "IX_CV_2_ADV_RS" "P00029" SymbolDirection.Input
        vendorEntry XGK "QX_CV_6_RET_SV" "P00106" SymbolDirection.Output
        vendorEntry XGK "IX_CV_6_RET_RS" "P0002A" SymbolDirection.Input
    ]
    let batch = Mapper.mapWithConfig XGK config entries
    Assert.Equal(3, batch.Mapped.Length)
    let workNames = batch.Mapped |> List.map (fun m -> m.WorkName) |> Set.ofList
    let message = sprintf "workNames=%A" workNames
    Assert.True((workNames = Set.singleton "CV"), message)
    for mapping in batch.Mapped do
        Assert.True(mapping.OutputEntry.IsSome)
        Assert.NotEmpty(mapping.InputEntries)

/// 회귀 가드 — 같은 Name 을 가진 actuator(Y)/sensor(X) 페어 (예: "Stopper Up" 가 X1240 / Y1250 둘 다).
/// 이전 Mapper.fs 의 entryByName = Map<string, SymbolEntry> 는 collision 으로 후자만 유지 → OutputEntry 의 lookup 도
/// 의도와 다른 entry 로 치환 → InputEntries 에도 OutputEntry 와 동일 주소가 박힘 → 시뮬레이터 duplicate-address 에러.
/// fix: (Name, Address) 쌍으로 lookup.
[<Fact>]
let ``Mapper — 같은 이름의 actuator/sensor 페어가 collision 없이 보존 (Y/X 페어)`` () =
    let config = MappingConfig.loadDefault ()
    // PLC 산출물의 흔한 패턴 — 액추에이터 출력 Y1250 과 위치 센서 입력 X1240 모두 "Laser Stopper Up" 으로 명명.
    let entries = [
        entry "Laser Stopper Up"   "X1240" SymbolDirection.Input    // sensor
        entry "Laser Stopper Up"   "Y1250" SymbolDirection.Output   // actuator
        entry "Laser Stopper Down" "X1241" SymbolDirection.Input
        entry "Laser Stopper Down" "Y1251" SymbolDirection.Output
    ]
    let batch = Mapper.mapWithConfig Mitsubishi config entries
    // OutputEntry 와 InputEntries[0] 이 같은 주소를 가지면 안 됨 — collision 회귀.
    for m in batch.Mapped do
        match m.OutputEntry, m.InputEntries |> List.tryHead with
        | Some o, Some i ->
            Assert.NotEqual<string>(o.Address, i.Address)
        | _ -> ()
