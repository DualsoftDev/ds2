namespace Ds2.LlmAgent

open System

/// device sugar 의 단일 SSOT 표 (Phase 2.5 M4).
///
/// **호출자**:
/// - `ToolOperations.queueAddCylinder` / `queueAddClamp` — apiNames 빈 list 시 default 보충 + duration default.
/// - `ModelProtocol.deviceDefaults` — DeviceLiteral 별 (apis, opposing, duration) 매핑.
/// - `ModelProtocol.exportToJson` — Passive emit 시 SystemType + apis fingerprint 로 deviceCase 역추정.
///
/// **변경 영향**: 본 표를 수정하면 apply / export / helper 3 경로가 자동 동기. literal 중복 (`["ADV";"RET"]` 등) 제거.
type KnownSugarSpec = {
    /// doc-level deviceCase 이름 (YAML/JSON 의 `device:` 값).
    DeviceCase: string
    /// store 의 `DsSystem.SystemType` 값.
    SystemType: string
    /// 사용자가 apis 미명시 시 채택할 default. 빈 list = 사용자 명시 필수 (robot).
    DefaultApis: string list
    /// SSOT §2.3 — `chain` / `all-pairs` / `none`.
    DefaultOpposing: string
    /// internal Work 의 default duration.
    DefaultDuration: TimeSpan
    /// export 시 apis 키를 *항상* emit 할지. cylinder/clamp = false (default 일치 시 생략 가능), robot = true (사용자 명시 필수).
    EmitApisAlways: bool
}

/// known device sugar 3종 SSOT 표. 호출자가 lookup 으로 default 를 가져온다.
module KnownSugars =

    let cylinder : KnownSugarSpec = {
        DeviceCase      = "cylinder"
        SystemType      = "Unit"
        DefaultApis     = [ "ADV"; "RET" ]
        DefaultOpposing = "chain"
        DefaultDuration = TimeSpan.FromMilliseconds 500.
        EmitApisAlways  = false
    }

    let clamp : KnownSugarSpec = {
        DeviceCase      = "clamp"
        SystemType      = "Unit"
        DefaultApis     = [ "CLP"; "UNCLP" ]
        DefaultOpposing = "chain"
        DefaultDuration = TimeSpan.FromMilliseconds 500.
        EmitApisAlways  = false
    }

    let robot : KnownSugarSpec = {
        DeviceCase      = "robot"
        SystemType      = "Robot"
        DefaultApis     = []
        DefaultOpposing = "none"
        DefaultDuration = TimeSpan.FromMilliseconds 500.
        EmitApisAlways  = true
    }

    /// 모든 known sugar.
    let all : KnownSugarSpec list = [ cylinder; clamp; robot ]

    /// `Custom` 케이스용 default (사용자 정의 SystemType + apis 명시 필수). SystemType / DeviceCase 는 호출처가 지정.
    /// Phase 2.5 cycle2 M1 (5인 review): robot 의 default 와 우연 일치하는 하드코드 (`[], "none", 500ms`) 명시화.
    /// 의미는 별개 — robot 은 known sugar (SystemType="Robot"), Custom 은 사용자 정의 SystemType.
    let customDefaultApis : string list = []
    let customDefaultOpposing : string = "none"
    let customDefaultDuration : System.TimeSpan = System.TimeSpan.FromMilliseconds 500.

    /// export fingerprint 매칭: (SystemType, apis) 로 known sugar 역추정.
    /// - cylinder/clamp: apis set 비교 (순서/중복 무시 — 정확한 ADV/RET 또는 CLP/UNCLP 일치).
    /// - robot: SystemType="Robot" 매칭 시 (apis 자유 — robot 은 apis 명시 필수 sugar 라 fingerprint 키가 SystemType 단독).
    /// 매칭 없으면 None — 호출자가 `custom(<SystemType>)` fallback.
    ///
    /// **확장 주의** (외부 review Minor): `DefaultApis = []` 인 sugar 가 추가되면 첫 매칭으로 흡수되어
    /// robot 과 충돌 가능. 향후 `Conveyor` 등 SystemType 만으로 식별되는 sugar 도입 시 fingerprint key
    /// 를 (SystemType, apis 패턴) 명시 union 으로 확장 필요.
    let tryMatchFingerprint (systemType: string) (apis: string list) : KnownSugarSpec option =
        // Phase 2.5 cycle2 m1 (5인 review): `List.sort` 비교 → `Set.ofList` 비교로 의도 명료화 (apis 순서/중복 무시).
        all |> List.tryPick (fun spec ->
            if spec.SystemType <> systemType then None
            elif List.isEmpty spec.DefaultApis then Some spec
            elif Set.ofList apis = Set.ofList spec.DefaultApis then Some spec
            else None)
