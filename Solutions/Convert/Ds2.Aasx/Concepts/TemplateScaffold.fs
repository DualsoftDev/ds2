namespace Ds2.Aasx

open System
open System.Collections.Generic
open AasCore.Aas3_1

/// Template-driven Submodel scaffold.
///
/// IDTA published .aasx 템플릿이 SM 의 구조/CD/semanticId/언어 슬롯을 정의하고,
/// 본 모듈은 그 위에 ds2 사용자 값만 inject 한다.
///
/// 핵심 함수:
///   setProp     : path → string  (Property.Value 설정)
///   setMlp      : path → (lang × text) seq (MLP 의 langString 교체)
///   expandSml   : path × N → 템플릿 첫 child 를 N 개로 복제
///   tryFindElem : path → ISubmodelElement option (직접 접근 필요 시)
///
/// path 형식: "/" 구분, root SM 의 SubmodelElements 부터 시작.
///   예: "Markings/Marking00/MarkingName"
///       "ContactInformation/Phone/TelephoneNumber"
module AasxTemplateScaffold =

    /// "/" 로 path 분해. 빈 segment 제외.
    let private splitPath (path: string) : string list =
        if String.IsNullOrEmpty path then []
        else
            path.Split([| '/' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList

    /// SubmodelElement 컨테이너에서 idShort 일치 자식 1개.
    let private findChild (parent: ISubmodelElement) (idShort: string) : ISubmodelElement option =
        match parent with
        | :? SubmodelElementCollection as smc when not (isNull smc.Value) ->
            smc.Value |> Seq.tryFind (fun e -> e.IdShort = idShort)
        | :? SubmodelElementList as sml when not (isNull sml.Value) ->
            sml.Value |> Seq.tryFind (fun e -> e.IdShort = idShort)
        | :? Entity as ent when not (isNull ent.Statements) ->
            ent.Statements |> Seq.tryFind (fun e -> e.IdShort = idShort)
        | _ -> None

    /// Submodel 의 top-level 에서 idShort 자식.
    let private findTopChild (sm: ISubmodel) (idShort: string) : ISubmodelElement option =
        if isNull sm.SubmodelElements then None
        else sm.SubmodelElements |> Seq.tryFind (fun e -> e.IdShort = idShort)

    /// path 를 따라 ISubmodelElement 추적.
    let tryFindElem (sm: ISubmodel) (path: string) : ISubmodelElement option =
        match splitPath path with
        | [] -> None
        | head :: tail ->
            findTopChild sm head
            |> Option.bind (fun first ->
                tail |> List.fold (fun acc seg ->
                    acc |> Option.bind (fun cur -> findChild cur seg)) (Some first))

    // ─── 값 setting ────────────────────────────────────────────────────────

    /// Property.Value 설정. 대상이 Property 가 아니면 false.
    let setProp (sm: ISubmodel) (path: string) (value: string) : bool =
        match tryFindElem sm path with
        | Some (:? Property as p) ->
            p.Value <- if isNull value then "" else value
            true
        | _ -> false

    /// MultiLanguageProperty 의 langString 들을 교체. langs = (language, text) seq.
    /// 빈 text 는 제외. langs 가 비어있으면 기존 유지.
    let setMlp (sm: ISubmodel) (path: string) (langs: (string * string) seq) : bool =
        match tryFindElem sm path with
        | Some (:? MultiLanguageProperty as mlp) ->
            let nonEmpty =
                langs
                |> Seq.choose (fun (l, t) ->
                    if String.IsNullOrEmpty t then None
                    else Some (LangStringTextType(l, t) :> ILangStringTextType))
                |> Seq.toList
            if nonEmpty.IsEmpty then true
            else
                mlp.Value <- ResizeArray<ILangStringTextType>(nonEmpty)
                true
        | _ -> false

    /// 단일 언어(en) 편의 오버로드.
    let setMlpEn (sm: ISubmodel) (path: string) (text: string) : bool =
        setMlp sm path [ "en", text ]

    /// File element 의 Value(파일 경로 또는 URL) 설정.
    /// contentType 이 None 이면 기존 값 유지 — 템플릿이 이미 적절한 값을 갖고 있다고 가정.
    let setFile (sm: ISubmodel) (path: string) (filePath: string) (contentType: string option) : bool =
        match tryFindElem sm path with
        | Some (:? File as f) ->
            f.Value <- if isNull filePath then "" else filePath
            match contentType with
            | Some ct -> f.ContentType <- ct
            | None -> ()
            true
        | _ -> false

    // ─── SML 확장 ─────────────────────────────────────────────────────────

    /// SML 의 template item (첫 자식) 을 deep copy.
    /// AAS Core Aas3_1 의 SubmodelElement 는 mutable POCO — 단순 직렬화/역직렬화 통한 복제.
    let private cloneElem (src: ISubmodelElement) : ISubmodelElement =
        // XmlSerializer 기반 round-trip — AasCore 가 JSON/XML 직접 디시리얼라이즈를 노출.
        use ms = new System.IO.MemoryStream()
        use xw = System.Xml.XmlWriter.Create(ms,
                    System.Xml.XmlWriterSettings(OmitXmlDeclaration = true, Indent = false))
        Xmlization.Serialize.``To``(src, xw)
        xw.Flush()
        ms.Position <- 0L
        use xr = System.Xml.XmlReader.Create(ms)
        xr.MoveToContent() |> ignore
        Xmlization.Deserialize.ISubmodelElementFrom(xr)

    /// SML path 를 찾아 template item 을 N 개로 복제 + idShort 자동 부여 (`{base}00`, `{base}01`...).
    /// itemIdShortBase 를 None 으로 두면 원본 idShort 유지.
    /// 반환: 복제된 item 들의 리스트 (호출 측이 이어서 path 로 값 setting).
    let expandSml
            (sm: ISubmodel) (path: string) (count: int)
            (itemIdShortBase: string option)
            : ISubmodelElement list =
        if count <= 0 then []
        else
            match tryFindElem sm path with
            | Some (:? SubmodelElementList as sml) when not (isNull sml.Value) && sml.Value.Count > 0 ->
                let template = sml.Value.[0]
                let newList = ResizeArray<ISubmodelElement>(count)
                for i in 0 .. count - 1 do
                    let copy = cloneElem template
                    match itemIdShortBase with
                    | Some baseName -> copy.IdShort <- sprintf "%s%02d" baseName i
                    | None -> ()
                    newList.Add(copy)
                sml.Value <- newList
                newList |> Seq.toList
            | _ -> []

    /// SML 의 모든 자식 비우기 (template item 도 제거 — caller 가 직접 추가).
    let clearSml (sm: ISubmodel) (path: string) : bool =
        match tryFindElem sm path with
        | Some (:? SubmodelElementList as sml) ->
            sml.Value <- ResizeArray<ISubmodelElement>()
            true
        | _ -> false

    /// Property-아이템 SML (예: Languages > Language) 을 ds2 string list 로 채움.
    /// 템플릿의 첫 child(Property template) 를 N 개 복제 후 각 .Value 설정.
    /// idShortBase 는 expandSml 과 동일 규칙으로 자동 부여.
    let setSmlOfStrings
            (sm: ISubmodel) (path: string) (values: string seq)
            (itemIdShortBase: string)
            : bool =
        let vals = values |> Seq.filter (fun v -> not (String.IsNullOrEmpty v)) |> Seq.toList
        if vals.IsEmpty then false
        else
            let items = expandSml sm path vals.Length (Some itemIdShortBase)
            if items.IsEmpty then false
            else
                List.iter2 (fun (item: ISubmodelElement) (v: string) ->
                    match item with
                    | :? Property as p -> p.Value <- v
                    | _ -> ()) items vals
                true

    /// SMC path 의 children (Value) 에 추가 element 들을 append.
    /// 템플릿이 빈 placeholder SMC (예: AddressInformation in Nameplate v3) 일 때 ds2 데이터를 inject 용.
    /// 기존 children 은 보존 — replace 가 필요하면 호출 측이 미리 비우기.
    let appendChildren (sm: ISubmodel) (path: string) (children: ISubmodelElement seq) : bool =
        match tryFindElem sm path with
        | Some (:? SubmodelElementCollection as smc) ->
            if isNull smc.Value then smc.Value <- ResizeArray<ISubmodelElement>()
            for c in children do smc.Value.Add(c)
            true
        | _ -> false

    /// File-아이템 SML (예: DigitalFiles > DigitalFile) 을 ds2 file path list 로 채움.
    /// contentTypeOfFn 은 각 path → contentType 을 결정하는 함수.
    let setSmlOfFiles
            (sm: ISubmodel) (path: string) (filePaths: string seq)
            (itemIdShortBase: string)
            (contentTypeOfFn: string -> string)
            : bool =
        let paths = filePaths |> Seq.filter (fun v -> not (String.IsNullOrEmpty v)) |> Seq.toList
        if paths.IsEmpty then false
        else
            let items = expandSml sm path paths.Length (Some itemIdShortBase)
            if items.IsEmpty then false
            else
                List.iter2 (fun (item: ISubmodelElement) (fp: string) ->
                    match item with
                    | :? File as f ->
                        f.Value <- fp
                        f.ContentType <- contentTypeOfFn fp
                    | _ -> ()) items paths
                true

    // ─── 값 reading (round-trip 용) ─────────────────────────────────────────

    /// Property.Value 읽기. 미존재/타입 불일치 시 None.
    let tryReadProp (sm: ISubmodel) (path: string) : string option =
        match tryFindElem sm path with
        | Some (:? Property as p) when not (isNull p.Value) -> Some p.Value
        | _ -> None

    /// MLP 의 (lang, text) 리스트.
    let tryReadMlp (sm: ISubmodel) (path: string) : (string * string) list =
        match tryFindElem sm path with
        | Some (:? MultiLanguageProperty as mlp) when not (isNull mlp.Value) ->
            mlp.Value
            |> Seq.choose (fun ls ->
                if isNull ls then None
                else Some (ls.Language, ls.Text))
            |> Seq.toList
        | _ -> []

    /// MLP 의 단일 언어(en 우선, 없으면 첫 항목) 텍스트.
    let tryReadMlpEn (sm: ISubmodel) (path: string) : string option =
        let items = tryReadMlp sm path
        items
        |> List.tryFind (fun (l, _) ->
            String.Equals(l, "en", StringComparison.OrdinalIgnoreCase))
        |> Option.map snd
        |> Option.orElseWith (fun () ->
            items |> List.tryHead |> Option.map snd)

    /// File element 의 Value (경로/URL) 읽기.
    let tryReadFile (sm: ISubmodel) (path: string) : string option =
        match tryFindElem sm path with
        | Some (:? File as f) when not (isNull f.Value) -> Some f.Value
        | _ -> None

    /// SML 의 자식 element 리스트 (각 item path 추가 탐색용).
    let tryReadSmlItems (sm: ISubmodel) (path: string) : ISubmodelElement list =
        match tryFindElem sm path with
        | Some (:? SubmodelElementList as sml) when not (isNull sml.Value) ->
            sml.Value |> Seq.toList
        | _ -> []

    // ─── Submodel id 재설정 ───────────────────────────────────────────────

    /// 템플릿 SM 의 id 를 ds2 project-specific URN 으로 변경.
    /// 템플릿 원본 id (`https://admin-shell.io/idta/SubmodelTemplate/...`) 는 instance 화 후 유지하지 않는 게 표준.
    let assignInstanceId (sm: ISubmodel) (newId: string) : unit =
        sm.Id <- newId
