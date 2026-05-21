namespace Ds2.LightHouseService

open System

/// **Phase S7 D-S7-4 (s6-r66)** — multi-tenant 정책 SSOT.
///
/// 모든 collection-scope endpoint / session 발급이 본 module 통과 의무. visible / writable 두 술어 노출.
/// `ServiceConfig.MultiTenant.Mode` 분기:
///   - "T1" — flat (default). 모든 entry visible+writable.
///   - "T2" — per-user namespace. `entry.ImportedBy = requestingUser` 만 visible+writable.
///       legacy entry (ImportedBy null/"" — T1 시절 등록) 은 전체 공개 (사용자 결정: 마이그레이션 부담 0).
///   - "T3" — collection ACL. `entry.Acl` 검증. null acl / 빈 users = 전체 공개. acl.users 포함 시 visible.
///       acl.ReadOnly=true 면 visible 한 user 도 mutation reject (검색은 허용).
///
/// 비교 = ordinal case-insensitive (Windows username + AD identity 정합 — KWAK ≡ kwak).
[<RequireQualifiedAccess>]
module MultiTenantPolicy =

    /// 정책 판정 결과 — endpoint 가 응답 분기에 사용.
    [<NoComparison; NoEquality; RequireQualifiedAccess>]
    type AccessDecision =
        /// visible + writable (mutation 허용).
        | Allow
        /// visible 이지만 read-only (T3 acl.readOnly=true). search / list / read OK, payload swap / delete reject.
        | ReadOnly
        /// invisible. unknown 으로 응답 (정보 leak 방지 — acl reject 와 미존재 구분 안 함).
        | Hidden

    let private idEq (a: string) (b: string) : bool =
        String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

    let private aclVisible (acl: CollectionAcl) (requestingUser: string) : bool =
        if obj.ReferenceEquals(box acl, null) then true
        elif isNull acl.Users || acl.Users.Length = 0 then true
        else acl.Users |> Array.exists (fun u -> idEq u requestingUser)

    let private aclReadOnly (acl: CollectionAcl) : bool =
        if obj.ReferenceEquals(box acl, null) then false
        else acl.ReadOnly

    /// 단일 entry 의 access 판정.
    let evaluate (mode: string) (requestingUser: string) (entry: CollectionEntry) : AccessDecision =
        match mode with
        | s when s = MultiTenantMode.T1 -> AccessDecision.Allow
        | s when s = MultiTenantMode.T2 ->
            // legacy entry (ImportedBy 비어있음) 전체 공개. 일반 entry 는 owner 비교.
            if String.IsNullOrWhiteSpace entry.ImportedBy then AccessDecision.Allow
            elif idEq entry.ImportedBy requestingUser then AccessDecision.Allow
            else AccessDecision.Hidden
        | s when s = MultiTenantMode.T3 ->
            if not (aclVisible entry.Acl requestingUser) then AccessDecision.Hidden
            elif aclReadOnly entry.Acl then AccessDecision.ReadOnly
            else AccessDecision.Allow
        | _ ->
            // validateMultiTenant 가 load 시점에 fail-fast. 본 분기 도달 = 정상적으로 불가능.
            // fail-safe: 정보 leak 방지 측면에서 Hidden 박제 (open default 보다 안전).
            AccessDecision.Hidden

    /// list view 의 filter — T1=전체, T2/T3=visible 만 (ReadOnly 도 visible 에 포함).
    let filterVisible (mode: string) (requestingUser: string) (entries: CollectionEntry array) : CollectionEntry array =
        if mode = MultiTenantMode.T1 then entries
        else
            entries
            |> Array.filter (fun e ->
                match evaluate mode requestingUser e with
                | AccessDecision.Allow | AccessDecision.ReadOnly -> true
                | AccessDecision.Hidden -> false)

    /// mutation 허용 여부. visible 하나 ReadOnly 거나 Hidden 이면 false.
    let canMutate (mode: string) (requestingUser: string) (entry: CollectionEntry) : bool =
        match evaluate mode requestingUser entry with
        | AccessDecision.Allow -> true
        | AccessDecision.ReadOnly | AccessDecision.Hidden -> false
