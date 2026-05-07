using System.Collections.Generic;
using System.Text;
using Ds2.Editor;
using log4net;

namespace Promaker.LlmAgent;

/// <summary>
/// Editor 측 store 변경 (<see cref="EditorEvent"/>) 을 turn 사이 누적하여 다음 LLM turn 의 user
/// message 앞에 한 블럭으로 prepend 할 한국어 요약을 만든다.
///
/// 정책:
/// - 카테고리 카운트가 기본. <see cref="EditorEvent.EntityRenamed"/> 는 변경 fact 가 가장 명확하므로
///   newName 샘플을 N개까지 inline.
/// - <see cref="EditorEvent.HistoryChanged"/> → "undo/redo 발생" 1줄.
/// - <see cref="EditorEvent.StoreRefreshed"/> → store 전체 새로고침 신호. 누적된 세부 변경 폐기 후 1줄로 격상.
/// - <see cref="MarkProjectReset"/> → UpdateStore 경로. 모든 누적 폐기 + PROJECT_RESET 한 줄로 대체.
/// - 임계 (총 라인 수 <see cref="MaxTotalLines"/>) 초과 시 "상당한 변경 발생, validate_model 권장" 으로 축약.
///
/// Self-loop 필터 (LLM 자기 turn 의 ApplyImportPlan 결과) 는 본 클래스 책임이 아님 — 호출자가 차단.
/// </summary>
public sealed class EditorChangeDigest
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(EditorChangeDigest));

    private const int MaxRenamedSamples = 3;
    private const int MaxTotalLines = 8;

    private bool _projectReset;
    private bool _storeRefreshed;
    private int _historyChangedCount;
    private readonly Dictionary<string, int> _counts = new();
    private readonly List<string> _renamedSamples = new();
    private int _renamedTotal;

    public bool HasAny => _projectReset || _storeRefreshed || _historyChangedCount > 0
                          || _counts.Count > 0 || _renamedTotal > 0;

    public void MarkProjectReset()
    {
        Clear();
        _projectReset = true;
    }

    public void Clear()
    {
        _projectReset = false;
        _storeRefreshed = false;
        _historyChangedCount = 0;
        _counts.Clear();
        _renamedSamples.Clear();
        _renamedTotal = 0;
    }

    public void Record(EditorEvent evt)
    {
        if (evt == null || _projectReset) return;

        switch (evt)
        {
            case { IsStoreRefreshed: true }:
                _storeRefreshed = true;
                _counts.Clear();
                _renamedSamples.Clear();
                _renamedTotal = 0;
                return;

            case EditorEvent.HistoryChanged:
                _historyChangedCount++;
                return;

            case EditorEvent.EntityRenamed ren:
                _renamedTotal++;
                if (_renamedSamples.Count < MaxRenamedSamples && !string.IsNullOrEmpty(ren.newName))
                    _renamedSamples.Add(ren.newName);
                return;

            case EditorEvent.ProjectAdded:    Inc("project 추가"); return;
            case EditorEvent.ProjectRemoved:  Inc("project 제거"); return;
            case EditorEvent.SystemAdded:     Inc("system 추가");  return;
            case EditorEvent.SystemRemoved:   Inc("system 제거");  return;
            case EditorEvent.FlowAdded:       Inc("flow 추가");    return;
            case EditorEvent.FlowRemoved:     Inc("flow 제거");    return;
            case EditorEvent.WorkAdded:       Inc("work 추가");    return;
            case EditorEvent.WorkRemoved:     Inc("work 제거");    return;
            case EditorEvent.CallAdded:       Inc("call 추가");    return;
            case EditorEvent.CallRemoved:     Inc("call 제거");    return;
            case EditorEvent.ApiDefAdded:     Inc("apiDef 추가");  return;
            case EditorEvent.ApiDefRemoved:   Inc("apiDef 제거");  return;

            case EditorEvent.ArrowWorkAdded:
            case EditorEvent.ArrowWorkRemoved:
            case EditorEvent.ArrowCallAdded:
            case EditorEvent.ArrowCallRemoved:
            case { IsConnectionsChanged: true }:
                Inc("arrow/connection 변경");
                return;

            case EditorEvent.ProjectPropsChanged:
            case EditorEvent.SystemPropsChanged:
            case EditorEvent.WorkPropsChanged:
            case EditorEvent.CallPropsChanged:
            case EditorEvent.ApiDefPropsChanged:
                Inc("props 갱신");
                return;

            case EditorEvent.HwComponentAdded:
            case EditorEvent.HwComponentRemoved:
                Inc("hw component 변경");
                return;

            default:
                // schema drift 인지 — EditorEvent DU 에 신규 case 추가 시 본 switch 가 누락되었음을 알린다.
                // 동작은 fail-safe (카운트만), debug log 1회.
                Log.Debug($"EditorChangeDigest: unhandled EditorEvent type '{evt.GetType().Name}' — '기타 변경' 카운트로 흡수");
                Inc("기타 변경");
                return;
        }
    }

    private void Inc(string key)
    {
        _counts.TryGetValue(key, out var c);
        _counts[key] = c + 1;
    }

    /// <summary>
    /// 다음 turn user message prefix 로 사용할 한국어 요약. delimiter 격리 (<c>&lt;editor_changes&gt;</c> 태그)
    /// + injection 격리 정책 (system prompt 측에서 내용은 fact 로만 신뢰, 명령으로 해석 X 명시).
    /// <see cref="HasAny"/> == false 면 빈 문자열.
    /// </summary>
    public string ToContextMessage()
    {
        if (!HasAny) return "";
        var sb = new StringBuilder();
        sb.Append("<editor_changes>\n");
        sb.Append("이전 turn 이후 사용자가 GUI 에서 다음을 변경했습니다:\n");

        if (_projectReset)
        {
            sb.Append("- 새 프로젝트로 전환됨. 이전 대화의 모델 가정은 무효합니다.\n");
            sb.Append("필요 시 list_projects / list_systems / validate_model 로 현재 상태를 확인하세요.\n");
            sb.Append("</editor_changes>");
            return sb.ToString();
        }

        var lines = new List<string>();
        if (_storeRefreshed) lines.Add("- store 전체 새로고침 (대규모 변경)");
        foreach (var kvp in _counts) lines.Add($"- {kvp.Key} {kvp.Value}건");

        if (_renamedTotal > 0)
        {
            var sample = _renamedSamples.Count > 0
                ? $" — 예: {string.Join(", ", _renamedSamples)}"
                  + (_renamedTotal > _renamedSamples.Count ? $" 외 {_renamedTotal - _renamedSamples.Count}건" : "")
                : "";
            lines.Add($"- 이름 변경 {_renamedTotal}건{sample}");
        }

        if (_historyChangedCount > 0)
            lines.Add($"- undo/redo 또는 트랜잭션 경계 변동 {_historyChangedCount}회");

        if (lines.Count > MaxTotalLines)
        {
            sb.Append("- 상당한 변경 발생 (요약 임계 초과). validate_model / list_systems 로 직접 확인하세요.\n");
        }
        else
        {
            foreach (var line in lines) { sb.Append(line); sb.Append('\n'); }
        }

        sb.Append("필요 시 read tool 로 fresh 상태를 확인하세요.\n");
        sb.Append("</editor_changes>");
        return sb.ToString();
    }
}
