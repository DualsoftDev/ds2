using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Dialogs;

namespace Promaker.Services;

/// <summary>
/// IoBatchRow 컬렉션을 DsStore.UpdateApiCallIoTags 로 일괄 적용.
/// 이전엔 TagWizardDialog.ApplySignals 와 TagWizardBasicDialog.Apply_Click 두 곳에서
/// 동일 루프를 따로 작성 — 단일 진입점으로 통합.
/// </summary>
public static class IoTagApplier
{
    public sealed record ApplyResult(int SuccessCount, IReadOnlyList<string> FailedItems)
    {
        public int FailedCount => FailedItems.Count;
        public bool AnyFailed => FailedCount > 0;
    }

    /// <summary>
    /// rows 의 (CallId, ApiCallId) 가 비어있지 않은 항목만 ApiCall 의 InTag/OutTag 에 덮어쓴다.
    /// Out → OutTag, In → InTag. 행 단위 예외는 FailedItems 에 누적.
    /// </summary>
    public static ApplyResult Apply(DsStore store, IEnumerable<IoBatchRow> rows)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));

        int success = 0;
        var failed = new List<string>();

        foreach (var row in rows)
        {
            if (row.CallId == Guid.Empty || row.ApiCallId == Guid.Empty)
            {
                failed.Add($"{row.Flow}/{row.Device}/{row.Api}: Call/ApiCall 매칭 실패");
                continue;
            }

            try
            {
                store.UpdateApiCallIoTags(
                    row.CallId,
                    row.ApiCallId,
                    new IOTag(row.OutSymbol ?? "", row.OutAddress ?? "", ""),
                    new IOTag(row.InSymbol ?? "", row.InAddress ?? "", ""));
                success++;
            }
            catch (Exception ex)
            {
                failed.Add($"{row.Flow}/{row.Device}/{row.Api}: {ex.Message}");
            }
        }

        return new ApplyResult(success, failed);
    }
}
