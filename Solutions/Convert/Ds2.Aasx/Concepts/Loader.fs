namespace Ds2.Aasx

module AasxConceptDescriptionLoader =

    /// IRDI로 ConceptDescription 찾기
    let findByIrdi (irdi: string) (conceptDescriptions: AasCore.Aas3_1.IConceptDescription list) : AasCore.Aas3_1.IConceptDescription option =
        conceptDescriptions
        |> List.tryFind (fun cd -> cd.Id = irdi)
