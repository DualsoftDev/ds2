using System;
using System.Collections.Generic;
using Ds2.Core;

namespace Promaker.Services;

/// <summary>
/// 입력 검증을 담당하는 서비스 인터페이스
/// </summary>
public interface IValidationService
{
    /// <summary>
    /// 엔티티 이름 유효성 검증
    /// </summary>
    /// <param name="name">검증할 이름</param>
    /// <returns>유효성 검증 결과</returns>
    ValidationResult ValidateName(string? name);

    /// <summary>
    /// 파일 경로 유효성 검증
    /// </summary>
    /// <param name="path">검증할 경로</param>
    /// <returns>유효성 검증 결과</returns>
    ValidationResult ValidateFilePath(string? path);

    /// <summary>
    /// ValueSpec 유효성 검증
    /// </summary>
    /// <param name="valueSpec">검증할 ValueSpec</param>
    /// <returns>유효성 검증 결과</returns>
    ValidationResult ValidateValueSpec(ValueSpec? valueSpec);

    /// <summary>
    /// 엔티티가 삭제 가능한지 검증
    /// </summary>
    /// <param name="entityIds">삭제할 엔티티 ID 목록</param>
    /// <returns>유효성 검증 결과</returns>
    ValidationResult ValidateCanDelete(IEnumerable<Guid> entityIds);
}

/// <summary>
/// 유효성 검증 결과
/// </summary>
public record ValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }

    public static ValidationResult Success() => new() { IsValid = true };
    public static ValidationResult Fail(string errorMessage) => new() { IsValid = false, ErrorMessage = errorMessage };
}
