using System;
using System.Collections.Generic;
using System.Windows.Media;
using AAStoPLC.TagWizard;
using Ds2.Editor;

namespace Promaker.Dialogs;

/// <summary>
/// IO·태그 확인용 IO 행 — 표시 + 매칭 강조용 IsUnmatched/HasError 만 변경 가능.
/// IoQueryService.Generate 이 채워서 반환한다.
/// </summary>
public sealed class IoBatchRow : BatchRowBase
{
    public IoBatchRow(Guid callId, Guid apiCallId, string flow, string work, string device, string api,
                      string inAddress, string inSymbol, string outAddress, string outSymbol,
                      string outDataType = "BOOL", string inDataType = "BOOL")
    {
        CallId = callId;
        ApiCallId = apiCallId;
        Flow = flow;
        Work = work;
        Device = device;
        Api = api;
        InAddress = inAddress;
        InSymbol = inSymbol;
        OutAddress = outAddress;
        OutSymbol = outSymbol;
        OutDataType = outDataType;
        InDataType = inDataType;
    }

    public Guid CallId { get; }
    public Guid ApiCallId { get; }
    public string Flow { get; }
    public string Work { get; }
    public string Device { get; }
    public string Api { get; }

    public string InAddress   { get; }
    public string InSymbol    { get; }
    public string OutAddress  { get; }
    public string OutSymbol   { get; }
    public string OutDataType { get; }
    public string InDataType  { get; }
}

/// <summary>
/// 진단 패널 항목 표시용 ViewModel.
/// XAML 바인딩에 필요한 표시 속성(Icon/AccentBrush/HasRawMessage 등)을 노출하고,
/// "해당 행 보기" 클릭 시 점프할 행 목록을 들고 있다.
/// </summary>
public sealed class DiagnosticItemViewModel
{
    private static readonly Brush ErrorBrush   = MakeBrush(0xE1, 0x5B, 0x5B);
    private static readonly Brush WarningBrush = MakeBrush(0xF2, 0xB1, 0x34);
    private static readonly Brush InfoBrush    = MakeBrush(0x57, 0xC0, 0x6D);

    public DiagnosticItemViewModel(
        DiagnosticItem source,
        IReadOnlyList<IoBatchRow> matchedRows,
        bool fbTagMapEditAvailable = false)
    {
        Source = source;
        MatchedRows = matchedRows;
        FBTagMapEditAvailable = fbTagMapEditAvailable;
    }

    public DiagnosticItem Source { get; }
    public IReadOnlyList<IoBatchRow> MatchedRows { get; }
    public bool FBTagMapEditAvailable { get; }
    public string? SystemType => Source.SystemType;

    public string Icon => Source.Severity switch
    {
        DiagnosticSeverity.Error   => "❌",
        DiagnosticSeverity.Warning => "⚠",
        _                          => "ℹ",
    };

    public Brush AccentBrush => Source.Severity switch
    {
        DiagnosticSeverity.Error   => ErrorBrush,
        DiagnosticSeverity.Warning => WarningBrush,
        _                          => InfoBrush,
    };

    public string Title       => Source.Title;
    public string Detail      => Source.Detail;
    public string? RawMessage => Source.RawMessage;

    public bool HasRawMessage    => !string.IsNullOrEmpty(Source.RawMessage);
    public bool HasAffectedRows  => MatchedRows.Count > 0;

    /// <summary>SystemType 이 식별되고 호출자가 편집 액션을 제공했을 때만 버튼 노출.</summary>
    public bool CanOpenFBTagMap  => FBTagMapEditAvailable && !string.IsNullOrEmpty(SystemType);

    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
