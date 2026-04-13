module Ds2.Aasx.FieldValidation

open System
open System.Reflection
open Ds2.Core

/// AasxFieldAttribute가 있는 엔티티의 모든 속성 추출
/// 반환: (PropertyName, AasxFieldName) 튜플 맵
let private getAasxFieldsForEntity (entityType: Type) =
    // 현재 타입과 모든 베이스 타입의 속성을 가져오기
    let rec getAllProperties (t: Type) =
        seq {
            yield! t.GetProperties(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly)
            if t.BaseType <> null && t.BaseType <> typeof<obj> then
                yield! getAllProperties t.BaseType
        }

    let allProps = getAllProperties entityType |> Seq.toArray
    allProps
    |> Array.filter (fun p -> p.Name <> "Properties")  // Properties 컬렉션만 제외
    |> Array.choose (fun p ->
        match p.GetCustomAttribute<AasxFieldAttribute>(true) |> box with  // inherit=true로 변경
        | null -> None
        | :? AasxFieldAttribute as attr when not attr.Skip ->
            Some (p.Name, attr.FieldName)
        | _ -> None)
    |> Map.ofArray

let getAasxFieldsForProject () = getAasxFieldsForEntity typeof<Project>
let getAasxFieldsForSystem () = getAasxFieldsForEntity typeof<DsSystem>
let getAasxFieldsForFlow () = getAasxFieldsForEntity typeof<Flow>
let getAasxFieldsForWork () = getAasxFieldsForEntity typeof<Work>
let getAasxFieldsForCall () = getAasxFieldsForEntity typeof<Call>
let getAasxFieldsForApiCall () = getAasxFieldsForEntity typeof<ApiCall>
let getAasxFieldsForApiDef () = getAasxFieldsForEntity typeof<ApiDef>

/// 엔티티별 검증 결과 출력
let private validateEntity (entityName: string) (getFields: unit -> Map<string, string>) =
    let fields = getFields ()
    if fields.IsEmpty then
        printfn "   %s: No [<AasxField>] attributes" entityName
    else
        printfn "   %s: %d fields" entityName fields.Count
        fields
        |> Map.iter (fun propName fieldName ->
            printfn "      - %s → \"%s\"" propName fieldName)

/// 모든 엔티티 검증
let validateAll () =
    printfn ""
    printfn "=== AASX Field Validation ==="
    printfn "✅ Using reflection-based auto-generation for Export/Import"
    printfn ""

    validateEntity "Project" getAasxFieldsForProject
    validateEntity "DsSystem" getAasxFieldsForSystem
    validateEntity "Flow" getAasxFieldsForFlow
    validateEntity "Work" getAasxFieldsForWork
    validateEntity "Call" getAasxFieldsForCall
    validateEntity "ApiCall" getAasxFieldsForApiCall
    validateEntity "ApiDef" getAasxFieldsForApiDef

    printfn ""
    printfn "✅ All fields will be automatically exported/imported via mkPropsFromAasxFields()"
    printfn "==="
    printfn ""
    true
