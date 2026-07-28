namespace Ds2.Core

open System

/// AAS semanticId. Backing form may be IRI, URN, or IRDI.
/// This type is a thin value wrapper — validation is intentionally minimal.
[<Struct; CustomEquality; CustomComparison>]
type SemanticId =
    val private raw: string

    new(value: string) = { raw = value }

    member this.Value =
        if isNull this.raw then "" else this.raw

    override this.ToString() = this.Value

    override this.Equals(other: obj) =
        match other with
        | :? SemanticId as o -> this.Value = o.Value
        | _ -> false

    override this.GetHashCode() = this.Value.GetHashCode()

    interface IComparable with
        member this.CompareTo(other: obj) =
            match other with
            | :? SemanticId as o -> String.CompareOrdinal(this.Value, o.Value)
            | _ -> invalidArg "other" "SemanticId only comparable to SemanticId"


module SemanticId =

    [<Literal>]
    let MaxLength = 2048

    let tryCreate (raw: string) : Result<SemanticId, string> =
        if isNull raw then Error "SemanticId cannot be null"
        elif String.IsNullOrWhiteSpace raw then Error "SemanticId cannot be empty"
        elif raw.Length > MaxLength then
            Error (sprintf "SemanticId too long (max %d)" MaxLength)
        elif raw.Contains ' ' then
            Error "SemanticId cannot contain whitespace"
        else
            Ok (SemanticId raw)

    let create (raw: string) : SemanticId =
        match tryCreate raw with
        | Ok id -> id
        | Error msg -> invalidArg "raw" msg

    let empty = SemanticId ""

    /// True iff the value looks like an IRDI (starts with digit + '-1#…' or '-2#…').
    let isIrdi (id: SemanticId) : bool =
        let v = id.Value
        v.Length > 4
        && Char.IsDigit v.[0]
        && (v.Contains "-1#" || v.Contains "-2#")

    /// True iff the value looks like an IRI (http/https).
    let isIri (id: SemanticId) : bool =
        let v = id.Value
        v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)

    /// True iff the value looks like a URN.
    let isUrn (id: SemanticId) : bool =
        id.Value.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)
