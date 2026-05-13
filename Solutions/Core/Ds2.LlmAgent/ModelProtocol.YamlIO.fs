namespace Ds2.LlmAgent

open System
open System.Text.Json
open Ds2.Core.Store

/// `.yaml` 파일 IO 의 store ↔ YAML 합성 wrapper.
///
/// Save 측 (`exportStoreToYamlText`): `ModelProtocol.exportToJson` 으로 JSON 객체 emit
/// → `ModelProtocolYaml.jsonElementToYaml` 으로 YAML 표현 변환. view: full 부착은 exportToJson 책임.
///
/// Open 측 (`loadStoreFromYamlText`): `ModelProtocolYaml.yamlToJson` 으로 JSON parse
/// → 빈 `DsStore` + `ImportPlanBuilder` + `ModelProtocol.apply` + `ImportPlan.applyDirect` 의 4단계
/// 합성. view: partial 거부 / protocol 검증 / summary 거부 등은 모두 `ModelProtocol.apply` 안에서 처리.
///
/// SSOT: `Apps/Promaker/Docs/yaml-protocol-v0.md`. 본 module 은 *wiring 책임 only* — schema 의미 0.
[<RequireQualifiedAccess>]
module ModelProtocolYamlIO =

    /// store → YAML 텍스트. `exportToJson` 결과의 view 키 = full 자동 부착 (SSOT §2.8).
    let exportStoreToYamlText (store: DsStore) : string =
        use doc = ModelProtocol.exportToJson store
        ModelProtocolYaml.jsonElementToYaml doc.RootElement

    /// YAML 텍스트 → 새 `DsStore`. apply 단계에서 Diagnostics.HasErrors 면 `Error msg` 반환.
    /// 호출자 (FileCommands) 가 dialog 노출 + `IsDirty` 강제 책임.
    let loadStoreFromYamlText (yamlText: string) : Result<DsStore, string> =
        use jsonDoc = ModelProtocolYaml.yamlToJson yamlText
        let newStore = DsStore()
        let plan = ImportPlanBuilder()
        let diag, _refs = ModelProtocol.apply plan newStore jsonDoc.RootElement
        if diag.HasErrors then
            Error (diag.Format())
        else
            Ds2.Core.Store.ImportPlan.applyDirect newStore (plan.Build())
            Ok newStore
