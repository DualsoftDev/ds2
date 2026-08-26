using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    private static readonly log4net.ILog Log = log4net.LogManager.GetLogger(typeof(SystemPackageClipboard));

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
        var envelope = new Envelope(EnvelopeType, EnvelopeVersion, AppVersion, roots.ToList(), storeJson);
        return JsonSerializer.Serialize(envelope);
    }

    /// <summary>실행 중인 프로메이커 버전 — 봉투/스풀 메타 공통.</summary>
    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";

    /// <summary>
    /// 클립보드 게시 결과. Flushed = OleFlushClipboard 성공(이 창을 닫아도 내용 유지),
    /// Volatile = 플러시 실패로 이 프로세스 소유 상태로만 게시(창을 닫으면 내용 소멸),
    /// Failed = 클립보드 게시 자체 불가(점유/차단) — 파일 채널(SystemPackageSpool)이 대신 성립시킨다.
    /// </summary>
    public enum WriteMode { Flushed, Volatile, Failed }

    private const int SetRetryCount = 5;
    private const int SetRetryDelayMs = 250;

    /// <summary>
    /// 봉투 JSON 을 클립보드에 게시. Windows 클립보드는 전역 단일 배타 자원이라
    /// 다른 프로세스(클립보드 관리자·원격 데스크톱·Office 등)가 점유 중이면
    /// OleSetClipboard 가 CLIPBRD_E_CANT_OPEN(0x800401D0) 으로 실패한다. WPF 자체 재시도
    /// (10×100ms)만으로는 부족해 ①간격을 넓힌 재시도, ②플러시 없는 게시(copy:false)로
    /// 2단 폴백한다. 둘 다 실패해도 <b>예외를 던지지 않고</b> Failed 를 돌려준다 — 파일 채널이
    /// 이미 복사를 성립시켰으므로 클립보드 실패는 기능 실패가 아니다(호출부가 문구로만 안내).
    ///
    /// 스레드 계약: UI(STA) 스레드에서 호출할 것 — await 복귀 지점도 UI 스레드여야 하므로
    /// SynchronizationContext 가 있는 곳에서만 호출한다.
    /// </summary>
    public static async Task<WriteMode> SetPackageTextAsync(string envelopeJson)
    {
        for (var attempt = 0; attempt < SetRetryCount; attempt++)
        {
            try
            {
                // copy:true = OleSetClipboard + OleFlushClipboard (프로세스 종료 후에도 유지)
                Clipboard.SetDataObject(envelopeJson, copy: true);
                return WriteMode.Flushed;
            }
            catch (Exception ex) when (IsClipboardBusy(ex))
            {
                // 결정적 실패(매 시도 동일 HRESULT)와 순간 경합을 사후 구분하기 위한 흔적.
                Log.Warn($"Clipboard SetDataObject(copy:true) attempt {attempt + 1}/{SetRetryCount} failed — {Describe(ex, envelopeJson)}");
                if (attempt < SetRetryCount - 1)
                    await Task.Delay(SetRetryDelayMs);
            }
        }

        // 폴백: 플러시 생략 — 클립보드 소유 시간이 짧아 성공률이 높다. 인스턴스 간 복사는
        // 원본 창이 살아 있는 게 정상 시나리오라 실사용에 충분(창 닫으면 소멸은 호출부가 안내).
        try
        {
            Clipboard.SetDataObject(envelopeJson, copy: false);
            return WriteMode.Volatile;
        }
        catch (Exception ex) when (IsClipboardBusy(ex))
        {
            Log.Error($"Clipboard SetDataObject(copy:false) fallback failed — {Describe(ex, envelopeJson)}");
        }

        return WriteMode.Failed;
    }

    /// <summary>
    /// 실패 원인 분류용 진단 문구. HRESULT 는 원인별로 다르고(0x800401D0=점유/OLE 미초기화),
    /// 아파트먼트 상태·Dispatcher 접근은 스레드 계약 위반을 즉시 가려낸다.
    /// </summary>
    private static string Describe(Exception? ex, string envelopeJson)
    {
        var hr = ex is ExternalException ee ? $"0x{ee.ErrorCode:X8}" : "n/a";
        var sta = Thread.CurrentThread.GetApartmentState();
        var onUi = Application.Current?.Dispatcher.CheckAccess() ?? false;
        var kb = envelopeJson.Length * 2 / 1024.0;
        return $"hr={hr} apartment={sta} onDispatcher={onUi} payload={kb:F0}KB seq={GetClipboardSequenceNumber()} msg={ex?.Message}";
    }

    /// <summary>클립보드 점유/일시 실패 판별 — 그 외 예외(프로그래밍 오류)는 그대로 전파.</summary>
    private static bool IsClipboardBusy(Exception ex) =>
        ex is ExternalException or InvalidOperationException;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    /// <summary>현재 클립보드 시퀀스 번호 — 스풀이 "클립보드가 그 뒤로 안 바뀌었다"를 판정하는 근거.</summary>
    public static uint CurrentSequence => GetClipboardSequenceNumber();

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
