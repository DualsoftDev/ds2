namespace Ds2.CSV

open System
open Ds2.Core

/// ds2-csv-for-ai/v1 — 7열(Kind,Name,Type,Detail,Time,InTag,OutTag) 파싱 결과 타입.
///
/// 계약 문서: Solutions/Convert/Ds2.CSV/docs/CSV_FOR_AI_SCHEMA.md
/// 생성 지침: Solutions/Convert/Ds2.CSV/docs/CSV_FOR_AI_GUIDE.md
///
/// 기본 3열과 갈리는 지점은 하나다 — **행 순서에 의미가 없다**.
/// 3열은 «행 순서 = StartReset 체인» 이라는 암묵 규약을 썼고, 그래서 Flow 를 나눠 적어도
/// 매퍼가 한 줄로 이어 붙였다. 여기서는 Work 사이 관계를 전부 ARROW 행으로 적는다.
/// 정렬해도 모델이 변하지 않는다.

/// 행 종류. 1열 `Kind` 값이며, 이 값이 나머지 6열의 뜻을 정한다.
type AiRowKind =
    | KSys
    | KFlow
    | KWork
    | KArrow
    | KApi
    | KCond

/// 조건식 AST. COND 행 Detail 셀을 파싱한 결과.
///
/// ds2 Condition 은 (IsOR, IsInverted, ApiCalls, Children) 트리이고 leaf 마다 ContactKind 를
/// 갖는다. 부정은 **그룹이 진다** — leaf 접점으로 부정을 겹쳐 걸면 `/A=false` 처럼 같은 뜻을
/// 두 번 말해 읽을 수 없게 된다(ds2 가 최근 그 방향으로 정리했다).
type AiCondExpr =
    /// `Device.Api` leaf. spec 은 기대값 텍스트(없으면 기본 true).
    | AiLeaf of device: string * api: string * contact: ContactKind * spec: string option
    /// `_ON` / `_OFF` 같은 raw 심벌 leaf — ApiCall 매핑 없이 이름으로만 남는다.
    | AiRawLeaf of symbol: string * contact: ContactKind
    /// 묶음. isOr=false 면 AND. isInverted 면 그룹 전체를 부정한다.
    | AiGroup of isOr: bool * isInverted: bool * children: AiCondExpr list

/// SYS 행 — System 하나. 평면 컬렉션이라 부모/자식 열이 없다.
type AiSystemRow = {
    Name       : string
    IsActive   : bool
    /// SystemType(선택). 공란이면 매퍼가 디바이스 캐스케이드 기본값을 쓴다.
    SystemType : string option
    LineNumber : int
}

/// FLOW 행 — Flow 하나. **행 개수가 곧 Capa** 다(동시에 보유하는 고유 제품 수).
type AiFlowRow = {
    Name       : string
    LineNumber : int
}

/// WORK 행 — Work 하나와 그 안의 Call DAG.
type AiWorkRow = {
    FlowName   : string
    WorkName   : string
    /// TokenRole 조합(`Source+Sink` 등). 역할이 없으면 TokenRole.Normal.
    Roles      : TokenRole
    /// `Finish` 플래그 — TokenRole 이 아니라 «초기 상태가 Finish» 표시(R1 상호 재무장의 복귀측).
    InitFinish : bool
    /// (노드 키 = "디바이스.액션", 디바이스, 액션) — Detail 최초 등장순, 같은 이름은 병합.
    Nodes      : (string * string * string) list
    /// (source 노드 키, target 노드 키) — `>` 인접쌍. Call 레벨 Start 엣지.
    Edges      : (string * string) list
    /// Call 이 없는 Work 에만 허용되는 동작 시간. Call 을 가진 Work 에 적으면 AI021 오류.
    Duration   : TimeSpan option
    LineNumber : int
}

/// ARROW 행 — Work 사이(2마디) 또는 Call 사이(4마디) 관계.
/// 끝점 마디 수가 곧 레벨 판정이라 별도 열이 없다.
type AiArrowRow = {
    /// true = Call 레벨(4마디). Call 레벨은 Start/Group 만 허용한다.
    IsCallLevel : bool
    Source      : string
    ArrowType   : ArrowType
    Targets     : string list
    LineNumber  : int
}

/// API 행 — ApiDef 하나와 그 IO 태그.
type AiApiRow = {
    Device     : string
    Api        : string
    Action     : ActionType
    Sensing    : SensingType
    /// 이 API 가 구동하는 «Call 없는 Work» 의 Duration. 미지정이면 매퍼가 기본값(500ms)을 쓴다.
    Duration   : TimeSpan option
    /// true = 센서 전용 선언. DONE 더미 Work 와 그것이 낳는 Source 후보 경고를 면제한다.
    /// 센서는 출력이 없어 API 가 하나뿐인데, 그 하나를 «동작» 으로 보면 짝이 없어 경고가 난다.
    IsSensor   : bool
    InTag      : AiTagSpec option
    OutTag     : AiTagSpec option
    LineNumber : int
}

/// IO 태그 한 칸. `[심벌@]주소[:DataType][=기대값]`.
and AiTagSpec = {
    Symbol   : string option
    Address  : string
    DataType : string option
    Expected : string option
}

/// COND 행 — 조건 하나.
type AiCondRow = {
    /// true = Call 소유(4마디). false = Work 소유(2마디).
    IsCallLevel : bool
    /// 소유자 경로. Work 면 `Flow.Work`, Call 이면 `Flow.Work.Device.Api`.
    OwnerPath   : string
    CondType    : ConditionType
    Expr        : AiCondExpr
    LineNumber  : int
}

/// 파싱된 문서 전체. 행 순서는 보존하지 않는다(의미가 없으므로).
type AiCsvDocument = {
    Systems  : AiSystemRow list
    Flows    : AiFlowRow list
    Works    : AiWorkRow list
    Arrows   : AiArrowRow list
    Apis     : AiApiRow list
    Conds    : AiCondRow list
    /// 오류가 아닌 경고 — 불러오기는 진행되지만 사용자가 알아야 하는 것.
    Warnings : string list
}

/// 불러오기 전 미리보기. Capa 를 반드시 보여 준다 — Flow 개수를 공정 단계 수로 오해하는 것이
/// 이 형식에서 가장 흔한 모델링 실수이기 때문이다.
type AiCsvPreview = {
    ActiveSystemName   : string
    PassiveSystemNames : string list
    /// = Flow 행 개수. 동시에 보유하는 고유 제품 수.
    Capa               : int
    WorkCount          : int
    ArrowCount         : int
    ApiCount           : int
    CondCount          : int
    /// Time 을 적지 않아 기본값(500ms)이 들어간 API 수.
    UnspecifiedTimes   : int
    Warnings           : string list
}
