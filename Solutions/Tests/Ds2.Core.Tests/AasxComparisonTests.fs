module AasxComparisonTests

open System
open System.IO
open System.IO.Compression
open System.Text
open Xunit
open Xunit.Abstractions

type AasxComparisonTests(output: ITestOutputHelper) =

    /// ZIP 엔트리 목록 읽기
    let readZipEntries (path: string) : Map<string, byte[]> =
        use fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
        use archive = new ZipArchive(fileStream, ZipArchiveMode.Read)
        archive.Entries
        |> Seq.map (fun entry ->
            use stream = entry.Open()
            use memStream = new MemoryStream()
            stream.CopyTo(memStream)
            (entry.FullName, memStream.ToArray())
        )
        |> Map.ofSeq

    /// 바이트 배열을 텍스트로 변환 (BOM 처리)
    let bytesToText (bytes: byte[]) : string =
        if bytes = null || bytes.Length = 0 then ""
        else
            use memStream = new MemoryStream(bytes)
            use reader = new StreamReader(memStream, Encoding.UTF8, detectEncodingFromByteOrderMarks = true)
            reader.ReadToEnd()

    /// 두 바이트 배열의 차이점 분석
    let analyzeByteDifferences (name: string) (bytes1: byte[]) (bytes2: byte[]) =
        if bytes1.Length <> bytes2.Length then
            let diff = bytes2.Length - bytes1.Length
            let diffStr = if diff > 0 then $"+{diff}" elif diff < 0 then $"{diff}" else "0"
            output.WriteLine($"  [{name}] 크기 차이: {bytes1.Length} vs {bytes2.Length} bytes ({diffStr} bytes)")

        let maxCheck = min bytes1.Length bytes2.Length
        let mutable firstDiff = -1
        for i = 0 to maxCheck - 1 do
            if bytes1.[i] <> bytes2.[i] && firstDiff = -1 then
                firstDiff <- i

        if firstDiff >= 0 then
            output.WriteLine($"  [{name}] 첫 바이트 차이 위치: {firstDiff}")
            let contextStart = max 0 (firstDiff - 20)
            let contextEnd = min maxCheck (firstDiff + 20)
            output.WriteLine($"  File1 context: {BitConverter.ToString(bytes1.[contextStart..contextEnd])}")
            output.WriteLine($"  File2 context: {BitConverter.ToString(bytes2.[contextStart..contextEnd])}")

    /// XML 정규화 (공백, 개행 무시)
    let normalizeXml (xml: string) : string =
        if String.IsNullOrWhiteSpace(xml) then ""
        else
            try
                let doc = System.Xml.XmlDocument()
                doc.LoadXml(xml.Trim())
                use sw = new StringWriter()
                use writer = System.Xml.XmlWriter.Create(sw, System.Xml.XmlWriterSettings(Indent = true, IndentChars = "  "))
                doc.Save(writer)
                sw.ToString()
            with
            | ex ->
                output.WriteLine($"XML 정규화 실패: {ex.Message}")
                xml

    /// XML 내용 비교 (구조적 차이 분석)
    let compareXmlContent (name: string) (xml1: string) (xml2: string) =
        try
            let doc1 = System.Xml.XmlDocument()
            let doc2 = System.Xml.XmlDocument()
            doc1.LoadXml(xml1.Trim())
            doc2.LoadXml(xml2.Trim())

            // 루트 요소 비교
            if doc1.DocumentElement.Name <> doc2.DocumentElement.Name then
                output.WriteLine($"  [{name}] 루트 요소 차이: {doc1.DocumentElement.Name} vs {doc2.DocumentElement.Name}")

            // 네임스페이스 비교
            if doc1.DocumentElement.NamespaceURI <> doc2.DocumentElement.NamespaceURI then
                output.WriteLine($"  [{name}] 네임스페이스 차이:")
                output.WriteLine($"    File1: {doc1.DocumentElement.NamespaceURI}")
                output.WriteLine($"    File2: {doc2.DocumentElement.NamespaceURI}")

            // 자식 요소 개수 비교
            let count1 = doc1.DocumentElement.ChildNodes.Count
            let count2 = doc2.DocumentElement.ChildNodes.Count
            if count1 <> count2 then
                output.WriteLine($"  [{name}] 자식 요소 개수 차이: {count1} vs {count2}")

        with
        | ex ->
            output.WriteLine($"  [{name}] XML 비교 실패: {ex.Message}")

    [<Fact>]
    let ``Compare NewProject3_1 aasx vs NewProject3_1 - saveas aasx`` () =
        let file1 = "/mnt/c/ds/NewProject3.1.aasx"
        let file2 = "/mnt/c/ds/NewProject3.1 - saveas.aasx"

        if not (File.Exists(file1)) then
            output.WriteLine($"File1 not found: {file1}")
            Assert.True(false, $"File1 not found: {file1}")

        if not (File.Exists(file2)) then
            output.WriteLine($"File2 not found: {file2}")
            Assert.True(false, $"File2 not found: {file2}")

        output.WriteLine("=".PadRight(80, '='))
        output.WriteLine("AASX 파일 비교 분석")
        output.WriteLine("=".PadRight(80, '='))
        output.WriteLine($"File1: {file1}")
        output.WriteLine($"File2: {file2}")
        output.WriteLine("")

        // 파일 크기 비교
        let size1 = FileInfo(file1).Length
        let size2 = FileInfo(file2).Length
        let sizeDiff = size2 - size1
        let sizeDiffStr = if sizeDiff > 0L then $"+{sizeDiff}" elif sizeDiff < 0L then $"{sizeDiff}" else "0"
        output.WriteLine($"파일 크기:")
        output.WriteLine($"  File1: {size1:N0} bytes")
        output.WriteLine($"  File2: {size2:N0} bytes")
        output.WriteLine($"  차이: {sizeDiffStr} bytes")
        output.WriteLine("")

        // ZIP 엔트리 읽기
        let entries1 = readZipEntries file1
        let entries2 = readZipEntries file2

        output.WriteLine($"ZIP 엔트리 개수:")
        output.WriteLine($"  File1: {entries1.Count} entries")
        output.WriteLine($"  File2: {entries2.Count} entries")
        output.WriteLine("")

        // 엔트리 목록 비교
        let keys1 = Set.ofSeq entries1.Keys
        let keys2 = Set.ofSeq entries2.Keys

        let onlyInFile1 = Set.difference keys1 keys2
        let onlyInFile2 = Set.difference keys2 keys1
        let common = Set.intersect keys1 keys2

        if not (Set.isEmpty onlyInFile1) then
            output.WriteLine($"File1에만 있는 엔트리 ({onlyInFile1.Count}):")
            for key in onlyInFile1 do
                output.WriteLine($"  - {key}")
            output.WriteLine("")

        if not (Set.isEmpty onlyInFile2) then
            output.WriteLine($"File2에만 있는 엔트리 ({onlyInFile2.Count}):")
            for key in onlyInFile2 do
                output.WriteLine($"  - {key}")
            output.WriteLine("")

        output.WriteLine($"공통 엔트리: {common.Count}")
        output.WriteLine("")

        // 공통 엔트리 내용 비교
        output.WriteLine("공통 엔트리 내용 비교:")
        output.WriteLine("-".PadRight(80, '-'))

        let mutable identicalCount = 0
        let mutable differentCount = 0

        for key in common do
            let bytes1 = entries1.[key]
            let bytes2 = entries2.[key]

            if bytes1 = bytes2 then
                identicalCount <- identicalCount + 1
            else
                differentCount <- differentCount + 1
                output.WriteLine($"\n[차이 발견] {key}")

                // XML 파일인지 확인
                if key.EndsWith(".xml") || key.EndsWith(".rels") then
                    let text1 = bytesToText bytes1
                    let text2 = bytesToText bytes2

                    if text1 <> text2 then
                        output.WriteLine($"  텍스트 내용 차이 감지")

                        // XML 구조 비교
                        compareXmlContent key text1 text2

                        // 정규화 후 비교
                        let norm1 = normalizeXml text1
                        let norm2 = normalizeXml text2

                        if norm1 = norm2 then
                            output.WriteLine($"  → 정규화 후 동일 (공백/개행 차이만 존재)")
                        else
                            output.WriteLine($"  → 정규화 후에도 다름 (구조적 차이 존재)")

                            // 텍스트 미리보기
                            let preview1 = if text1.Length > 500 then text1.Substring(0, 500) + "..." else text1
                            let preview2 = if text2.Length > 500 then text2.Substring(0, 500) + "..." else text2
                            output.WriteLine($"  File1 preview: {preview1}")
                            output.WriteLine($"  File2 preview: {preview2}")
                else
                    analyzeByteDifferences key bytes1 bytes2

        output.WriteLine("")
        output.WriteLine("-".PadRight(80, '-'))
        output.WriteLine($"동일한 엔트리: {identicalCount}")
        output.WriteLine($"다른 엔트리: {differentCount}")
        output.WriteLine("")

        // 핵심 메타데이터 파일 상세 분석
        output.WriteLine("=".PadRight(80, '='))
        output.WriteLine("핵심 메타데이터 파일 상세 분석")
        output.WriteLine("=".PadRight(80, '='))

        let criticalFiles = [
            "[Content_Types].xml"
            "_rels/.rels"
            "aasx/aas/aas.aas.xml"
        ]

        for file in criticalFiles do
            match entries1.TryFind(file), entries2.TryFind(file) with
            | Some bytes1, Some bytes2 ->
                output.WriteLine($"\n[{file}]")
                let text1 = bytesToText bytes1
                let text2 = bytesToText bytes2

                if text1 = text2 then
                    output.WriteLine($"  ✓ 동일")
                else
                    output.WriteLine($"  ✗ 차이 발견")
                    output.WriteLine($"    크기: {text1.Length} vs {text2.Length} chars")

                    // BOM 확인
                    if bytes1.Length >= 3 && bytes2.Length >= 3 then
                        let hasBom1 = bytes1.[0] = 239uy && bytes1.[1] = 187uy && bytes1.[2] = 191uy
                        let hasBom2 = bytes2.[0] = 239uy && bytes2.[1] = 187uy && bytes2.[2] = 191uy
                        output.WriteLine($"    BOM: {hasBom1} vs {hasBom2}")

                    compareXmlContent file text1 text2
            | None, Some _ ->
                output.WriteLine($"\n[{file}] File1에 없음")
            | Some _, None ->
                output.WriteLine($"\n[{file}] File2에 없음")
            | None, None ->
                output.WriteLine($"\n[{file}] 두 파일 모두 없음")

        output.WriteLine("")
        output.WriteLine("=".PadRight(80, '='))
        output.WriteLine("비교 완료")
        output.WriteLine("=".PadRight(80, '='))
