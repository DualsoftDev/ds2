namespace Ds2.Aasx

open System
open System.IO
open System.Text.Json
open AasCore.Aas3_1

module AasxConceptDescriptionLoader =

    /// IDTA 공식 Submodel Template JSON에서 ConceptDescription 로드
    /// JSON 형식: { "conceptDescriptions": [...] }
    let loadFromJson (jsonPath: string) : IConceptDescription list =
        if not (File.Exists(jsonPath)) then
            printfn $"Warning: ConceptDescription JSON not found: {jsonPath}"
            []
        else
            try
                let jsonText = File.ReadAllText(jsonPath)
                let doc = JsonDocument.Parse(jsonText)
                let root = doc.RootElement

                match root.TryGetProperty("conceptDescriptions") with
                | true, cdArray when cdArray.ValueKind = JsonValueKind.Array ->
                    let mutable result = []
                    for cdElement in cdArray.EnumerateArray() do
                        try
                            // AasCore JSON deserializer 사용
                            let cdJson = cdElement.GetRawText()
                            let cd = Jsonization.Deserialize.ConceptDescriptionFrom(cdJson)
                            result <- (cd :> IConceptDescription) :: result
                        with ex ->
                            printfn $"Warning: Failed to deserialize ConceptDescription: {ex.Message}"
                    List.rev result
                | _ ->
                    printfn "Warning: 'conceptDescriptions' array not found in JSON"
                    []
            with ex ->
                printfn $"Error loading ConceptDescriptions from {jsonPath}: {ex.Message}"
                []

    /// 임베디드 리소스에서 ConceptDescription 로드
    let loadFromEmbeddedResource (assembly: Reflection.Assembly) (resourceName: string) : IConceptDescription list =
        try
            use stream = assembly.GetManifestResourceStream(resourceName)
            if isNull stream then
                printfn $"Warning: Embedded resource not found: {resourceName}"
                []
            else
                use reader = new StreamReader(stream)
                let jsonText = reader.ReadToEnd()
                let doc = JsonDocument.Parse(jsonText)
                let root = doc.RootElement

                match root.TryGetProperty("conceptDescriptions") with
                | true, cdArray when cdArray.ValueKind = JsonValueKind.Array ->
                    let mutable result = []
                    for cdElement in cdArray.EnumerateArray() do
                        try
                            let cdJson = cdElement.GetRawText()
                            let cd = Jsonization.Deserialize.ConceptDescriptionFrom(cdJson)
                            result <- (cd :> IConceptDescription) :: result
                        with ex ->
                            printfn $"Warning: Failed to deserialize ConceptDescription: {ex.Message}"
                    List.rev result
                | _ ->
                    printfn "Warning: 'conceptDescriptions' array not found in embedded resource"
                    []
        with ex ->
            printfn $"Error loading embedded ConceptDescriptions {resourceName}: {ex.Message}"
            []

    /// IRDI로 ConceptDescription 찾기
    let findByIrdi (irdi: string) (conceptDescriptions: IConceptDescription list) : IConceptDescription option =
        conceptDescriptions
        |> List.tryFind (fun cd -> cd.Id = irdi)
