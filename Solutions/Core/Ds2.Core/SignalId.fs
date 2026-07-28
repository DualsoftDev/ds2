namespace Ds2.Core

open System

/// Identity of a single interaction data point within an asset.
/// Value carried by AID `InteractionMetadata.signalId` (DualSoft extension).
/// Adopted convention: `{lineId}.{assetId}.{shortName}` — kebab-case, lowercase.
[<Struct; CustomEquality; CustomComparison>]
type SignalId =
    val private raw: string

    new(value: string) = { raw = value }

    /// The underlying string (never null; empty means "unset").
    member this.Value =
        if isNull this.raw then "" else this.raw

    override this.ToString() = this.Value

    override this.Equals(other: obj) =
        match other with
        | :? SignalId as o -> this.Value = o.Value
        | _ -> false

    override this.GetHashCode() = this.Value.GetHashCode()

    interface IComparable with
        member this.CompareTo(other: obj) =
            match other with
            | :? SignalId as o -> String.CompareOrdinal(this.Value, o.Value)
            | _ -> invalidArg "other" "SignalId only comparable to SignalId"


/// SignalId construction and validation.
module SignalId =

    /// Maximum length permitted (arbitrary safety cap for storage keys).
    [<Literal>]
    let MaxLength = 128

    /// Try to construct a SignalId, returning Error if the value violates
    /// the format contract (Phase 0 rules — kept conservative).
    let tryCreate (raw: string) : Result<SignalId, string> =
        if isNull raw then Error "SignalId cannot be null"
        elif String.IsNullOrWhiteSpace raw then Error "SignalId cannot be empty"
        elif raw.Length > MaxLength then
            Error (sprintf "SignalId too long (max %d)" MaxLength)
        elif raw <> raw.ToLowerInvariant() then
            Error "SignalId must be lowercase"
        elif raw.StartsWith('.') || raw.EndsWith('.') then
            Error "SignalId cannot start or end with '.'"
        elif raw.Contains ' ' then
            Error "SignalId cannot contain whitespace"
        else
            let allowed =
                raw |> Seq.forall (fun c ->
                    (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') ||
                    c = '-' || c = '.' || c = '_')
            if not allowed then
                Error "SignalId may only contain [a-z0-9-_.]"
            else
                Ok (SignalId raw)

    /// Construct or throw ArgumentException.
    let create (raw: string) : SignalId =
        match tryCreate raw with
        | Ok id -> id
        | Error msg -> invalidArg "raw" msg

    /// The empty / unset sentinel.
    let empty = SignalId ""
