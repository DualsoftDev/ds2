using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AAStoPLC.TagWizard;
using Promaker.Services;

namespace Promaker.Dialogs;

// =============================================================================
// 신호 (Signal) 영역 — TagWizard 의 IW/QW/MW 섹션 처리, 생성 파이프라인,
// 적용 + 통합 리포트를 하나로 묶음. 세 책임 모두 "TagWizard 의 signal"
// 도메인이라 합침 (이전 SignalSections.cs / SignalGeneration.cs / SignalApplication.cs 통합).
// =============================================================================

public partial class TagWizardDialog
{
    // ── Sections: IW/QW/MW 3섹션 핸들 + DTO ↔ Row 매핑 ──────────────────────

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
            SectionOf = dto => dto.IwPatterns, Grid = Step2Section.IwSignalGrid,
            ChunkedToggle = Step2Section.IwChunkedToggle, ChunkedView = Step2Section.IwChunkedView, Chunks = IwChunks },
        new SignalSectionInfo {
            Name = "QW", Rows = _qwSignalRows, DefaultPattern = "W_$(F)_SOL_$(D)_$(A)",
            SectionOf = dto => dto.QwPatterns, Grid = Step2Section.QwSignalGrid,
            ChunkedToggle = Step2Section.QwChunkedToggle, ChunkedView = Step2Section.QwChunkedView, Chunks = QwChunks },
        new SignalSectionInfo {
            Name = "MW", Rows = _mwSignalRows, DefaultPattern = "W_$(F)_M_$(D)_$(A)",
            SectionOf = dto => dto.MwPatterns, Grid = Step2Section.MwSignalGrid,
            ChunkedToggle = Step2Section.MwChunkedToggle, ChunkedView = Step2Section.MwChunkedView, Chunks = MwChunks },
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

    // ── Generation: IoSignalPipeline facade 호출 + 결과 분류 ───────────────

    /// <summary>
    /// 신호 생성 — IoSignalPipeline facade 만 호출. F# IoListPipeline 직접 의존 없음.
    /// 행 변환/매칭/Dummy 변환 로직은 모두 Services/IoSignalPipeline.
    /// </summary>
    private bool GenerateSignals()
    {
        try
        {
            NextButton.IsEnabled = false;
            NextButton.Content = "생성 중...";

            var bundle = IoSignalPipeline.GenerateAll(_store);

            // 파이프라인 자체 오류는 오류 탭으로 노출.
            if (bundle.ErrorMessages.Count > 0)
            {
                DisplayErrorsFromMessages(bundle.ErrorMessages);
                return true; // Step 3 로 이동하여 오류 탭 표시
            }

            _ioRows.Clear();
            _dummyRows.Clear();
            _unmatchedRows.Clear();

            foreach (var row in bundle.IoRows)
                _ioRows.Add(row);
            foreach (var row in bundle.DummyRows)
                _dummyRows.Add(row);
            foreach (var u in IoSignalPipeline.ClassifyUnmatched(_ioRows))
                _unmatchedRows.Add(u);

            UpdateUnmatchedTab();

            var unmatched = _unmatchedRows.Count;
            Step3Section.GenerationStatusText.Text = unmatched > 0
                ? $"✅ IO 신호 {_ioRows.Count}개, Dummy 신호 {_dummyRows.Count}개 생성 | ⚠ 매칭 실패 {unmatched}개"
                : $"✅ IO 신호 {_ioRows.Count}개, Dummy 신호 {_dummyRows.Count}개가 생성되었습니다. 모든 신호가 매칭되었습니다.";

            return true;
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"신호 생성 중 예외가 발생했습니다:\n\n{ex.Message}",
                "TAG Wizard - 오류",
                MessageBoxButton.OK,
                "✖");
            return false;
        }
        finally
        {
            NextButton.IsEnabled = true;
            NextButton.Content = "적용 ▶";
        }
    }

    private void UpdateUnmatchedTab()
    {
        if (_unmatchedRows.Count > 0)
        {
            Step3Section.UnmatchedTabItem.Visibility = Visibility.Visible;
            Step3Section.UnmatchedCountText.Text = _unmatchedRows.Count.ToString();
        }
        else
        {
            Step3Section.UnmatchedTabItem.Visibility = Visibility.Collapsed;
        }
    }

    // ── Application: 패턴 적용 + 통합 리포트 ───────────────────────────────

    /// <summary>
    /// "패턴 적용" 버튼 — 명시적 동의 후 ApiCall 에 일괄 덮어쓰기.
    /// </summary>
    internal void ApplyPatterns_Click(object sender, RoutedEventArgs e) => ConfirmAndApplyPatterns();

    /// <summary>
    /// 단일 확인 → 적용 → 단일 통합 리포트.
    /// 다이얼로그는 사전 확인 1번 + 사후 통합 리포트 1번 (총 최대 2회).
    /// 통합 리포트 안에서 Call 구조 마이그레이션도 같이 분기 처리.
    /// </summary>
    private bool ConfirmAndApplyPatterns()
    {
        if (_ioRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox("적용할 IO 신호가 없습니다.",
                "TAG Wizard", MessageBoxButton.OK, "⚠");
            return false;
        }

        var validRows = _ioRows
            .Where(r => r.CallId != System.Guid.Empty && r.ApiCallId != System.Guid.Empty)
            .ToList();
        if (validRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                "DS2 모델과 매칭되는 항목이 없습니다.\n\n" +
                "Flow, Device, Api 이름이 정확히 일치하는지 확인하세요.",
                "TAG Wizard", MessageBoxButton.OK, "⚠");
            return false;
        }

        // 사전 확인 — unmatched/덮어쓰기 경고를 한 화면에 통합.
        var unmatchedCount = _unmatchedRows.Count;
        var preMsg = new StringBuilder();
        preMsg.AppendLine($"매칭된 {validRows.Count}개 ApiCall 의 InTag/OutTag 에 패턴을 적용합니다.");
        if (unmatchedCount > 0)
        {
            preMsg.AppendLine();
            preMsg.AppendLine($"⚠ {unmatchedCount}개 항목이 DS2 모델과 매칭되지 않아 제외됩니다 ('매칭 실패' 탭 참조).");
        }
        preMsg.AppendLine();
        preMsg.AppendLine("⚠ 현재 ApiCall 에 수동으로 설정된 이름과 주소가 모두 덮어써집니다.");
        preMsg.AppendLine("   (I/O 일괄 편집에서 개별 수정한 항목 포함)");
        preMsg.AppendLine();
        preMsg.Append("계속하시겠습니까?");

        var confirm = DialogHelpers.ShowThemedMessageBox(
            preMsg.ToString(), "패턴 적용 확인", MessageBoxButton.YesNo, "⚠");
        if (confirm != MessageBoxResult.Yes) return false;

        // 적용 실행.
        IoTagApplier.ApplyResult applyResult;
        try
        {
            NextButton.IsEnabled = false;
            NextButton.Content = "적용 중...";
            applyResult = IoTagApplier.Apply(_store, validRows);
            _successCount = applyResult.SuccessCount;
        }
        catch (System.Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"IO 태그 적용 중 오류가 발생했습니다:\n\n{ex.Message}",
                "TAG Wizard - 오류", MessageBoxButton.OK, "✖");
            return false;
        }
        finally
        {
            NextButton.IsEnabled = true;
        }

        if (_successCount <= 0) return false;

        // 사후 통합 리포트 — 신호 통계 + 적용 결과 + 검증 + Call 구조 위반 한꺼번에.
        ShowUnifiedApplyReport(applyResult, unmatchedCount);
        return true;
    }

    /// <summary>적용 후 통합 리포트 — 단일 다이얼로그. Call 구조 위반 시 마이그레이션 yes/no 분기 포함.</summary>
    private void ShowUnifiedApplyReport(IoTagApplier.ApplyResult applyResult, int unmatchedCount)
    {
        var summary = WizardSummaryBuilder.Build(
            _store, _successCount, _ioRows.Count, _dummyRows.Count, SignalTemplateWarnings);

        var msg = new StringBuilder();
        msg.AppendLine($"✓ {_successCount}개 ApiCall 에 패턴 적용 완료.");
        msg.AppendLine();
        msg.AppendLine("📊 신호 통계");
        msg.AppendLine(summary.SignalStats);

        if (applyResult.AnyFailed)
        {
            msg.AppendLine();
            msg.AppendLine($"⚠ 적용 실패 {applyResult.FailedCount}건:");
            foreach (var item in applyResult.FailedItems.Take(3))
                msg.AppendLine($"  • {item}");
            if (applyResult.FailedCount > 3)
                msg.AppendLine($"  … 외 {applyResult.FailedCount - 3}건");
        }
        if (unmatchedCount > 0)
        {
            msg.AppendLine();
            msg.AppendLine($"⚠ 매칭 실패 {unmatchedCount}건 — 적용 제외 ('매칭 실패' 탭 참조).");
        }

        msg.AppendLine();
        msg.AppendLine(summary.CompletionStatus);

        var hasViolations = summary.HasCallStructureViolations;
        if (hasViolations)
        {
            msg.AppendLine();
            msg.Append("Call 구조 마이그레이션을 지금 실행하시겠습니까?");
        }
        else
        {
            msg.AppendLine();
            msg.Append("필요 시 '패턴 적용' 을 다시 눌러 재적용할 수 있습니다.");
        }

        var btn = hasViolations ? MessageBoxButton.YesNo : MessageBoxButton.OK;
        var result = DialogHelpers.ShowThemedMessageBox(
            msg.ToString(), "TAG Wizard - 적용 결과", btn, "✓");

        if (hasViolations && result == MessageBoxResult.Yes)
        {
            var dlg = new MultiDeviceCallMigrationDialog(_store) { Owner = this };
            dlg.ShowDialog();
        }
    }

    /// <summary>오류 메시지 리스트를 다이얼로그 탭에 표시 (IoSignalPipeline 결과 기반).</summary>
    private void DisplayErrorsFromMessages(System.Collections.Generic.IReadOnlyList<string> messages)
    {
        _errorItems.Clear();
        foreach (var msg in messages.Distinct())
            _errorItems.Add(new ErrorDisplayItem { ErrorType = "오류", Message = msg });

        Step3Section.ErrorsTabItem.Visibility = _errorItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Step3Section.ErrorCountText.Text = _errorItems.Count.ToString();
    }
}
