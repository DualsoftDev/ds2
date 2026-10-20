namespace Ds2.Core

open System
open System.Text.Json

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
    /// 연결된 Source Work Guid (None이면 미연결)
    WorkId: System.Guid option
}

/// DS2 Text 5 — `skip when token.<Field> ==|!=|in …` 의 잎. 제품 사양(TokenSpec.Fields)만 읽는다.
/// `Values` 는 글에 적힌 JSON 리터럴(true · 25 · "B") 그대로이고, `Kind` 는 "bool" | "number" | "string",
/// `Op` 는 "eq" | "ne" | "in" 이다. TokenSpec.Fields 의 값도 같은 JSON 리터럴 글이다.
/// 이 잎이 있는 조건에는 ApiCalls 가 없다. 런타임은 **Skip 자리에서만** 평가한다(#8938) — 시작하는 Work(Call 이면 부모 Work)가
/// 쥔 토큰의 사양(SimState.TokenSpecIds)을 읽고, 사양을 모르면 거짓(건너뛰지 않음)이다. Skip 밖의 token 잎은 SimIndex 가 거절한다.
type TokenPredicate = {
    Field: string
    Op: string
    Kind: string
    Values: JsonElement list
}

/// DS2 Text 5 — `when variable.<Name> …` / `while variable.<Name> …` 의 잎. 같은 active System 의 변수(DsVariable)를 읽는다.
/// 이 잎이 있는 조건에는 ApiCalls 가 없다. **런타임은 아직 이 잎을 평가하지 못한다.**
type VariableRef = {
    VariableId: Guid
    Name: string
    InputSpec: ValueSpec
}
