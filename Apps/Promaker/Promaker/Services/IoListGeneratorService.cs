using System;
using System.Linq;
using Ds2.IOList;
using Ds2.Store;

namespace Promaker.Services;

/// <summary>
/// Ds2.IOList F# API를 C#에서 사용하기 쉽게 래핑한 서비스
/// </summary>
public class IoListGeneratorService
{
    /// <summary>
    /// DS2 모델에서 IO/Dummy 신호 생성
    /// </summary>
    /// <param name="store">DS2 Store 인스턴스</param>
    /// <param name="templateDir">템플릿 디렉토리 경로</param>
    /// <returns>생성 결과 (IO 신호, Dummy 신호, 에러, 경고)</returns>
    public GenerationResult Generate(DsStore store, string templateDir)
    {
        return Pipeline.generate(store, templateDir);
    }

    /// <summary>
    /// 생성 결과가 성공인지 확인 (에러가 없는지)
    /// </summary>
    public bool IsSuccess(GenerationResult result)
    {
        return result.Errors.Length == 0;
    }

    /// <summary>
    /// 에러 메시지 요약 생성
    /// </summary>
    public string GetErrorSummary(GenerationResult result)
    {
        if (result.Errors.Length == 0)
            return "에러 없음";

        return string.Join("\n", result.Errors.Select(e => $"- {e.Message}"));
    }

    /// <summary>
    /// 경고 메시지 요약 생성
    /// </summary>
    public string GetWarningSummary(GenerationResult result)
    {
        if (result.Warnings.Length == 0)
            return "경고 없음";

        return string.Join("\n", result.Warnings.Select(w => $"- {w}"));
    }

    /// <summary>
    /// 생성 결과 통계 문자열 생성
    /// </summary>
    public string GetSummary(GenerationResult result)
    {
        return $"IO 신호: {result.IoSignals.Length}개\n" +
               $"Dummy 신호: {result.DummySignals.Length}개\n" +
               $"에러: {result.Errors.Length}개\n" +
               $"경고: {result.Warnings.Length}개";
    }
}
