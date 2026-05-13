using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AAStoPLC.TagWizard;
using Promaker.Services;

namespace Promaker.Dialogs;

// =============================================================================
// IW/QW/MW 3섹션 처리 통합 — 같은 구조의 ObservableCollection / 패턴 / DTO 매핑을
// SignalSectionInfo 로 일반화하고 추가/제거/로드/저장을 단일 헬퍼로 처리.
// =============================================================================

public partial class TagWizardDialog
{
    /// <summary>섹션 1개의 컬렉션·기본 패턴·DTO 매핑·UI 그리드를 묶은 핸들.</summary>
    private sealed class SignalSectionInfo
    {
        public required string Name { get; init; }                                    // "IW"/"QW"/"MW"
        public required ObservableCollection<SignalPatternRow> Rows { get; init; }
        public required string DefaultPattern { get; init; }                          // 행 추가 시 기본 패턴
        public required Func<FBTagMapPresetDto, List<SignalPatternEntryDto>> SectionOf { get; init; }
        public required DataGrid Grid { get; init; }
        public required CheckBox ChunkedToggle { get; init; }
        public required FrameworkElement ChunkedView { get; init; }
        public required ObservableCollection<ObservableCollection<IndexedPatternRow>> Chunks { get; init; }
    }

    private SignalSectionInfo[] AllSections() => new[]
    {
        new SignalSectionInfo {
            Name = "IW", Rows = _iwSignalRows, DefaultPattern = "W_$(F)_WRS_$(D)_$(A)",
            SectionOf = dto => dto.IwPatterns, Grid = IwSignalGrid,
            ChunkedToggle = IwChunkedToggle, ChunkedView = IwChunkedView, Chunks = IwChunks },
        new SignalSectionInfo {
            Name = "QW", Rows = _qwSignalRows, DefaultPattern = "W_$(F)_SOL_$(D)_$(A)",
            SectionOf = dto => dto.QwPatterns, Grid = QwSignalGrid,
            ChunkedToggle = QwChunkedToggle, ChunkedView = QwChunkedView, Chunks = QwChunks },
        new SignalSectionInfo {
            Name = "MW", Rows = _mwSignalRows, DefaultPattern = "W_$(F)_M_$(D)_$(A)",
            SectionOf = dto => dto.MwPatterns, Grid = MwSignalGrid,
            ChunkedToggle = MwChunkedToggle, ChunkedView = MwChunkedView, Chunks = MwChunks },
    };

    /// <summary>DTO entry → SignalPatternRow (currentFbType 주입). 레거시 ApiName="-" 도 IsSpare 로 흡수.</summary>
    private static SignalPatternRow RowFromDto(SignalPatternEntryDto e, string currentFbType) =>
        new()
        {
            ApiName          = e.ApiName,
            Pattern          = e.Pattern,
            TargetFBType     = currentFbType,
            TargetFBPort     = e.TargetFBPort,
            SkipAddressAlloc = e.SkipAddressAlloc,
            IsSpare          = e.IsSpare || e.ApiName == "-",
            PreFbCondition   = DtoToCoreExpr(e.PreFbCondition),
            UserDataType     = e.UserDataType ?? "",
        };

    /// <summary>Row → DTO entry. 빈 ApiName 의 비-Spare 행은 호출자가 사전 필터.</summary>
    private static SignalPatternEntryDto DtoFromRow(SignalPatternRow row) =>
        new()
        {
            ApiName          = row.ApiName ?? "",
            Pattern          = row.Pattern ?? "",
            TargetFBPort     = row.TargetFBPort ?? "",
            SkipAddressAlloc = row.SkipAddressAlloc,
            IsSpare          = row.IsSpare,
            PreFbCondition   = CoreToDtoExpr(row.PreFbCondition),
            UserDataType     = row.UserDataType ?? "",
        };

    private static Ds2.Core.FbInputExpr? DtoToCoreExpr(FbInputExprDto? d)
    {
        if (d == null) return null;
        var c = new Ds2.Core.FbInputExpr
        {
            Kind   = (Ds2.Core.FbInputExprKind)(int)d.Kind,
            Symbol = d.Symbol ?? "",
        };
        if (d.Children != null)
            foreach (var cd in d.Children)
            {
                var cc = DtoToCoreExpr(cd);
                if (cc != null) c.Children.Add(cc);
            }
        return c;
    }

    private static FbInputExprDto? CoreToDtoExpr(Ds2.Core.FbInputExpr? c)
    {
        if (c == null) return null;
        var d = new FbInputExprDto
        {
            Kind   = (FbInputExprKindDto)(int)c.Kind,
            Symbol = c.Symbol ?? "",
        };
        foreach (var cc in c.Children)
            if (cc != null) d.Children.Add(CoreToDtoExpr(cc));
        return d!;
    }

    /// <summary>섹션 1개를 DTO 에서 행으로 로드 (HookAutoSave 적용).</summary>
    private void LoadSectionRows(SignalSectionInfo sec, FBTagMapPresetDto dto, string currentFbType)
    {
        sec.Rows.Clear();
        foreach (var e in sec.SectionOf(dto))
            sec.Rows.Add(HookAutoSave(RowFromDto(e, currentFbType)));
    }

    /// <summary>섹션 1개를 행에서 DTO 로 저장 (Spare 또는 ApiName 있는 행만).</summary>
    private void SaveSectionRows(SignalSectionInfo sec, FBTagMapPresetDto dto)
    {
        var target = sec.SectionOf(dto);
        target.Clear();
        foreach (var row in sec.Rows)
            if (row.IsSpare || !string.IsNullOrWhiteSpace(row.ApiName))
                target.Add(DtoFromRow(row));
    }
}
