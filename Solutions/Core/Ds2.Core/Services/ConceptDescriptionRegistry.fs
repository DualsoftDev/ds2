namespace Ds2.Core.Services

open System
open System.Collections.Concurrent
open System.Text.RegularExpressions
open Ds2.Core

/// Managed ConceptDescription registry.
///
/// Not a Submodel — a domain service. The authoritative registry is served by
/// `Ds2.AasHost` (Phase 2) via CD Repository REST; this in-memory implementation
/// is the local cache / development stub. ADR-008 governs the ID scheme.
[<AutoOpen>]
module ConceptDescriptionTypes =

    /// Lifecycle state of a managed CD.
    type CdStatus =
        | Active
        | Deprecated

    /// One ConceptDescription per ADR-008 (`urn:dualsoft:cd:{path}/{major}/{minor}`).
    type ConceptDescription = {
        Id: SemanticId
        PreferredName: Map<string, string>    // language code → name
        Definition: Map<string, string>       // language code → definition
        Unit: string option
        Status: CdStatus
        /// ECLASS IRDI once mapping is available (§04-B-6 migration path).
        EclassRef: string option
        /// IEC CDD IRDI likewise.
        IecCddRef: string option
        IssuedBy: string option
        IssuedAt: DateTimeOffset
    }

    /// Read/write API surface.
    type IConceptDescriptionRegistry =
        abstract Get: SemanticId -> ConceptDescription option
        abstract List: filter: string option -> ConceptDescription seq
        abstract Register: ConceptDescription -> Result<unit, string>
        abstract Deprecate: SemanticId -> Result<unit, string>


module ConceptDescription =

    /// ADR-008 CD id shape: `urn:dualsoft:cd:{path}/{major}/{minor}`.
    /// `path` is dot-separated, kebab-case. Version parts are non-negative ints.
    let private cdRegex =
        Regex(@"^urn:dualsoft:cd:[a-z0-9]+(?:[-.][a-z0-9]+)*(?:/[0-9]+){2}$",
              RegexOptions.Compiled ||| RegexOptions.CultureInvariant)

    /// True iff the SemanticId conforms to the DualSoft CD scheme.
    let isValidId (id: SemanticId) : bool =
        cdRegex.IsMatch(id.Value)


/// In-memory implementation of `IConceptDescriptionRegistry`.
type InMemoryConceptDescriptionRegistry() =
    let store = ConcurrentDictionary<SemanticId, ConceptDescription>()

    interface IConceptDescriptionRegistry with

        member _.Get id =
            match store.TryGetValue id with
            | true, cd -> Some cd
            | false, _ -> None

        member _.List filter =
            let all = store.Values :> seq<ConceptDescription>
            match filter with
            | None -> all
            | Some needle ->
                let needle = needle.ToLowerInvariant()
                all |> Seq.filter (fun cd ->
                    cd.Id.Value.ToLowerInvariant().Contains needle
                    || (cd.PreferredName |> Map.exists (fun _ v ->
                            v.ToLowerInvariant().Contains needle)))

        member _.Register cd =
            if not (ConceptDescription.isValidId cd.Id) then
                Error (sprintf "CD id '%s' violates ADR-008 scheme" cd.Id.Value)
            elif store.ContainsKey cd.Id then
                Error (sprintf "CD '%s' already registered — deprecate + issue new version" cd.Id.Value)
            else
                store.[cd.Id] <- cd
                Ok ()

        member _.Deprecate id =
            match store.TryGetValue id with
            | true, cd ->
                store.[id] <- { cd with Status = Deprecated }
                Ok ()
            | false, _ ->
                Error (sprintf "CD '%s' not found" id.Value)
