using Promaker.Services;

namespace Promaker.Dialogs;

/// <summary>32점 chunked 뷰 전용 read-only 행 — Word/Bit 인덱스 + 패턴 텍스트.</summary>
public sealed record IndexedPatternRow(int Word, int Bit, string Pattern)
{
    /// "[ 0.00]" 형식의 인덱스 라벨.
    public string IndexLabel => $"[{Word,2}.{Bit:D2}]";
}

/// <summary>
/// Dummy 신호 행 (Preview용)
/// </summary>
public record DummySignalRow(
    string Flow,
    string Work,
    string Call,
    string Symbol,
    string Address,
    string Type,
    string DataType
);

/// <summary>
/// 매칭 실패 신호 행
/// </summary>
public record UnmatchedSignalRow(
    string Flow,
    string Device,
    string Api,
    string OutSymbol,
    string OutAddress,
    string InSymbol,
    string InAddress,
    string FailureReason
);

/// <summary>시스템 주소 설정 행 (DataGrid 바인딩용)</summary>
public class SystemBaseRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    string _systemType = "", _iwBase = "", _qwBase = "", _mwBase = "";
    bool _isEnabled = false;
    public string SystemType { get => _systemType; set => SetProperty(ref _systemType, value); }
    public bool   IsEnabled  { get => _isEnabled;  set => SetProperty(ref _isEnabled, value); }
    public string IW_Base    { get => _iwBase;     set => SetProperty(ref _iwBase, value); }
    public string QW_Base    { get => _qwBase;     set => SetProperty(ref _qwBase, value); }
    public string MW_Base    { get => _mwBase;     set => SetProperty(ref _mwBase, value); }
}

/// <summary>Flow 주소 설정 행 (DataGrid 바인딩용)</summary>
public class FlowBaseRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    string _flowName = "", _iwBase = "", _qwBase = "", _mwBase = "";
    public string FlowName { get => _flowName; set => SetProperty(ref _flowName, value); }
    public string IW_Base  { get => _iwBase;   set => SetProperty(ref _iwBase, value); }
    public string QW_Base  { get => _qwBase;   set => SetProperty(ref _qwBase, value); }
    public string MW_Base  { get => _mwBase;   set => SetProperty(ref _mwBase, value); }
}

/// <summary>
/// 신호 패턴 행 (IW/QW/MW 그리드 바인딩용)
/// </summary>
public class SignalPatternRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private const string ApiNoneSentinel = IoConstants.ApiNoneSentinel;

    string _apiName = "", _pattern = "", _targetFBType = "", _targetFBPort = "";
    bool _skipAddressAlloc, _isSpare;
    Ds2.Core.FbInputExpr? _preFbCondition;

    /// <summary>Pre-FB 입력식 — null 이면 단일 변수 와이어, 비어있지 않으면 LD contact 트리로 FB 핀 와이어.</summary>
    public Ds2.Core.FbInputExpr? PreFbCondition
    {
        get => _preFbCondition;
        set
        {
            _preFbCondition = value;
            OnPropertyChanged(nameof(PreFbCondition));
            OnPropertyChanged(nameof(PreFbConditionSummary));
            OnPropertyChanged(nameof(HasPreFbCondition));
        }
    }

    /// <summary>그리드 셀 표시용 한 줄 요약 — ST 형태.</summary>
    public string PreFbConditionSummary
    {
        get
        {
            if (_preFbCondition == null) return "";
            var node = Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.FromCore(_preFbCondition);
            return AAStoPLC.LadderEditor.Expression.CoilConditionConverter.ToStPreview(node);
        }
    }

    public bool HasPreFbCondition => _preFbCondition != null;

    public string ApiName
    {
        get => _apiName;
        set => SetProperty(ref _apiName, value ?? "");
    }

    /// 패턴 setter — ApiName 은 사용자가 명시적으로 선택한 값 그대로 보존.
    /// (Pattern 에 $(A)/$(C) 가 없어도 ApiName 이 "7TH_IN_OK" 같이 의미 있는 값이면 유지.)
    public string Pattern
    {
        get => _pattern;
        set
        {
            if (!SetProperty(ref _pattern, value ?? "")) return;
            OnPropertyChanged(nameof(DataType));
        }
    }

    /// 템플릿별 사용 FB 타입 (XGI_Template.xml 에서 선택)
    public string TargetFBType
    {
        get => _targetFBType;
        set
        {
            if (!SetProperty(ref _targetFBType, value ?? "")) return;
            OnPropertyChanged(nameof(PortOptions));
            OnPropertyChanged(nameof(DataType));
            OnPropertyChanged(nameof(IsDataTypeUserEditable));
            OnPropertyChanged(nameof(IsDataTypeFromFb));
        }
    }

    /// API 이름별 매핑할 FB Local Label
    public string TargetFBPort
    {
        get => _targetFBPort;
        set
        {
            if (!SetProperty(ref _targetFBPort, value ?? "")) return;
            OnPropertyChanged(nameof(DataType));
            OnPropertyChanged(nameof(IsDataTypeUserEditable));
            OnPropertyChanged(nameof(IsDataTypeFromFb));
        }
    }

    /// 주소 할당 미진행 (true) — IO 슬롯 미소비, Address 빈값. 예: _T1S, T#200MS.
    public bool SkipAddressAlloc
    {
        get => _skipAddressAlloc;
        set => SetProperty(ref _skipAddressAlloc, value);
    }

    /// 예비(Spare) 슬롯 (true) — 주소 1비트 예약, 신호 미생성. 셀 값 무시 (UI 비활성, 기존 내용 보존).
    public bool IsSpare
    {
        get => _isSpare;
        set
        {
            if (!SetProperty(ref _isSpare, value)) return;
            OnPropertyChanged(nameof(IsEditable));
            OnPropertyChanged(nameof(DataType));
            OnPropertyChanged(nameof(IsDataTypeUserEditable));
            OnPropertyChanged(nameof(IsDataTypeFromFb));
        }
    }

    /// IsSpare 의 반대 — UI 셀 IsEnabled 바인딩용.
    public bool IsEditable => !_isSpare;

    /// 사용자가 직접 선택한 데이터 타입 — FB Local Label 미설정 시에만 의미 있음.
    /// FB 가 설정되면 FB 포트 타입이 우선되어 무시됨.
    string _userDataType = "";
    public string UserDataType
    {
        get => _userDataType;
        set
        {
            if (!SetProperty(ref _userDataType, value ?? "")) return;
            OnPropertyChanged(nameof(DataType));
        }
    }

    /// 선택된 FB Local Label 의 IEC 데이터 타입.
    /// 우선순위: 시스템 플래그 → IsSpare → FB 포트 → 사용자 선택.
    public string DataType
    {
        get
        {
            var sysType = Plc.Xgi.XgiSystemFlags.tryGetTypeName(_pattern ?? "");
            if (Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(sysType)) return sysType.Value;
            if (_isSpare) return "BOOL";
            if (!string.IsNullOrEmpty(_targetFBType) && !string.IsNullOrEmpty(_targetFBPort))
                return FBPortCatalog.GetPortTypeMap(_targetFBType).TryGetValue(_targetFBPort, out var t) ? t : "";
            return _userDataType ?? "";
        }
        set
        {
            // FB 가 미설정일 때만 사용자 선택 의미 있음 — Cell 콤보 SelectedValue=DataType TwoWay 바인딩 호환.
            if (string.IsNullOrEmpty(_targetFBType) || string.IsNullOrEmpty(_targetFBPort))
                UserDataType = value ?? "";
        }
    }

    /// FB 가 미설정 = 사용자 선택 가능. FB 가 설정되면 read-only.
    public bool IsDataTypeUserEditable =>
        !_isSpare
        && (string.IsNullOrEmpty(_targetFBType) || string.IsNullOrEmpty(_targetFBPort));

    /// FB 가 설정되어 데이터타입이 FB 포트로부터 자동 결정됨 — IsEditable 콤보의 IsReadOnly 바인딩.
    public bool IsDataTypeFromFb => !IsDataTypeUserEditable;

    /// 사용자 선택 가능한 IEC 표준 데이터 타입 후보.
    public static System.Collections.Generic.IReadOnlyList<string> StandardDataTypes { get; }
        = new[] { "", "BOOL", "BYTE", "WORD", "DWORD", "LWORD", "SINT", "INT", "DINT", "LINT",
                  "USINT", "UINT", "UDINT", "ULINT", "REAL", "LREAL", "TIME", "STRING" };

    /// 선택된 FB 의 Local Label 목록 (콤보 데이터소스)
    public System.Collections.Generic.IReadOnlyList<string> PortOptions =>
        FbPortOptionsHelper.Get(_targetFBType);
}

/// AUX 포트 콤보 옵션의 방향 필터 — 입력/출력/전체.
public enum AuxPortDirectionFilter { All = 0, Input = 1, Output = 2 }

/// <summary>
/// AUX 포트 매핑 행 (AUX 포트 탭 그리드 바인딩용).
/// 하나의 API 당 하나의 행 — AutoAux/ComAux 포트 이름을 FB 포트 드롭다운에서 선택.
/// SystemType 이 바뀌면 전체 행 리로드 및 TargetFBType 변경에 따라 PortOptions 갱신.
/// </summary>
public class AuxPortRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    /// 방향 필터 — 모든 행이 공유하는 정적 상태. 변경 시 NotifyAll() 로 PortOptions 갱신.
    public static AuxPortDirectionFilter DirectionFilter { get; private set; } = AuxPortDirectionFilter.All;

    private static event System.Action? FilterChanged;

    public static void SetDirectionFilter(AuxPortDirectionFilter mode)
    {
        if (DirectionFilter == mode) return;
        DirectionFilter = mode;
        FilterChanged?.Invoke();
    }

    string _apiName = "", _targetFBType = "", _targetFBPort = "", _kind = "DirectFB", _auxKind = "AutoAux";
    Ds2.Core.FbInputExpr? _condition;

    public AuxPortRow()
    {
        FilterChanged += () => OnPropertyChanged(nameof(PortOptions));
    }

    public string ApiName      { get => _apiName;      set => SetProperty(ref _apiName, value ?? ""); }
    /// API 이름 콤보 후보 — row 생성 시 스냅샷 주입.
    /// 동적 RelativeSource 바인딩 race (ItemsSource 평가 지연 → SelectedItem 매칭 실패 → TwoWay null 역기록) 회피.
    public System.Collections.Generic.IReadOnlyList<string> ApiOptions { get; set; } = System.Array.Empty<string>();

    /// 외부에서 ApiOptions 갱신 후 콤보 ItemsSource 재바인딩 트리거용.
    public void RaisePropertyChanged(string propertyName) => OnPropertyChanged(propertyName);
    /// FB 입력 포트 (FB Local Label).
    public string TargetFBPort { get => _targetFBPort; set => SetProperty(ref _targetFBPort, value ?? ""); }
    /// 와이어 종류 — "DirectFB" / "AuxCoil".
    public string Kind
    {
        get => _kind;
        set
        {
            if (!SetProperty(ref _kind, value ?? "DirectFB")) return;
            OnPropertyChanged(nameof(IsAuxCoil));
        }
    }
    /// AuxCoil 일 때 CallCondition 속성 매핑 — "AutoAux" / "ComAux".
    public string AuxKind { get => _auxKind; set => SetProperty(ref _auxKind, value ?? "AutoAux"); }
    /// XAML AuxKind 콤보 IsEnabled 바인딩.
    public bool IsAuxCoil =>
        string.Equals(_kind, "AuxCoil", System.StringComparison.OrdinalIgnoreCase);

    /// 사용자 정의 추가 수식 — 자동 합성 조건과 AND 결합.
    public Ds2.Core.FbInputExpr? Condition
    {
        get => _condition;
        set
        {
            _condition = value;
            OnPropertyChanged(nameof(Condition));
            OnPropertyChanged(nameof(ConditionSummary));
        }
    }
    /// 그리드 셀 표시용 ST 한 줄 요약.
    public string ConditionSummary
    {
        get
        {
            if (_condition == null) return "";
            var node = Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.FromCore(_condition);
            return AAStoPLC.LadderEditor.Expression.CoilConditionConverter.ToStPreview(node);
        }
    }

    /// Kind 콤보 후보.
    public static System.Collections.Generic.IReadOnlyList<string> KindOptions { get; }
        = new[] { "DirectFB", "AuxCoil" };
    /// AuxKind 콤보 후보 — None 은 CallCondition 미사용 (entry.Condition 만 사용).
    public static System.Collections.Generic.IReadOnlyList<string> AuxKindOptions { get; }
        = new[] { "AutoAux", "ComAux", "None" };

    /// SystemType 선택 시 일괄 주입. PortOptions 는 동일 콤보 데이터소스로 의존.
    public string TargetFBType
    {
        get => _targetFBType;
        set
        {
            if (!SetProperty(ref _targetFBType, value ?? "")) return;
            OnPropertyChanged(nameof(PortOptions));
        }
    }

    /// AUX 콤보 옵션 — 방향 필터 적용. 빈 항목은 항상 첫 행 (선택 해제용).
    public System.Collections.Generic.IReadOnlyList<string> PortOptions =>
        FbPortOptionsHelper.GetByDirection(_targetFBType, DirectionFilter);
}

/// AUX 매핑 클립보드 직렬화 단위.
public sealed record AuxPortClipboardItem(
    string ApiName,
    string TargetFBPort,
    string Kind,
    string AuxKind,
    AAStoPLC.LadderEditor.Expression.ExprNode? Condition);

/// <summary>
/// EndPortMap 행 — API 이름 → 완료 FB 출력 포트 매핑.
/// PLC 인과 자동 게이팅 (A 의 완료 → B 시작) 에 사용. preset 단위 1:1 정적 매핑.
/// </summary>
public class EndPortRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    string _apiName = "", _endPort = "", _targetFBType = "";

    public string ApiName { get => _apiName; set => SetProperty(ref _apiName, value ?? ""); }
    public string EndPort { get => _endPort; set => SetProperty(ref _endPort, value ?? ""); }

    public System.Collections.Generic.IReadOnlyList<string> ApiOptions { get; set; }
        = System.Array.Empty<string>();

    public string TargetFBType
    {
        get => _targetFBType;
        set
        {
            if (!SetProperty(ref _targetFBType, value ?? "")) return;
            OnPropertyChanged(nameof(PortOptions));
        }
    }

    /// 완료 OUT 포트 후보 — FB 의 출력 포트만.
    public System.Collections.Generic.IReadOnlyList<string> PortOptions =>
        FbPortOptionsHelper.GetByDirection(_targetFBType, AuxPortDirectionFilter.Output);
}

/// IW/QW/MW 신호 패턴 행 클립보드 직렬화 단위 — 동일 타입 그리드끼리 호환.
public sealed record SignalRowClipboardItem(
    string ApiName,
    string Pattern,
    string TargetFBPort,
    bool   SkipAddressAlloc,
    bool   IsSpare,
    string UserDataType,
    AAStoPLC.LadderEditor.Expression.ExprNode? PreFbCondition);

/// <summary>
/// 오류 표시 항목 (ListBox 바인딩용)
/// </summary>
public class ErrorDisplayItem
{
    public string ErrorType { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>
/// FB Local Label 목록 조회 공용 헬퍼 — SignalPatternRow/AuxPortRow 의 ComboBox ItemsSource.
/// fbType 이 비어있으면 빈 배열 반환.
/// </summary>
internal static class FbPortOptionsHelper
{
    /// <summary>FB Local Label 콤보 옵션. 첫 항목 "" 은 미바인딩 선택용 — 사용자가 라벨을 비울 수 있게 한다.</summary>
    public static System.Collections.Generic.IReadOnlyList<string> Get(string? fbType)
    {
        if (string.IsNullOrEmpty(fbType)) return System.Array.Empty<string>();
        var labels = FBPortCatalog.GetLocalLabels(fbType!);
        var result = new System.Collections.Generic.List<string>(labels.Count + 1) { "" };
        result.AddRange(labels);
        return result;
    }

    /// <summary>방향(입력/출력/전체) 필터를 적용한 FB Local Label 콤보 옵션.</summary>
    public static System.Collections.Generic.IReadOnlyList<string> GetByDirection(string? fbType, AuxPortDirectionFilter mode)
    {
        if (string.IsNullOrEmpty(fbType)) return System.Array.Empty<string>();
        var (inputs, outputs) = FBPortCatalog.GetPortsByDirection(fbType!);
        var result = new System.Collections.Generic.List<string> { "" };
        switch (mode)
        {
            case AuxPortDirectionFilter.Input:  result.AddRange(inputs);  break;
            case AuxPortDirectionFilter.Output: result.AddRange(outputs); break;
            default: result.AddRange(inputs); result.AddRange(outputs); break;
        }
        return result;
    }
}
