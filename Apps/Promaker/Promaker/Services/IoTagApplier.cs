using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Dialogs;

namespace Promaker.Services;

/// <summary>
/// IoBatchRow 컬렉션을 Ds2.Editor 의 batch 확장으로 일괄 적용.
/// 단일 transaction + Call 별 1회 이벤트 — N transactions/events → 최소화.
/// </summary>
public static class IoTagApplier
{
    public sealed record ApplyResult(int SuccessCount, IReadOnlyList<string> FailedItems)
    {
        public int FailedCount => FailedItems.Count;
        public bool AnyFailed => FailedCount > 0;
    }

    public static ApplyResult Apply(DsStore store, IEnumerable<IoBatchRow> rows)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));

        var failed = new List<string>();
        var entries = new List<(Guid, Guid, IOTag, IOTag)>();

        foreach (var row in rows)
        {
            if (row.CallId == Guid.Empty || row.ApiCallId == Guid.Empty)
            {
                failed.Add($"{row.Flow}/{row.Device}/{row.Api}: Call/ApiCall 매칭 실패");
                continue;
            }
            entries.Add((
                row.CallId, row.ApiCallId,
                new IOTag(row.OutSymbol ?? "", row.OutAddress ?? "", ""),
                new IOTag(row.InSymbol  ?? "", row.InAddress  ?? "", "")));
        }

        int success = 0;
        try
        {
            success = store.UpdateApiCallIoTagsBatch(entries);
        }
        catch (Exception ex)
        {
            failed.Add($"일괄 적용 예외: {ex.Message}");
        }

        return new ApplyResult(success, failed);
    }
}
