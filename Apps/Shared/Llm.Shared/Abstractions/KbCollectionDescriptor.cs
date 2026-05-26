namespace Llm.Shared.Abstractions;

/// <summary>
/// KB collection 의 lightweight read-only descriptor — <see cref="KbDigestBuilder"/> 의 input shape.
///
/// <para>App 별 KB collection wire POCO (Promaker 의 <c>CollectionInfo</c> 등) 를 직접 의존하지 않기 위한 추출.
/// caller 가 자신의 POCO → 본 record 1줄 매핑 (e.g.
/// <c>new KbCollectionDescriptor { DisplayName = c.DisplayName, Description = c.Description, Keywords = c.Keywords }</c>).</para>
///
/// <para><b>named init property style</b> — caller 의 test 박제 syntax 가 wire POCO 의 named property 와
/// 정합 (positional record 보다 readability ↑ / Id 등 wire-only 필드 자연 누락).</para>
/// </summary>
public sealed record KbCollectionDescriptor
{
    /// <summary>사용자 표시명 (digest 의 "- " 박제 키). 빈 string 인 entry 는 KbDigestBuilder 가 skip.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>collection topic 1줄. 빈 string 시 description 줄 생략.</summary>
    public string? Description { get; init; }

    /// <summary>top-N keyword (PR-A r0). null/empty 시 "keywords:" 줄 생략.</summary>
    public string[]? Keywords { get; init; }
}
