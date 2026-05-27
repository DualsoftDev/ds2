using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Promaker.Dialogs.ConfigEditor.Models;

/// <summary>
/// API 매핑 정의 (MappingSet 내부)
/// </summary>
public partial class ApiMapping : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _outputKeywords = "";

    [ObservableProperty]
    private string _inputKeywords = "";
}

/// <summary>
/// 디바이스-API 매핑 세트
/// </summary>
public partial class MappingSetItem : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _deviceKeywords = "";

    [ObservableProperty]
    private bool _isExpanded;

    public ObservableCollection<ApiMapping> Apis { get; } = new();
}

/// <summary>
/// 대칭 규칙 (Output → Input 패턴)
/// </summary>
public partial class SymmetryRuleItem : ObservableObject
{
    [ObservableProperty]
    private string _outputPattern = "";

    [ObservableProperty]
    private string _inputPattern = "";

    [ObservableProperty]
    private string _description = "";
}

/// <summary>
/// 명시적 매핑 (Output → Input 키워드)
/// </summary>
public partial class ExplicitMappingItem : ObservableObject
{
    [ObservableProperty]
    private string _outputKeyword = "";

    [ObservableProperty]
    private string _inputKeyword = "";

    [ObservableProperty]
    private string _description = "";
}

/// <summary>
/// MappingSet 카테고리 (UI 그룹핑용)
/// </summary>
public partial class MappingSetCategory : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private bool _isExpanded = false;

    public ObservableCollection<MappingSetItem> MappingSets { get; } = new();
}

// ============================================================
// Node Connection Rules Models
// ============================================================

/// <summary>
/// Call 노드 연결 순서 (예: START → *_IN_OK → WORK_COMP_RST)
/// </summary>
public partial class CallSequenceItem : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    /// <summary>
    /// 쉼표로 구분된 패턴 (와일드카드 * 지원)
    /// </summary>
    [ObservableProperty]
    private string _steps = "";

    /// <summary>
    /// 연결 타입: StartReset, Start, Reset, Interlock
    /// </summary>
    [ObservableProperty]
    private string _type = "StartReset";
}

/// <summary>
/// Work 노드 간 연결 규칙
/// </summary>
public partial class WorkConnectionItem : ObservableObject
{
    /// <summary>
    /// 소스 Work 패턴 (와일드카드 * 지원)
    /// </summary>
    [ObservableProperty]
    private string _fromPattern = "*";

    /// <summary>
    /// 대상 Work 패턴 (와일드카드 * 지원)
    /// </summary>
    [ObservableProperty]
    private string _toPattern = "*";

    /// <summary>
    /// 연결 타입: StartReset, Start, Reset, Interlock
    /// </summary>
    [ObservableProperty]
    private string _type = "StartReset";

    /// <summary>
    /// 연결 모드: Sequential (순차), Parallel (병렬)
    /// </summary>
    [ObservableProperty]
    private string _mode = "Sequential";
}

/// <summary>
/// 디바이스 쌍 연결 규칙 (예: SV ↔ CT)
/// </summary>
public partial class DevicePairItem : ObservableObject
{
    [ObservableProperty]
    private string _device1 = "";

    [ObservableProperty]
    private string _device2 = "";

    /// <summary>
    /// 연결 타입: Interlock, StartReset, etc.
    /// </summary>
    [ObservableProperty]
    private string _type = "Interlock";
}

/// <summary>
/// XGK 슬롯 정의 — P-prefix 주소가 입력/출력 공통이라 슬롯별 주소 범위로 disambiguate.
/// 예) 슬롯 0(XGI-D28A/B 입력) → P00000 ~ P0003F = Input
/// </summary>
public partial class XgkSlotItem : ObservableObject
{
    [ObservableProperty]
    private int _slot;

    [ObservableProperty]
    private string _module = "";

    /// <summary>Input / Output / Memory</summary>
    [ObservableProperty]
    private string _direction = "Input";

    /// <summary>예: P00000</summary>
    [ObservableProperty]
    private string _addressStart = "";

    /// <summary>예: P0003F</summary>
    [ObservableProperty]
    private string _addressEnd = "";

    [ObservableProperty]
    private string _description = "";
}

/// <summary>
/// AutoPre(선행 조건) 규칙
/// </summary>
public partial class AutoPreRuleItem : ObservableObject
{
    /// <summary>
    /// 소스 패턴 (와일드카드 * 지원)
    /// </summary>
    [ObservableProperty]
    private string _sourcePattern = "";

    /// <summary>
    /// 타겟 패턴 (와일드카드 * 지원)
    /// </summary>
    [ObservableProperty]
    private string _targetPattern = "";

    /// <summary>
    /// 연결 타입: AutoPre, Start, Reset, StartReset
    /// </summary>
    [ObservableProperty]
    private string _type = "AutoPre";
}
