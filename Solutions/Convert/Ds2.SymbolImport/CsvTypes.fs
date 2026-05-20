namespace Ds2.SymbolImport

/// <summary>PLC 심볼 import 의 vendor / 입출력 방향 / 통합 entry 정의.</summary>
[<AutoOpen>]
module CsvTypes =

    /// 지원 vendor — DS1 mapper 가 LS XG5000 + Mitsubishi 만 있어 그 두 vendor 부터.
    /// AB (Allen-Bradley) 등 추후 확장.
    type Vendor =
        | Mitsubishi
        | XG5000      // LS 산전
        | AB          // Allen-Bradley (확장 예약, 현재 parser 미구현)

    /// 심볼 입출력 방향 — X (input) / Y (output) / M (memory) / 기타.
    type SymbolDirection =
        | Input        // X / IW — PLC 입력 (센서 등)
        | Output       // Y / QW — PLC 출력 (액추에이터 등)
        | Memory       // M / MW — 내부 메모리
        | UnknownDir

    /// vendor-neutral 심볼 entry — vendor 별 parser 가 이 record 로 변환 후 Mapper 공통 처리.
    type SymbolEntry = {
        /// 원본 PLC 주소 (예: "X10", "Y20", "%MW100"). vendor 표기 그대로 보존.
        Address: string
        /// 심볼 이름 (예: "Conv1_Adv_LMT", "FEEDER_PB_START").
        Name: string
        /// 입출력 방향 — 주소 prefix 기반 추론.
        Direction: SymbolDirection
        /// 코멘트 / 설명 — PLC 측 사용자 코멘트 필드.
        Comment: string
        /// vendor — DS1 mapper 위임 시 vendor 별 dispatch 위해 보존.
        Vendor: Vendor
    }

    /// CSV parse 결과 — 성공 시 entry list + warning, 실패 시 error message.
    type CsvParseResult = {
        Entries: SymbolEntry list
        Warnings: string list
    }
