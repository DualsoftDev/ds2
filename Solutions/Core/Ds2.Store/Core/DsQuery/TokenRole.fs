namespace Ds2.Store.DsQuery

open System
open Ds2.Core

/// TokenRole 헬퍼 + Work 이름 파싱
module TokenRoleOps =

    /// <summary>Work 이름을 "FlowName." 접두사와 LocalName으로 분리</summary>
    /// <returns>(prefix, localName) — prefix는 "FlowName." 포함, 구분자 없으면 ("", fullName)</returns>
    let parseWorkNameParts (fullName: string) : struct(string * string) =
        match fullName.IndexOf('.') with
        | dotIdx when dotIdx >= 0 -> struct(fullName.[..dotIdx], fullName.[(dotIdx + 1)..])
        | _ -> struct("", fullName)

    /// <summary>다중 선택 시 TokenRole flag의 3-state 체크박스 상태 결정</summary>
    /// <returns>Nullable: true (전부 on), false (전부 off), null (혼합)</returns>
    let resolveTokenRoleFlagState (roles: TokenRole seq) (flag: TokenRole) : Nullable<bool> =
        if Seq.isEmpty roles then Nullable<bool>(false)
        else
            let allSet = roles |> Seq.forall (fun r -> r.HasFlag(flag))
            let anySet = roles |> Seq.exists (fun r -> r.HasFlag(flag))
            if allSet = anySet then Nullable<bool>(allSet)
            else Nullable<bool>()

    /// <summary>TokenRole flag 토글: true이면 flag 추가, false이면 flag 제거</summary>
    let computeNextTokenRole (currentRole: TokenRole) (flag: TokenRole) (setFlag: bool) : TokenRole =
        if setFlag then currentRole ||| flag
        else currentRole &&& ~~~flag
