namespace Ds2.Aasx

open System
open AasCore.Aas3_0
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxConceptDescriptions
open Ds2.Aasx.AasxFileIO
open Ds2.Store

module internal AasxExportCore =

    let mkProp (idShort: string) (value: string) : ISubmodelElement =
        let p = Property(valueType = DataTypeDefXsd.String)
        p.IdShort <- idShort
        p.Value <- if isNull value then "" else value
        p :> ISubmodelElement

    let mkJsonProp<'T> (idShort: string) (obj: 'T) : ISubmodelElement =
        mkProp idShort (Ds2.Serialization.JsonConverter.serialize obj)

    let mkSmc (idShort: string) (elems: ISubmodelElement list) : ISubmodelElement =
        let smc = SubmodelElementCollection()
        smc.IdShort <- idShort
        smc.Value <- ResizeArray<ISubmodelElement>(elems)
        smc :> ISubmodelElement

    let mkSml (idShort: string) (items: ISubmodelElement list) : ISubmodelElement =
        let sml = SubmodelElementList(typeValueListElement = AasSubmodelElements.SubmodelElementCollection)
        sml.IdShort <- idShort
        sml.Value <- ResizeArray<ISubmodelElement>(items)
        sml :> ISubmodelElement

    let mkSmlProp (idShort: string) (items: ISubmodelElement list) : ISubmodelElement =
        let sml = SubmodelElementList(typeValueListElement = AasSubmodelElements.Property)
        sml.IdShort <- idShort
        sml.Value <- ResizeArray<ISubmodelElement>(items)
        sml :> ISubmodelElement

    /// MultiLanguageProperty — 단일 언어(en)만 지원
    let mkMlp (idShort: string) (value: string) : ISubmodelElement =
        let mlp = MultiLanguageProperty()
        mlp.IdShort <- idShort
        let v = if isNull value then "" else value
        mlp.Value <- ResizeArray<ILangStringTextType>([LangStringTextType("en", v) :> ILangStringTextType])
        mlp :> ISubmodelElement

    let mkSemanticRef (semanticId: string) : IReference =
        Reference(
            ReferenceTypes.ExternalReference,
            ResizeArray<IKey>([Key(KeyTypes.GlobalReference, semanticId) :> IKey])) :> IReference

    let mkSubmodel (id: string) (idShort: string) (semanticId: string) (elems: ISubmodelElement list) : Submodel =
        let sm = Submodel(id = id)
        sm.IdShort <- idShort
        sm.SemanticId <- mkSemanticRef semanticId
        sm.SubmodelElements <- ResizeArray<ISubmodelElement>(elems)
        sm

    // ── 변환 계층 ──────────────────────────────────────────────────────────────
