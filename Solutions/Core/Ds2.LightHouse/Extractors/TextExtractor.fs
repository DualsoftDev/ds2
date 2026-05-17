namespace Ds2.LightHouse.Extractors

open System
open System.IO
open System.Threading
open Ds2.LightHouse

/// TXT / MD / 일반 텍스트 (.log / .csv / .json / .yaml 등) extractor.
///
/// 책임: 인코딩 추정 (`TextEncoding.detectEncoding`) → decode → 단락 단위 segment 생성.
/// Markdown 의 `# heading` outline 추출은 Phase 1 simple — `^#{1,6}\s` line 단위만 인식. nested table / front-matter 무시.
/// chunk 의 token 단위 분할은 Chunker 책임 (§3.8).
type TextExtractor() =

    /// Markdown heading line ("^#{1,6}\s+title") 파서. depth = '#' 개수, label = trim 된 title.
    /// 비 heading line 은 None.
    static member private TryParseHeading (line: string) : (int * string) option =
        let trimmed = line.TrimStart()
        if String.IsNullOrEmpty trimmed || trimmed.[0] <> '#' then None
        else
            let mutable depth = 0
            while depth < trimmed.Length && trimmed.[depth] = '#' do
                depth <- depth + 1
            if depth = 0 || depth > 6 then None
            elif depth = trimmed.Length || not (Char.IsWhiteSpace trimmed.[depth]) then None
            else
                let label = trimmed.Substring(depth).Trim()
                if String.IsNullOrEmpty label then None
                else Some (depth, label)

    /// 한 문단 = 빈 line 으로 구분된 line 그룹. 빈 문단은 skip.
    static member private SplitParagraphs (text: string) : string array =
        text.Split([| "\r\n\r\n"; "\n\n" |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun p -> p.Trim())
        |> Array.filter (fun p -> p.Length > 0)

    interface IExtractor with
        member _.Supports kind =
            match kind with
            | Text | Markdown -> true
            | _ -> false

        member _.Extract (path, ct) =
            ct.ThrowIfCancellationRequested()
            let bytes = File.ReadAllBytes path
            let detect = TextEncoding.detectEncoding bytes
            let content = detect.Encoding.GetString bytes

            let kind =
                match Path.GetExtension(path).ToLowerInvariant() with
                | ".md" | ".markdown" -> Markdown
                | _ -> Text

            let outline =
                if kind = Markdown then
                    content.Split([| "\r\n"; "\n" |], StringSplitOptions.None)
                    |> Array.choose TextExtractor.TryParseHeading
                    |> Array.mapi (fun i (depth, label) ->
                        // depth 는 1~6. parent linking 은 단순화 — 동일 depth heading 까지 root 분리.
                        // (정밀 parent 계층은 Chunker 단계 또는 Phase 2 강화. Phase 1 은 flat outline.)
                        ignore depth
                        {
                            ParentIndex = None
                            Ordinal = i
                            NodeType = OutlineNodeType.Heading
                            Label = label
                            RefLocator = sprintf "p=%d" (i + 1)
                        })
                else
                    [||]

            let segments =
                TextExtractor.SplitParagraphs content
                |> Array.mapi (fun i paragraph ->
                    {
                        OutlineIndex = None
                        RefLocator = sprintf "p=%d" (i + 1)
                        Text = paragraph
                    })

            {
                DocType = kind
                PageOrSheetCnt = None
                Title = None
                Outline = outline
                Segments = segments
            }

        member _.Dispose () = ()
