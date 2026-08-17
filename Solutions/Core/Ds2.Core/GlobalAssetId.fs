namespace Ds2.Core

open System

/// Globally unique asset identifier per IEC 63278.
/// Conventional forms: `urn:{namespace}:asset:{name}`, IRI, or IRDI.
/// Uniqueness is a caller / registry contract — this type only validates *shape*.
[<Struct; CustomEquality; CustomComparison>]
type GlobalAssetId =
    val private raw: string

    new(value: string) = { raw = value }

    member this.Value =
        if isNull this.raw then "" else this.raw

    override this.ToString() = this.Value

    override this.Equals(other: obj) =
        match other with
        | :? GlobalAssetId as o -> this.Value = o.Value
        | _ -> false

    override this.GetHashCode() = this.Value.GetHashCode()

    interface IComparable with
        member this.CompareTo(other: obj) =
            match other with
            | :? GlobalAssetId as o -> String.CompareOrdinal(this.Value, o.Value)
            | _ -> invalidArg "other" "GlobalAssetId only comparable to GlobalAssetId"


module GlobalAssetId =

    [<Literal>]
    let MaxLength = 2048

    /// Try to construct with shape validation only.
    let tryCreate (raw: string) : Result<GlobalAssetId, string> =
        if isNull raw then Error "GlobalAssetId cannot be null"
        elif String.IsNullOrWhiteSpace raw then Error "GlobalAssetId cannot be empty"
        elif raw.Length > MaxLength then
            Error (sprintf "GlobalAssetId too long (max %d)" MaxLength)
        elif raw.Contains ' ' then
            Error "GlobalAssetId cannot contain whitespace"
        else
            // Accept any URI-like scheme (urn:, http:, https:, file:, IRDI 0173-1#…, …).
            // Absolute correctness is deferred to consumer registries.
            Ok (GlobalAssetId raw)

    let create (raw: string) : GlobalAssetId =
        match tryCreate raw with
        | Ok id -> id
        | Error msg -> invalidArg "raw" msg

    let empty = GlobalAssetId ""

    /// True iff the shape resembles an IRDI (starts with digit + '-1#…').
    let isIrdi (id: GlobalAssetId) : bool =
        let v = id.Value
        v.Length > 4
        && Char.IsDigit v.[0]
        && v.Contains "-1#"

    /// True iff the shape resembles a URI/URN.
    let isUri (id: GlobalAssetId) : bool =
        let v = id.Value
        v.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)
        || v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
