using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.Services;

/// <summary>
/// System/디바이스 트리의 OS 클립보드 전송 — 프로메이커 인스턴스(프로세스) 간 복사/붙여넣기.
/// payload = 폐포만 담은 부분 DsStore JSON. 직렬화기는 프로젝트 저장과 동일한
/// Ds2.Serialization.JsonConverter (신규 wire format 없음 — 봉투는 얇은 라우팅 껍데기).
/// 봉투 Version 불일치는 조용한 손실 방지를 위해 거부한다.
///
/// 스레드 계약: Clipboard 접근(TryGetRawText/HasPackage와 SetText 호출부)은 STA UI 스레드,
/// 직렬화/파싱(BuildEnvelopeJson/ParseEnvelope/DeserializeStore)은 배경 스레드 안전 —
/// 호출부가 Task.Run 으로 배경화해 UI 스피너를 살린다 (옵션 B).
/// </summary>
public static class SystemPackageClipboard
{
    public const string EnvelopeType = "PromakerSystemPackage";
    public const int EnvelopeVersion = 1;

    /// <summary>봉투 식별 프리픽스 — Peek 저비용 판별용. Envelope 의 Type 이 첫 프로퍼티여야 유지된다.</summary>
    private const string EnvelopePrefix = "{\"Type\":\"" + EnvelopeType + "\"";

    /// <summary>복사 루트 1건. IsActive 는 대상 프로젝트 등록 구분(설비=Active / 디바이스=Passive).</summary>
    public sealed record RootEntry(Guid Id, bool IsActive, string Name);

    /// <summary>클립보드 봉투. Type 은 반드시 첫 프로퍼티(EnvelopePrefix 판별 계약).</summary>
    public sealed record Envelope(
        string Type, int Version, string AppVersion, List<RootEntry> Roots, string StoreJson);

    /// <summary>
    /// 선택 시스템들의 폐포를 부분 store 로 직렬화해 봉투 JSON 을 만든다.
    /// 배경 스레드 안전 (store 는 읽기만) — 단 호출부가 실행 중 store 불변을 보장할 것
    /// (BusyOverlay 입력 차단 + Revision 사전/사후 대조).
    /// </summary>
    public static string BuildEnvelopeJson(DsStore store, IReadOnlyList<RootEntry> roots)
    {
        var pruned = store.BuildSystemPackageStore(roots.Select(r => r.Id));
        var storeJson = Ds2.Serialization.JsonConverter.serialize(pruned);
        var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
        var envelope = new Envelope(EnvelopeType, EnvelopeVersion, appVersion, roots.ToList(), storeJson);
        return JsonSerializer.Serialize(envelope);
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private static uint _cachedSequence;
    private static bool _cachedHasPackage;
    private static bool _cacheInitialized;

    /// <summary>
    /// CanExecute 폴링용 저비용 체크 — 클립보드 시퀀스 번호로 캐시해서
    /// 클립보드 내용이 바뀐 경우에만 실제 텍스트를 읽는다 (수 MB 텍스트 반복 읽기 방지).
    /// UI(STA) 스레드 전용.
    /// </summary>
    public static bool HasPackage()
    {
        var sequence = GetClipboardSequenceNumber();
        if (_cacheInitialized && sequence == _cachedSequence)
            return _cachedHasPackage;
        _cachedSequence = sequence;
        _cacheInitialized = true;
        _cachedHasPackage = TryGetRawText() is not null;
        return _cachedHasPackage;
    }

    /// <summary>클립보드에서 봉투 원문을 읽는다 (프리픽스 불일치/실패 시 null). UI(STA) 스레드 전용.</summary>
    public static string? TryGetRawText()
    {
        try
        {
            if (!Clipboard.ContainsText())
                return null;
            var text = Clipboard.GetText();
            return text.StartsWith(EnvelopePrefix, StringComparison.Ordinal) ? text : null;
        }
        catch
        {
            // 클립보드 잠금 등 일시 실패 — 없음으로 간주 (다음 폴링에서 재시도)
            return null;
        }
    }

    /// <summary>봉투 원문 파싱 + Version 가드. 배경 스레드 안전. null 이면 error 에 사유(사용자 표시용).</summary>
    public static Envelope? ParseEnvelope(string rawText, out string error)
    {
        error = "";
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(rawText);
            if (envelope is null || envelope.Roots is not { Count: > 0 } || string.IsNullOrEmpty(envelope.StoreJson))
            {
                error = "클립보드의 시스템 패키지가 손상되었습니다.";
                return null;
            }
            if (envelope.Version != EnvelopeVersion)
            {
                // 버전 스큐 = 조용한 손실 위험 — 거부가 정책 (설계 §7)
                error = $"클립보드의 시스템 패키지 버전({envelope.Version})이 이 프로메이커(v{EnvelopeVersion})와 다릅니다.\n"
                      + "두 프로메이커를 같은 버전으로 맞춘 뒤 다시 복사하세요.";
                return null;
            }
            return envelope;
        }
        catch (Exception ex)
        {
            error = $"클립보드 패키지 파싱 실패: {ex.Message}";
            return null;
        }
    }

    /// <summary>봉투 payload 를 소스 store 로 역직렬화 (ImportSystemsFrom 의 source 계약). 배경 스레드 안전.</summary>
    public static DsStore DeserializeStore(Envelope envelope) =>
        Ds2.Serialization.JsonConverter.deserialize<DsStore>(envelope.StoreJson);
}
