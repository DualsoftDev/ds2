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

    /// <summary>선택 시스템들의 폐포를 부분 store 로 직렬화해 클립보드에 싣는다.</summary>
    public static bool TryCopy(DsStore store, IReadOnlyList<RootEntry> roots, out string error)
    {
        error = "";
        try
        {
            var pruned = store.BuildSystemPackageStore(roots.Select(r => r.Id));
            var storeJson = Ds2.Serialization.JsonConverter.serialize(pruned);
            var appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
            var envelope = new Envelope(EnvelopeType, EnvelopeVersion, appVersion, roots.ToList(), storeJson);
            Clipboard.SetText(JsonSerializer.Serialize(envelope));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private static uint _cachedSequence;
    private static bool _cachedHasPackage;
    private static bool _cacheInitialized;

    /// <summary>
    /// CanExecute 폴링용 저비용 체크 — 클립보드 시퀀스 번호로 캐시해서
    /// 클립보드 내용이 바뀐 경우에만 실제 텍스트를 읽는다 (수 MB 텍스트 반복 읽기 방지).
    /// </summary>
    public static bool HasPackage()
    {
        var sequence = GetClipboardSequenceNumber();
        if (_cacheInitialized && sequence == _cachedSequence)
            return _cachedHasPackage;
        _cachedSequence = sequence;
        _cacheInitialized = true;
        _cachedHasPackage = PeekEnvelope();
        return _cachedHasPackage;
    }

    private static bool PeekEnvelope()
    {
        try
        {
            return Clipboard.ContainsText()
                && Clipboard.GetText() is { } text
                && text.StartsWith(EnvelopePrefix, StringComparison.Ordinal);
        }
        catch
        {
            // 클립보드 잠금 등 일시 실패 — 없음으로 간주 (다음 폴링에서 재시도)
            return false;
        }
    }

    /// <summary>봉투 파싱 + Version 가드. 반환 null 이면 error 에 사유(사용자 표시용).</summary>
    public static Envelope? TryRead(out string error)
    {
        error = "";
        try
        {
            if (!Clipboard.ContainsText())
                return null;
            var text = Clipboard.GetText();
            if (!text.StartsWith(EnvelopePrefix, StringComparison.Ordinal))
                return null;

            var envelope = JsonSerializer.Deserialize<Envelope>(text);
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
            error = $"클립보드 읽기 실패: {ex.Message}";
            return null;
        }
    }

    /// <summary>봉투 payload 를 소스 store 로 역직렬화 (ImportSystemsFrom 의 source 계약).</summary>
    public static DsStore DeserializeStore(Envelope envelope) =>
        Ds2.Serialization.JsonConverter.deserialize<DsStore>(envelope.StoreJson);
}
