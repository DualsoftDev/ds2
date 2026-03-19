namespace Ds2.Core

/// Token value carried by a Work slot during simulation.
/// V1 uses IntToken only; extensible via new DU cases.
type TokenValue =
    | IntToken of int

/// Token specification — binds real data (recipe/product) to a token number.
/// Stored in DsProject.TokenSpecs.
type TokenSpec = {
    Id: int
    Label: string
    Fields: Map<string, string>
}
