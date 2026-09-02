namespace Ds2.Core.Store

open System

/// Device Alias / ApiName 유효성 검증
module InputValidation =

    /// DevicesAlias 검증 결과
    type AliasValidationResult =
        | Valid
        | EmptyAlias
        | AliasDotForbidden

    /// ApiName 검증 결과
    type ApiNameValidationResult =
        | Valid of string list
        | EmptyInput
        | EmptyAfterParse
        | ApiNameDotForbidden

    /// DevicesAlias 유효성 검증
    let validateDevicesAlias (alias: string) : AliasValidationResult =
        let trimmed = alias.Trim()
        if String.IsNullOrEmpty(trimmed) then EmptyAlias
        elif trimmed.Contains('.') then AliasDotForbidden
        else AliasValidationResult.Valid

    /// ApiName 텍스트 (세미콜론 구분) 파싱 + 검증
    let validateApiNames (text: string) : ApiNameValidationResult =
        let trimmed = text.Trim()
        if String.IsNullOrEmpty(trimmed) then EmptyInput
        else
            let names =
                trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun s -> s.Trim())
                |> Array.filter (fun s -> s.Length > 0)
                |> Array.toList
            if names.IsEmpty then EmptyAfterParse
            elif names |> List.exists (fun n -> n.Contains('.')) then ApiNameDotForbidden
            else ApiNameValidationResult.Valid names

/// 이름 정책(SSOT) — 이름이 어디로 흐르는지에 따라 허용 문자가 다르다.
///   · 식별자(Flow/System): URL 경로 세그먼트·저장 키·그룹핑 키로 쓰임 → '/' '\' 금지(→ '-' 변환).
///     (flow 이름의 '/' 가 DSPilot API 404 를 실증 — 2026-08 flow 경계·분기 진단)
///   · 값(Work/Call/Device): JSON 바디·쿼리·DB 파라미터로만 흐르므로 자유 — 현장 어휘("D/L FORK") 보존.
///   · 전 개체 공통: tagAddress 조인 구분자 " / "(공백-슬래시-공백) 부분열 금지, 제어문자 제거, 앞뒤 공백 제거.
/// 사용처: Promaker 입력 확정 시 미리보기/경고(대화형), SystemPackage 임포트 자동 적용(무대화형),
///         프로젝트 열기 린트. 코어가 최종 가드라는 nextUniqueName 원칙과 동일하게 여기 한 곳만 고친다.
module NamePolicy =

    /// 개체별 정책 역할. Identifier = Flow/System, Value = Work/Call/Device.
    type NameRole =
        | Identifier
        | Value

    let private stripControl (s: string) =
        if s |> Seq.exists Char.IsControl then
            String(s |> Seq.filter (fun c -> not (Char.IsControl c)) |> Seq.toArray)
        else s

    /// tagAddress 는 "WORK / DEVICE.API" 형태로 " / " 를 구분자로 조인/파싱하므로
    /// 이름 안의 동일 부분열은 역파싱을 깨뜨린다. 시각적으로 가장 가까운 " - " 로 무해화.
    let private fixJoinSeparator (s: string) = s.Replace(" / ", " - ")

    /// 정책 적용 후 이름. 변환이 없으면 입력과 동일한 문자열을 반환하므로,
    /// 호출자는 원본과 비교해 "변환 발생" 여부(경고 표시 필요)를 판단한다.
    let sanitize (role: NameRole) (name: string) : string =
        let s = (if isNull name then "" else name) |> stripControl |> fixJoinSeparator
        let s =
            match role with
            | Identifier -> s.Replace('/', '-').Replace('\\', '-')
            | Value -> s
        s.Trim()

    /// C# 편의 래퍼: Flow/System 이름.
    let SanitizeIdentifier (name: string) = sanitize Identifier name

    /// C# 편의 래퍼: Work/Call/Device 이름.
    let SanitizeValue (name: string) = sanitize Value name
