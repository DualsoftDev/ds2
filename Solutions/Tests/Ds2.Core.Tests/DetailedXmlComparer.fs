module DetailedXmlComparer

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Xml
open System.Xml.Linq
open Xunit
open Xunit.Abstractions

type DetailedXmlComparer(output: ITestOutputHelper) =

    let readAasXmlFromAasx (path: string) : string =
        use fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        use archive = new ZipArchive(fileStream, ZipArchiveMode.Read)
        let entry = archive.GetEntry("aasx/aas/aas.aas.xml")
        use stream = entry.Open()
        use memStream = new MemoryStream()
        stream.CopyTo(memStream)
        let bytes = memStream.ToArray()
        use reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks = true)
        reader.ReadToEnd()

    let rec compareNodes (path: string) (node1: XNode) (node2: XNode) : bool =
        match node1, node2 with
        | (:? XElement as e1), (:? XElement as e2) ->
            // 요소 이름 비교
            if e1.Name <> e2.Name then
                output.WriteLine($"[{path}] 요소 이름 차이: {e1.Name} vs {e2.Name}")
                false
            // 속성 비교
            elif e1.Attributes() |> Seq.length <> (e2.Attributes() |> Seq.length) then
                output.WriteLine($"[{path}] 속성 개수 차이: {e1.Attributes() |> Seq.length} vs {e2.Attributes() |> Seq.length}")
                false
            elif e1.Attributes() |> Seq.exists (fun a1 ->
                let a2 = e2.Attribute(a1.Name)
                a2 = null || a2.Value <> a1.Value) then
                output.WriteLine($"[{path}] 속성 값 차이")
                for a1 in e1.Attributes() do
                    let a2 = e2.Attribute(a1.Name)
                    if a2 = null then
                        output.WriteLine($"  {a1.Name}: '{a1.Value}' vs (없음)")
                    elif a2.Value <> a1.Value then
                        output.WriteLine($"  {a1.Name}: '{a1.Value}' vs '{a2.Value}'")
                false
            // 자식 노드 개수 비교
            elif e1.Nodes() |> Seq.length <> (e2.Nodes() |> Seq.length) then
                output.WriteLine($"[{path}] 자식 노드 개수 차이: {e1.Nodes() |> Seq.length} vs {e2.Nodes() |> Seq.length}")

                // 어떤 요소가 다른지 상세 출력
                let children1 = e1.Elements() |> Seq.map (fun e -> e.Name.LocalName) |> Seq.toList
                let children2 = e2.Elements() |> Seq.map (fun e -> e.Name.LocalName) |> Seq.toList

                output.WriteLine($"  File1 자식 요소들:")
                for (i, name) in children1 |> List.indexed do
                    output.WriteLine($"    [{i}] {name}")

                output.WriteLine($"  File2 자식 요소들:")
                for (i, name) in children2 |> List.indexed do
                    output.WriteLine($"    [{i}] {name}")

                false
            // 자식이 없고 텍스트만 있는 경우
            elif not (e1.HasElements) && not (e2.HasElements) then
                if e1.Value <> e2.Value then
                    output.WriteLine($"[{path}] 텍스트 값 차이:")
                    output.WriteLine($"  File1: '{e1.Value}'")
                    output.WriteLine($"  File2: '{e2.Value}'")
                    false
                else
                    true
            // 자식 노드들을 재귀적으로 비교
            else
                let nodes1 = e1.Nodes() |> Seq.toList
                let nodes2 = e2.Nodes() |> Seq.toList
                List.zip nodes1 nodes2
                |> List.mapi (fun i (n1, n2) -> compareNodes $"{path}/{e1.Name.LocalName}[{i}]" n1 n2)
                |> List.forall id

        | (:? XText as t1), (:? XText as t2) ->
            let v1 = t1.Value.Trim()
            let v2 = t2.Value.Trim()
            if v1 <> v2 then
                output.WriteLine($"[{path}] 텍스트 차이: '{v1}' vs '{v2}'")
                false
            else
                true

        | _ ->
            output.WriteLine($"[{path}] 노드 타입 차이: {node1.GetType().Name} vs {node2.GetType().Name}")
            false

    [<Fact>]
    let ``Detailed XML comparison of aas_aas_xml`` () =
        let file1 = "/mnt/c/ds/NewProject3.1.aasx"
        let file2 = "/mnt/c/ds/NewProject3.1 - saveas.aasx"

        output.WriteLine("=".PadRight(80, '='))
        output.WriteLine("상세 XML 비교 분석")
        output.WriteLine("=".PadRight(80, '='))
        output.WriteLine($"File1: {file1}")
        output.WriteLine($"File2: {file2}")
        output.WriteLine("")

        let xml1 = readAasXmlFromAasx file1
        let xml2 = readAasXmlFromAasx file2

        output.WriteLine($"XML 크기:")
        output.WriteLine($"  File1: {xml1.Length:N0} characters")
        output.WriteLine($"  File2: {xml2.Length:N0} characters")
        output.WriteLine($"  차이: {xml2.Length - xml1.Length} characters")
        output.WriteLine("")

        // 문자열 직접 비교
        if xml1 = xml2 then
            output.WriteLine("✓ XML 내용이 완전히 동일합니다")
        else
            output.WriteLine("✗ XML 내용이 다릅니다")
            output.WriteLine("")

            // 바이트 단위 비교로 첫 차이점 찾기
            let bytes1 = Encoding.UTF8.GetBytes(xml1)
            let bytes2 = Encoding.UTF8.GetBytes(xml2)

            output.WriteLine($"바이트 크기:")
            output.WriteLine($"  File1: {bytes1.Length:N0} bytes")
            output.WriteLine($"  File2: {bytes2.Length:N0} bytes")
            output.WriteLine("")

            let mutable firstDiff = -1
            let minLen = min bytes1.Length bytes2.Length
            for i = 0 to minLen - 1 do
                if bytes1.[i] <> bytes2.[i] && firstDiff = -1 then
                    firstDiff <- i

            if firstDiff >= 0 then
                output.WriteLine($"첫 번째 차이 위치: 바이트 {firstDiff}")

                // 주변 컨텍스트 출력
                let contextStart = max 0 (firstDiff - 100)
                let contextEnd = min minLen (firstDiff + 100)

                let context1 = xml1.Substring(contextStart, contextEnd - contextStart)
                let context2 = xml2.Substring(contextStart, contextEnd - contextStart)

                output.WriteLine($"File1 컨텍스트 (위치 {contextStart}-{contextEnd}):")
                output.WriteLine($"  {context1}")
                output.WriteLine($"File2 컨텍스트 (위치 {contextStart}-{contextEnd}):")
                output.WriteLine($"  {context2}")
                output.WriteLine("")

            // XML 파싱 및 구조적 비교
            try
                let doc1 = XDocument.Parse(xml1)
                let doc2 = XDocument.Parse(xml2)

                output.WriteLine("XML 구조적 비교:")
                output.WriteLine("-".PadRight(80, '-'))

                let identical = compareNodes "/environment" doc1.Root doc2.Root

                output.WriteLine("-".PadRight(80, '-'))

                if identical then
                    output.WriteLine("✓ XML 구조가 동일합니다")
                else
                    output.WriteLine("✗ XML 구조에 차이가 있습니다")

            with
            | ex ->
                output.WriteLine($"XML 파싱 오류: {ex.Message}")

        output.WriteLine("")
        output.WriteLine("=".PadRight(80, '='))
        output.WriteLine("분석 완료")
        output.WriteLine("=".PadRight(80, '='))
