namespace Ds2.Serialization

open System
open System.IO
open System.IO.Compression
open System.Text
open System.Text.Json
open Ds2.Core

/// <summary>
/// System.Text.Json을 사용한 JSON 직렬화/역직렬화
/// </summary>
module JsonConverter =

    /// JSON 직렬화 옵션
    let private defaultOptions =
        let options = JsonOptions.createProjectSerializationOptions ()
        let resolver = Serialization.Metadata.DefaultJsonTypeInfoResolver()
        resolver.Modifiers.Add(fun info ->
            if info.Type = typeof<Project> then
                // A missing file timestamp is not the time of loading. Keep the
                // legacy Text sentinel deterministic; explicit JSON overwrites it.
                // Ordinary Project construction still uses DateTimeOffset.Now.
                info.OnDeserializing <- Action<obj>(fun value ->
                    (value :?> Project).DateTime <- DateTimeOffset.UnixEpoch))
        options.TypeInfoResolver <- resolver
        options
    let private sdfExtension = ".sdf"

    let private hasExtension (filePath: string) (extension: string) =
        Path.GetExtension(filePath).Equals(extension, StringComparison.OrdinalIgnoreCase)

    let private isSdfFile (filePath: string) =
        hasExtension filePath sdfExtension

    let private isGZipFile (filePath: string) =
        use stream = File.OpenRead(filePath)
        if stream.Length < 2L then false
        else
            stream.ReadByte() = 0x1f && stream.ReadByte() = 0x8b

    let private writeCompressedUtf8 (stream: Stream) (text: string) =
        let bytes = Encoding.UTF8.GetBytes(text)
        use gzip = new GZipStream(stream, CompressionLevel.SmallestSize, true)
        gzip.Write(bytes, 0, bytes.Length)

    /// File.WriteAllText(path, text, Encoding.UTF8) 과 같은 바이트(UTF-8 BOM 포함)를 스트림에 쓴다.
    let private writeUtf8 (stream: Stream) (text: string) =
        use writer = new StreamWriter(stream, Encoding.UTF8, 4096, true)
        writer.Write(text)

    let private readCompressedUtf8 (filePath: string) =
        use file = File.OpenRead(filePath)
        use gzip = new GZipStream(file, CompressionMode.Decompress)
        use reader = new StreamReader(gzip, Encoding.UTF8)
        reader.ReadToEnd()

    /// 객체를 JSON 문자열로 직렬화
    let serialize<'T> (value: 'T) : string =
        JsonSerializer.Serialize(value, defaultOptions)

    /// JSON 문자열을 객체로 역직렬화
    let deserialize<'T> (json: string) : 'T =
        JsonSerializer.Deserialize<'T>(json, defaultOptions)

    /// 파일에 JSON 저장 — 원자적 교체(AtomicFile). 쓰는 도중 실패해도 기존 파일이 깨지지 않는다.
    let saveToFile<'T> (filePath: string) (value: 'T) : unit =
        let json = serialize value
        AtomicFile.write filePath (fun stream ->
            if isSdfFile filePath then writeCompressedUtf8 stream json
            else writeUtf8 stream json)

    /// 파일에서 JSON 로드
    let loadFromFile<'T> (filePath: string) : 'T =
        let json =
            if isSdfFile filePath && isGZipFile filePath then
                readCompressedUtf8 filePath
            else
                File.ReadAllText(filePath, Encoding.UTF8)
        deserialize<'T> json

/// <summary>
/// Project 전용 직렬화 헬퍼
/// </summary>
module ProjectSerializer =

    /// Project를 JSON 파일로 저장
    let saveProject (filePath: string) (project: Project) : unit =
        JsonConverter.saveToFile filePath project

    /// JSON 파일에서 Project 로드
    let loadProject (filePath: string) : Project =
        JsonConverter.loadFromFile filePath
