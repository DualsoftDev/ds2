module Ds2.LightHouseService.Tests.MultiTenantPolicyTests

open Xunit
open Ds2.LightHouseService

/// **s6-r66 D-S7-4** — MultiTenantPolicy SSOT 검증.
///
/// T1=Allow 전체 / T2=ImportedBy 일치만 Allow (legacy ImportedBy 빈 값은 Allow) / T3=acl 검증 (Hidden/ReadOnly/Allow).

let private mkEntry (id: string) (importedBy: string) (acl: CollectionAcl) : CollectionEntry = {
    Id = id
    DisplayName = "test-" + id
    IndexerVersion = "1.0.0"
    FileCount = 0
    TotalSourceBytes = 0L
    CreatedAt = "2026-05-20T00:00:00Z"
    ImportedAt = "2026-05-20T00:00:00Z"
    ImportedBy = importedBy
    StorageRelPath = "Collections\\" + id
    Status = "idle"
    ErrorReason = null
    LastImportedAt = "2026-05-20T00:00:00Z"
    // **PR-A (r0)** — 두 신 필드 default (MultiTenant 검증과 무관)
    Description = ""
    Keywords = [||]
    Acl = acl
}

let private nullAcl : CollectionAcl = Unchecked.defaultof<CollectionAcl>

[<Fact>]
let ``T1 mode — 모든 entry Allow (회귀 0)`` () =
    let entry = mkEntry "c1" "alice" nullAcl
    Assert.Equal(MultiTenantPolicy.AccessDecision.Allow,
                 MultiTenantPolicy.evaluate MultiTenantMode.T1 "bob" entry)

[<Fact>]
let ``T2 mode — ImportedBy 일치 시 Allow`` () =
    let entry = mkEntry "c1" "alice" nullAcl
    Assert.Equal(MultiTenantPolicy.AccessDecision.Allow,
                 MultiTenantPolicy.evaluate MultiTenantMode.T2 "alice" entry)

[<Fact>]
let ``T2 mode — ImportedBy 불일치 시 Hidden`` () =
    let entry = mkEntry "c1" "alice" nullAcl
    Assert.Equal(MultiTenantPolicy.AccessDecision.Hidden,
                 MultiTenantPolicy.evaluate MultiTenantMode.T2 "bob" entry)

[<Fact>]
let ``T2 mode — 대소문자 무시 (Windows username 정합)`` () =
    let entry = mkEntry "c1" "Alice" nullAcl
    Assert.Equal(MultiTenantPolicy.AccessDecision.Allow,
                 MultiTenantPolicy.evaluate MultiTenantMode.T2 "ALICE" entry)

[<Fact>]
let ``T2 mode — legacy entry (ImportedBy 빈 값) 전체 공개`` () =
    let entry = mkEntry "c1" "" nullAcl
    Assert.Equal(MultiTenantPolicy.AccessDecision.Allow,
                 MultiTenantPolicy.evaluate MultiTenantMode.T2 "anyone" entry)
    let entry2 = mkEntry "c2" null nullAcl
    Assert.Equal(MultiTenantPolicy.AccessDecision.Allow,
                 MultiTenantPolicy.evaluate MultiTenantMode.T2 "anyone" entry2)

[<Fact>]
let ``T3 mode — acl null = 전체 공개 (legacy fallback)`` () =
    let entry = mkEntry "c1" "alice" nullAcl
    Assert.Equal(MultiTenantPolicy.AccessDecision.Allow,
                 MultiTenantPolicy.evaluate MultiTenantMode.T3 "bob" entry)

[<Fact>]
let ``T3 mode — acl.users 빈 배열 = 전체 공개`` () =
    let acl = { Users = Array.empty; ReadOnly = false }
    let entry = mkEntry "c1" "alice" acl
    Assert.Equal(MultiTenantPolicy.AccessDecision.Allow,
                 MultiTenantPolicy.evaluate MultiTenantMode.T3 "bob" entry)

[<Fact>]
let ``T3 mode — acl.users 포함 user = Allow`` () =
    let acl = { Users = [| "alice"; "bob" |]; ReadOnly = false }
    let entry = mkEntry "c1" "owner" acl
    Assert.Equal(MultiTenantPolicy.AccessDecision.Allow,
                 MultiTenantPolicy.evaluate MultiTenantMode.T3 "bob" entry)

[<Fact>]
let ``T3 mode — acl.users 미포함 user = Hidden`` () =
    let acl = { Users = [| "alice" |]; ReadOnly = false }
    let entry = mkEntry "c1" "owner" acl
    Assert.Equal(MultiTenantPolicy.AccessDecision.Hidden,
                 MultiTenantPolicy.evaluate MultiTenantMode.T3 "bob" entry)

[<Fact>]
let ``T3 mode — acl.readOnly=true + user 포함 = ReadOnly`` () =
    let acl = { Users = [| "alice" |]; ReadOnly = true }
    let entry = mkEntry "c1" "owner" acl
    Assert.Equal(MultiTenantPolicy.AccessDecision.ReadOnly,
                 MultiTenantPolicy.evaluate MultiTenantMode.T3 "alice" entry)

[<Fact>]
let ``T3 mode — acl.readOnly=true + user 미포함은 Hidden 우선`` () =
    let acl = { Users = [| "alice" |]; ReadOnly = true }
    let entry = mkEntry "c1" "owner" acl
    // Hidden 이 ReadOnly 보다 우선 (acl reject 가 mutation reject 보다 강함).
    Assert.Equal(MultiTenantPolicy.AccessDecision.Hidden,
                 MultiTenantPolicy.evaluate MultiTenantMode.T3 "bob" entry)

[<Fact>]
let ``filterVisible — T1 = 전체 통과 (회귀 0)`` () =
    let entries = [|
        mkEntry "c1" "alice" nullAcl
        mkEntry "c2" "bob" nullAcl
    |]
    let result = MultiTenantPolicy.filterVisible MultiTenantMode.T1 "ignored" entries
    Assert.Equal(2, result.Length)

[<Fact>]
let ``filterVisible — T2 = ImportedBy 일치만`` () =
    let entries = [|
        mkEntry "c1" "alice" nullAcl
        mkEntry "c2" "bob" nullAcl
        mkEntry "c3" "" nullAcl  // legacy = 전체 공개
    |]
    let result = MultiTenantPolicy.filterVisible MultiTenantMode.T2 "alice" entries
    Assert.Equal(2, result.Length)  // c1 + c3
    Assert.Contains(result, fun e -> e.Id = "c1")
    Assert.Contains(result, fun e -> e.Id = "c3")

[<Fact>]
let ``filterVisible — T3 + ReadOnly 도 visible 셋 포함`` () =
    let aclAllow = { Users = [| "alice" |]; ReadOnly = false }
    let aclReadOnly = { Users = [| "alice" |]; ReadOnly = true }
    let entries = [|
        mkEntry "c1" "owner" aclAllow
        mkEntry "c2" "owner" aclReadOnly
        mkEntry "c3" "owner" { Users = [| "bob" |]; ReadOnly = false }  // Hidden
    |]
    let result = MultiTenantPolicy.filterVisible MultiTenantMode.T3 "alice" entries
    Assert.Equal(2, result.Length)  // c1 + c2

[<Fact>]
let ``canMutate — T1 모두 true / T2 ImportedBy 일치만 / T3 Allow 만`` () =
    let entry1 = mkEntry "c1" "alice" nullAcl
    Assert.True(MultiTenantPolicy.canMutate MultiTenantMode.T1 "bob" entry1)
    Assert.True(MultiTenantPolicy.canMutate MultiTenantMode.T2 "alice" entry1)
    Assert.False(MultiTenantPolicy.canMutate MultiTenantMode.T2 "bob" entry1)
    let aclReadOnly = { Users = [| "alice" |]; ReadOnly = true }
    let entry2 = mkEntry "c2" "owner" aclReadOnly
    Assert.False(MultiTenantPolicy.canMutate MultiTenantMode.T3 "alice" entry2)  // ReadOnly
    Assert.False(MultiTenantPolicy.canMutate MultiTenantMode.T3 "bob" entry2)    // Hidden
