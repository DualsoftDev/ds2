namespace Ds2.CSV

/// ds2-basic-csv/v1 — 3열(FLOW,WORK,CALL) 기본 생성 계약의 파싱 결과 타입.
/// 계약 문서: DualSoftAI docs/workFlowLLM/DS2_BASIC_CSV_AI_GUIDE.md
type BasicCsvWork = {
    FlowName   : string
    WorkName   : string
    /// (노드 키 = "디바이스.액션", 디바이스, 액션) — CALL 셀 최초 등장순.
    /// 동일 이름 노드는 병합되어 1회만 등장한다(같은 이름 반복 = 자동 병합, 별칭 문법 없음).
    Nodes      : (string * string * string) list
    /// (source 노드 키, target 노드 키) — '>' 인접쌍, 최초 등장순, 중복 제거 완료.
    Edges      : (string * string) list
    LineNumber : int
}

type BasicCsvDocument = {
    Works    : BasicCsvWork list
    /// (디바이스, 액션) → 동작 시간. CALL 토큰의 '(1000MS)' / '(2.5S)' 접미사에서 수집.
    /// 미지정 디바이스 API 는 이 맵에 없으며, 매퍼가 기본값(500ms)을 쓴다.
    /// 같은 Call 이 여러 번 나오면서 서로 다른 시간을 지정하면 DUR002 오류(파서에서 차단).
    Durations : ((string * string) * System.TimeSpan) list
    /// 오류가 아닌 경고(CSV005 FLOW 비연속 등).
    Warnings : string list
}

type BasicCsvPreview = {
    FlowNames          : string list
    WorkNames          : string list
    PassiveSystemNames : string list
    CallNodeCount      : int
    CallEdgeCount      : int
    /// 행 순서 StartReset 체인 화살표 수 = max(WorkCount-1, 0)
    WorkArrowCount     : int
}
