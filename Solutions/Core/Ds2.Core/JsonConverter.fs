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
    let private defaultOptions = JsonOptions.createProjectSerializationOptions ()
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

    let private writeCompressedUtf8 (filePath: string) (text: string) =
        let bytes = Encoding.UTF8.GetBytes(text)
        use file = File.Create(filePath)
        use gzip = new GZipStream(file, CompressionLevel.SmallestSize)
        gzip.Write(bytes, 0, bytes.Length)

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

    /// 파일에 JSON 저장
    let saveToFile<'T> (filePath: string) (value: 'T) : unit =
        let json = serialize value
        if isSdfFile filePath then
            writeCompressedUtf8 filePath json
        else
            File.WriteAllText(filePath, json, Encoding.UTF8)

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
