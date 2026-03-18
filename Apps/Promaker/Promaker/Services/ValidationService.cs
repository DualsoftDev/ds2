using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ds2.Core;

namespace Promaker.Services;

/// <summary>
/// 입력 검증을 담당하는 서비스 구현
/// </summary>
public class ValidationService : IValidationService
{
    public ValidationResult ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ValidationResult.Fail("이름은 비어 있을 수 없습니다.");

        if (name.Length > 100)
            return ValidationResult.Fail("이름은 100자를 초과할 수 없습니다.");

        var invalidChars = Path.GetInvalidFileNameChars();
        if (name.Any(c => invalidChars.Contains(c)))
            return ValidationResult.Fail($"이름에 잘못된 문자가 포함되어 있습니다: {string.Join(", ", invalidChars.Where(name.Contains))}");

        return ValidationResult.Success();
    }

    public ValidationResult ValidateFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ValidationResult.Fail("파일 경로는 비어 있을 수 없습니다.");

        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                return ValidationResult.Fail($"디렉터리가 존재하지 않습니다: {directory}");

            return ValidationResult.Success();
        }
        catch (Exception ex)
        {
            return ValidationResult.Fail($"잘못된 파일 경로입니다: {ex.Message}");
        }
    }

    public ValidationResult ValidateValueSpec(ValueSpec? valueSpec)
    {
        if (valueSpec is null)
            return ValidationResult.Fail("ValueSpec은 null일 수 없습니다.");

        // 추가 검증 로직을 여기에 구현
        // 예: 데이터 타입 검증, 범위 검증 등

        return ValidationResult.Success();
    }

    public ValidationResult ValidateCanDelete(IEnumerable<Guid> entityIds)
    {
        var idList = entityIds.ToList();

        if (idList.Count == 0)
            return ValidationResult.Fail("삭제할 엔티티가 없습니다.");

        // 추가 검증 로직을 여기에 구현
        // 예: 참조 무결성 검증, 시스템 엔티티 삭제 방지 등

        return ValidationResult.Success();
    }
}
