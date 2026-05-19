using Microsoft.FSharp.Core;

namespace Promaker.Presentation;

/// <summary>
/// C# 호출자가 <see cref="FSharpOption{T}"/> 를 다루기 쉽게 하는 확장.
/// <c>FSharpOption&lt;T&gt;.get_IsSome(opt) ? opt.Value : ...</c> 보일러플레이트를 줄인다.
///
/// 주의: FSharpOption 의 None 은 null 참조다.
/// reference type 의 경우 단순 null 비교로 처리 가능 — 그 패턴에는 본 확장이 필요 없음.
/// </summary>
public static class FSharpOptionExtensions
{
    /// <summary>Some 이면 value 를 채우고 true, None 이면 default + false.</summary>
    public static bool TryGet<T>(this FSharpOption<T> opt, out T value)
    {
        if (FSharpOption<T>.get_IsSome(opt))
        {
            value = opt.Value;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>Some 이면 value, None 이면 fallback.</summary>
    public static T GetOrDefault<T>(this FSharpOption<T> opt, T fallback) =>
        FSharpOption<T>.get_IsSome(opt) ? opt.Value : fallback;

    /// <summary>Some 이면 value, None 이면 default(T)/null. value type 에는 사용 자제.</summary>
    public static T? GetOrNull<T>(this FSharpOption<T> opt) where T : class =>
        FSharpOption<T>.get_IsSome(opt) ? opt.Value : null;

    /// <summary>Some 이면 value, None 이면 default(T)/null (struct).</summary>
    public static T? GetOrNullStruct<T>(this FSharpOption<T> opt) where T : struct =>
        FSharpOption<T>.get_IsSome(opt) ? opt.Value : null;

    /// <summary>Some/None 분기 — match 처럼.</summary>
    public static TResult Match<T, TResult>(
        this FSharpOption<T> opt,
        System.Func<T, TResult> onSome,
        System.Func<TResult> onNone) =>
        FSharpOption<T>.get_IsSome(opt) ? onSome(opt.Value) : onNone();
}
