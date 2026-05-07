module SanitizeNameTests

open Xunit
open Ds2.LlmAgent

/// 1d-4 C / Pass E - ToolOperations.sanitizeName negative test.
/// RLO override / null byte / ZWJ / control / length / whitespace - prompt injection 1차 방어 회귀.
/// 후속 수정에서 trim 만 하도록 단순화하면 본 묶음이 즉시 실패.
///
/// **인코딩 노트** (Phase 2 후속 정리): RLO / NUL / ZWJ / control 문자는 escape sequence (\uXXXX) 로 작성.
/// literal 로 박으면 git 이 binary 로 분류되어 diff/blame/merge 가시성을 잃음.
/// 의미는 동일 - F# 컴파일러가 escape 를 동일 codepoint sequence 로 변환.

let private cap = ToolOperations.NameMaxLength  // 128

let private isValid (result: string) = result = ""

[<Fact>]
let ``ASCII 영문 이름은 valid`` () =
    Assert.True(isValid (ToolOperations.sanitizeName "Sys1" "name" cap))

[<Fact>]
let ``한글 이름은 valid`` () =
    Assert.True(isValid (ToolOperations.sanitizeName "시스템1" "name" cap))

[<Fact>]
let ``공백 trim 후 valid`` () =
    Assert.True(isValid (ToolOperations.sanitizeName "  Sys  " "name" cap))

[<Fact>]
let ``빈 문자열은 invalid (비어있습니다 메시지)`` () =
    let result = ToolOperations.sanitizeName "" "name" cap
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains("비어있습니다", result)

[<Fact>]
let ``공백만은 invalid (IsNullOrWhiteSpace 잡음)`` () =
    let result = ToolOperations.sanitizeName "   " "name" cap
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains("비어있습니다", result)

[<Fact>]
let ``null 도 invalid 처리`` () =
    let result = ToolOperations.sanitizeName null "name" cap
    Assert.StartsWith("VALIDATION_ERROR:", result)

[<Fact>]
let ``maxLength 초과는 invalid (길이 메시지)`` () =
    let longStr = System.String('A', cap + 1)
    let result = ToolOperations.sanitizeName longStr "name" cap
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains("길이", result)
    Assert.Contains(string (cap + 1), result)

[<Fact>]
let ``RLO override (U+202E) 는 invalid + codepoint 명시`` () =
    let result = ToolOperations.sanitizeName "Sys\u202ENam" "name" cap
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains("U+202E", result)

[<Fact>]
let ``null byte (U+0000) 는 invalid + codepoint 명시`` () =
    let result = ToolOperations.sanitizeName "Sys\u0000Name" "name" cap
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains("U+0000", result)

[<Fact>]
let ``ZWJ (U+200D) 는 invalid + codepoint 명시`` () =
    let result = ToolOperations.sanitizeName "Sys\u200DName" "name" cap
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains("U+200D", result)

[<Fact>]
let ``일반 제어문자 (U+0001) 는 invalid + codepoint 명시`` () =
    let result = ToolOperations.sanitizeName "Sys\u0001Name" "name" cap
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains("U+0001", result)

[<Fact>]
let ``줄바꿈 (LF, U+000A, Cc 카테고리) 는 invalid`` () =
    let result = ToolOperations.sanitizeName "Sys\nName" "name" cap
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains("U+000A", result)

[<Fact>]
let ``field 이름이 메시지에 포함됨 (LLM 회복 단서)`` () =
    let result = ToolOperations.sanitizeName "" "devicesAlias" cap
    Assert.Contains("devicesAlias", result)

// --- Pass 3 (c) - @/$ prefix reject (C5 / M10 self-loop 방지) ---

[<Fact>]
let ``at-prefix 이름은 invalid (예약 prefix)`` () =
    let result = ToolOperations.sanitizeName "@malicious" "name" cap
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains("예약 prefix", result)

[<Fact>]
let ``dollar-prefix 이름은 invalid`` () =
    let result = ToolOperations.sanitizeName "$varlike" "name" cap
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains("예약 prefix", result)

[<Fact>]
let ``at 단독도 invalid`` () =
    let result = ToolOperations.sanitizeName "@" "name" cap
    Assert.StartsWith("VALIDATION_ERROR:", result)

[<Fact>]
let ``dollar 단독도 invalid`` () =
    let result = ToolOperations.sanitizeName "$" "name" cap
    Assert.StartsWith("VALIDATION_ERROR:", result)

[<Fact>]
let ``trim 후 at-prefix 시작도 invalid (선행 공백 무관)`` () =
    let result = ToolOperations.sanitizeName "   @x" "name" cap
    Assert.StartsWith("VALIDATION_ERROR:", result)
    Assert.Contains("예약 prefix", result)

[<Fact>]
let ``이름 중간의 at 글자는 valid (entity name 안 at 자체는 허용)`` () =
    Assert.True(isValid (ToolOperations.sanitizeName "Sys@1" "name" cap))
