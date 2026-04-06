namespace Ds2.Aasx

open System
open AasCore.Aas3_0
open log4net
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxFileIO
open Ds2.Store

module internal AasxImportCore =

    let log = LogManager.GetLogger("Ds2.Aasx.AasxImporter")

    // ────────────────────────────────────────────────────────────────────────────
    // AAS SubmodelElement 파싱 헬퍼 함수들
    // ────────────────────────────────────────────────────────────────────────────

    /// Property 값을 안전하게 가져오기 (null → 빈 문자열)
    let valueOrEmpty (p: Property) = if p.Value = null then "" else p.Value

    /// SubmodelElementCollection에서 Property 값 추출
    let getProp (smc: SubmodelElementCollection) (idShort: string) : string option =
        if smc.Value = null then None
        else
            smc.Value
            |> Seq.tryPick (function
                | :? Property as p when p.IdShort = idShort ->
                    if p.Value = null then None else Some p.Value
                | _ -> None)

    /// JSON Property를 역직렬화
    let fromJsonProp<'T> (smc: SubmodelElementCollection) (idShort: string) : 'T option =
        getProp smc idShort
        |> Option.bind (fun json ->
            try Some (Ds2.Serialization.JsonConverter.deserialize<'T> json)
            with ex -> log.Warn($"JSON 역직렬화 실패: {idShort} — {ex.Message}", ex); None)

    /// SubmodelElementList의 모든 SMC 자식 요소 가져오기
    let getChildSmlSmcs (smc: SubmodelElementCollection) (idShort: string) : SubmodelElementCollection list =
        if smc.Value = null then []
        else
            smc.Value
            |> Seq.tryPick (function
                | :? SubmodelElementList as l when l.IdShort = idShort ->
                    if l.Value = null then Some []
                    else
                        Some (l.Value |> Seq.choose (function
                            | :? SubmodelElementCollection as c -> Some c
                            | _ -> None) |> Seq.toList)
                | _ -> None)
            |> Option.defaultValue []

    // ────────────────────────────────────────────────────────────────────────────
    // 타입 변환 함수들
    // ────────────────────────────────────────────────────────────────────────────

    /// ArrowType Enum 파싱
    let parseArrowType (s: string) : ArrowType =
        match Enum.TryParse<ArrowType>(s) with
        | true, v -> v
        | _ -> ArrowType.Unspecified

    /// Status4 Enum 파싱
    let parseStatus4 (s: string) : Status4 =
        match Enum.TryParse<Status4>(s) with
        | true, v -> v
        | _ -> Status4.Ready

    // ────────────────────────────────────────────────────────────────────────────
    // 유틸리티 함수들
    // ────────────────────────────────────────────────────────────────────────────

    /// SMC 요소를 설명하는 문자열 생성 (로깅용)
    let describeSmc (smc: SubmodelElementCollection) : string =
        let guidText = getProp smc Guid_ |> Option.defaultValue "<missing>"
        let nameText = getProp smc Name_ |> Option.defaultValue "<missing>"
        $"Guid={guidText}, Name={nameText}"

    /// 엄격한 리스트 파싱 (하나라도 실패하면 전체 None)
    let parseStrictList
        (ownerLabel: string)
        (itemLabel: string)
        (items: SubmodelElementCollection list)
        (parser: SubmodelElementCollection -> 'T option)
        : 'T list option =
        let rec loop acc rest =
            match rest with
            | [] -> Some(List.rev acc)
            | smc :: tail ->
                match parser smc with
                | Some value -> loop (value :: acc) tail
                | None ->
                    log.Error($"AASX import failed: invalid {itemLabel} under {ownerLabel} ({describeSmc smc}).")
                    None
        loop [] items

    // ────────────────────────────────────────────────────────────────────────────
    // SMC → Entity 변환 함수들
    // ────────────────────────────────────────────────────────────────────────────

    /// SMC를 Arrow 엔티티로 변환 (범용)
    let smcToArrow<'T when 'T :> DsArrow> (label: string) (smc: SubmodelElementCollection) (parentId: Guid)
                                                   (ctor: Guid -> Guid -> Guid -> ArrowType -> 'T) : 'T option =
        try
            match getProp smc Source_ |> Option.map Guid.Parse, getProp smc Target_ |> Option.map Guid.Parse with
            | Some sourceId, Some targetId ->
                let id        = getProp smc Guid_ |> Option.map Guid.Parse |> Option.defaultValue (Guid.NewGuid())
                let arrowType = getProp smc Type_  |> Option.map parseArrowType |> Option.defaultValue ArrowType.Unspecified
                let arrow = ctor parentId sourceId targetId arrowType
                arrow.Id <- id
                Some arrow
            | _ -> log.Warn($"{label}: Source 또는 Target 누락"); None
        with ex -> log.Warn($"{label} 실패: {ex.Message}", ex); None

    /// SMC를 ArrowBetweenCalls로 변환
    let smcToArrowCall smc workId   = smcToArrow "smcToArrowCall" smc workId   (fun p s t a -> ArrowBetweenCalls(p, s, t, a))

    /// SMC를 ArrowBetweenWorks로 변환
    let smcToArrowWork smc systemId = smcToArrow "smcToArrowWork" smc systemId (fun p s t a -> ArrowBetweenWorks(p, s, t, a))
