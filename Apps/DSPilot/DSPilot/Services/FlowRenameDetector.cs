// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
namespace DSPilot.Services;

/// <summary>
/// AASX 에서 flow 이름만 바뀐 경우를 찾는 순수 함수(2026-09-11).
/// <para>
/// 근거 = <c>dspFlow.flowId</c>(모델 Flow GUID 스냅샷). 모델의 (GUID, 이름) 과 DB 의 (flowId, flowName) 을 대조해
/// "GUID 는 같은데 이름이 다른" 행을 리네임으로 본다. 새 이름 행이 DB 에 이미 있으면(동명 flow 가 따로 있었거나
/// UPSERT 가 먼저 돌았음) 승계하지 않고 경고만 남긴다 — 두 설비의 이력을 합쳐 버리는 것이 삭제보다 나쁘다.
/// </para>
/// <para>
/// 배경: 종전 ReloadAndResync 는 새 모델에 없는 flow 이름의 dspFlow/dspCall/dspFlowHistory 를 무조건 삭제했다
/// ("삭제/리네임된 Flow 정리"). flow 이름 한 글자만 바꿔도 그 설비의 사이클 이력이 확인 없이 지워졸던 경로.
/// 첫 배포 부팅에는 flowId 가 비어 있어 판정 불가 → UPSERT 가 채운 뒤부터 유효(GUID 백필과 같은 한계).
/// </para>
/// </summary>
public static class FlowRenameDetector
{
    public sealed record DbFlow(string FlowName, string? FlowId);
    public sealed record Rename(string OldName, string NewName, Guid Id);

    public sealed class Result
    {
        public List<Rename> Renames { get; } = [];
        public List<string> Warnings { get; } = [];
    }

    public static Result Detect(IEnumerable<(Guid Id, string Name)> model, IEnumerable<DbFlow> db)
    {
        var result = new Result();
        var rows = db.Where(r => !string.IsNullOrWhiteSpace(r.FlowName)).ToList();
        var dbNames = new HashSet<string>(rows.Select(r => r.FlowName), StringComparer.Ordinal);
        var rowsById = new Dictionary<Guid, List<DbFlow>>();
        foreach (var r in rows)
        {
            if (!Guid.TryParse(r.FlowId, out var id) || id == Guid.Empty) continue;
            if (!rowsById.TryGetValue(id, out var list)) rowsById[id] = list = [];
            list.Add(r);
        }
        if (rowsById.Count == 0) return result;

        var seenModelIds = new HashSet<Guid>();
        foreach (var (id, name) in model)
        {
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(name)) continue;
            if (!seenModelIds.Add(id))
            {
                result.Warnings.Add($"모델에 같은 GUID 의 flow 가 둘 이상({id}) — '{name}' 리네임 판정 건너뜀");
                continue;
            }
            if (!rowsById.TryGetValue(id, out var candidates)) continue;
            foreach (var row in candidates)
            {
                if (string.Equals(row.FlowName, name, StringComparison.Ordinal)) continue;
                if (dbNames.Contains(name))
                {
                    result.Warnings.Add(
                        $"flow '{row.FlowName}' → '{name}' 리네임으로 보이지만 DB 에 '{name}' 행이 이미 있어 승계하지 않음(이력 병합 방지)");
                    continue;
                }
                result.Renames.Add(new Rename(row.FlowName, name, id));
                dbNames.Add(name);
            }
        }
        return result;
    }
}

/// <summary>flow 리네임 승계 1건의 결과 — 화면/API 안내용(설정 화면 '참조 재해석' 줄).</summary>
public sealed record FlowRenameRecord(
    DateTime AtUtc, string OldName, string NewName, Guid Id,
    int Flows, int Calls, int History, int Oee, int Settings);
