namespace Ds2.LlmAgent.Internal

// =============================================================================
// Modeling level SSOT — Phase 7 §10.2 #31 / yaml-protocol-v0.md §1.7 / §2.1 / §2.4.1
//
// `export_model_doc.level: "full" | "modeling"` 인자의 분류 SSOT. wire 의 각 키를
// 4 카테고리 (A/B/C/D) 로 분류하고 `level: modeling` 시 A 카테고리만 emit.
//
// 본 모듈은 *SSOT-of-types* — emit/apply/capturer 양측에서 같은 enum + helper 를 참조.
// 분류 표 자체는 `yaml-protocol-v0.md §2.4.1 Category 사전` 행과 docstring 정합.
//
// **사용자 결정 (2026-05-15)**:
// - A_Modeling: CallCondition / ContactKind / SkipInputSensor / CallType / TokenRole
//               + apiDetails.actionType (시간 인자 포함 — DU leaf 분해 불가, M2 footnote)
// - B_Addressing: IOTag (InTag / OutTag)
// - C_Meta: author / version / iri / description / workDuration / apiDetails.description
// - D_Plc: plc: sub-section (PlcMetadata.fs 의 54 leaf 통째 — M5 후속 leaf 단위 override 가능)
// =============================================================================

/// **visibility note** — module 자체는 *public* (internal 아님). 사유: `ExportLevel` 이
/// `ModelProtocol.exportToJsonWithLevel` / `exportToJsonScopedWithLevel` public 함수의
/// 매개변수 type 으로 노출됨 (wire schema 의 일부). `Category` / `isEmittedIn` / `categoryOfPlcLeaf`
/// 도 capturer (test project) 에서 직접 사용 가능 (`InternalsVisibleTo` 의존 0건).
/// namespace `Ds2.LlmAgent.Internal` 은 이름 convention — assembly visibility 와 직교.
/// `PlanLookup.fs` 의 namespace 정책 docstring (m6 외부 review 산출) 도 S4 phase 에서 갱신 예정.
module ModelingCategory =

    /// 4 카테고리 — modeling level 시 A_Modeling 만 emit, 나머지 생략.
    /// 골격 키 (protocol/project/view/level/summary/systems/system/kind/device/apis/opposing/
    /// flow <Name>/works/arrows/calls/ref/patch + ArrowType) 는 카테고리 무관 — modeling 도 emit.
    type Category =
        | A_Modeling      // LLM 모델 동작 의미 (제어 동작, 신호 평가, 시퀀스 정책)
        | B_Addressing    // PLC 어드레스 매핑 (IOTag — InTag/OutTag)
        | C_Meta          // 동작 무관 메타 (author/version/iri/description/workDuration)
        | D_Plc           // plc: sub-section (PlcMetadata.fs 의 54 leaf 통째)

    /// Export level — `exportToJson` / `exportToJsonScoped` 에 전달. wire 의 `level:` 키와 1:1.
    type ExportLevel =
        | Full            // 전체 카테고리 emit (default — 기존 동작, 회귀 0)
        | Modeling        // A_Modeling 만 emit (B/C/D + workDuration + apiDetails.description 생략)

    /// modeling level 시 emit 대상 여부.
    /// 분산 분기 패턴 — emit 직전 `if isEmittedIn level cat then ...` 형태로 13시점에서 호출.
    let isEmittedIn (level: ExportLevel) (cat: Category) : bool =
        match level with
        | Full -> true
        | Modeling -> cat = A_Modeling

    /// **M5 leaf 단위 분류 표현력 (사용자 결정 — defer)**:
    /// sub-agent review 권장은 `categoryOfPlcLeaf: PlcLeaf<'cp> → Category` helper 신설로
    /// leaf 단위 override 가능 구조 도입. 현재는 PlcMetadata 의 54 leaf 모두 D_Plc 일괄.
    /// 본 helper 자체는 dead code 가 되므로 미도입 — 실제 leaf 단위 분류 요구 (예: WorkTimeout 등
    /// 동작 의미 가능 leaf 를 A_Modeling 으로 격상) 가 발생하는 후속 phase 에서 PlcMetadata 의
    /// visibility 격상 또는 별도 분류 표 (key → Category map) 와 함께 도입.

    /// wire 의 `level:` 키 값 (format).
    let formatLevel (level: ExportLevel) : string =
        match level with
        | Full -> "full"
        | Modeling -> "modeling"

    /// wire 의 `level:` 키 parse. unknown 라벨은 None — apply 측이 §2.7 룰 #29 ERROR 발행.
    let tryParseLevel (raw: string) : ExportLevel option =
        match raw with
        | "full" -> Some Full
        | "modeling" -> Some Modeling
        | _ -> None

    /// modeling level wire 에서 *등장 금지* 인 키 → Category 매핑 (SSOT §2.7 룰 #30).
    /// 골격 키 (protocol / project / view / level / summary / systems / system / kind / device /
    /// apis / opposing / flow <Name> / works / arrows / calls / ref / patch) 와 A_Modeling 키
    /// (tokenRole / contactKind / skipInputSensor / callType / callCondition / actionType) 는
    /// 매핑 부재 — modeling level 에서도 등장 허용. 동일 키 (예: `plc`) 가 4 entity context 에 등장하나
    /// path 무관 일관 분류 — wire walk 시 키 이름만 lookup.
    /// **boundary**: `description` 은 apiDetails entry 안 leaf 한정 — C_Meta. top-level / 다른 entity 안
    /// `description` 키 등장 시에도 본 매핑 적용 (현 v0 schema 는 다른 위치에 description 키 부재).
    let nonModelingKeys : Map<string, Category> = Map.ofList [
        "author",       C_Meta
        "version",      C_Meta
        "iri",          C_Meta
        "workDuration", C_Meta
        "description",  C_Meta
        "plc",          D_Plc
        "inTag",        B_Addressing
        "outTag",       B_Addressing
    ]

    /// Category 의 한국어 라벨 (진단 메시지용). §2.4.1 Enum 사전의 Category 행 정합.
    let categoryLabel (cat: Category) : string =
        match cat with
        | A_Modeling   -> "A_Modeling"
        | B_Addressing -> "B_Addressing"
        | C_Meta       -> "C_Meta"
        | D_Plc        -> "D_Plc"
