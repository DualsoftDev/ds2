namespace Ds2.Serialization

open System.Text.Json
open System.Text.Json.Serialization
open Ds2.Core

/// <summary>
/// System.Text.Json을 사용한 JSON 직렬화/역직렬화
/// </summary>
module JsonConverter =

    /// JSON 직렬화 옵션
    let private defaultOptions =
        let options = JsonSerializerOptions()
        options.WriteIndented <- true
        options.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
        options.PropertyNameCaseInsensitive <- true  // 역직렬화 시 대소문자 무시
        options.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
        options.IncludeFields <- true  // private/internal 필드 포함

        // JsonFSharpConverter: internal 멤버 포함 설정
        let fsharpOptions = JsonFSharpOptions.Default().WithIncludeRecordProperties(true)
        options.Converters.Add(JsonFSharpConverter(fsharpOptions))
        options

    /// 객체를 JSON 문자열로 직렬화
    let serialize<'T> (value: 'T) : string =
        JsonSerializer.Serialize(value, defaultOptions)

    /// JSON 문자열을 객체로 역직렬화
    let deserialize<'T> (json: string) : 'T =
        JsonSerializer.Deserialize<'T>(json, defaultOptions)

    /// 파일에 JSON 저장
    let saveToFile<'T> (filePath: string) (value: 'T) : unit =
        let json = serialize value
        System.IO.File.WriteAllText(filePath, json)

    /// 파일에서 JSON 로드
    let loadFromFile<'T> (filePath: string) : 'T =
        let json = System.IO.File.ReadAllText(filePath)
        deserialize<'T> json

    /// Pretty print JSON
    let prettyPrint (json: string) : string =
        let doc = JsonDocument.Parse(json)
        JsonSerializer.Serialize(doc, defaultOptions)

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

    /// Project를 JSON 문자열로 변환
    let projectToJson (project: Project) : string =
        JsonConverter.serialize project

    /// JSON 문자열에서 Project 생성
    let projectFromJson (json: string) : Project =
        JsonConverter.deserialize json
