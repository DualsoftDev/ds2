using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Ds2.CSV;
using Promaker.Presentation;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Microsoft.Win32;

namespace Promaker.Dialogs;

public class CsvRowViewModel
{
    public string FlowName { get; set; } = "";
    public string WorkName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string SystemName { get; set; } = "";
    public string ApiName { get; set; } = "";
    public string InName { get; set; } = "";
    public string InAddress { get; set; } = "";
    public string OutName { get; set; } = "";
    public string OutAddress { get; set; } = "";
}

public enum CsvImportMode
{
    Standard9,
    Basic3
}

public class BasicCsvRowViewModel
{
    public string FlowName { get; set; } = "";
    public string WorkName { get; set; } = "";
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }
    public string CallSummary { get; set; } = "";
}

public partial class CsvImportDialog : Window
{
    private const string DefaultImportedName = "csv_import";
    private const string DefaultSourceText = "또는 아래에 CSV를 직접 붙여넣으세요.";
    private const string EmptyPreviewText = "CSV 내용을 붙여넣거나 CSV 파일 불러오기를 누르세요.";
    private const string PreviewFailureText = "미리보기를 생성하지 못했습니다.";
    private const string SampleCsv = @"Flow,Work,Device,System,Api,InName,InAddress,OutName,OutAddress
Cutting,Load,Cylinder,Cutting_Cylinder,Up,입력신호,X10A0,출력신호,Y10B0
Cutting,Load,Sensor,Cutting_Sensor,Detect,,X10A2,,
Cutting,Load,Motor,Cutting_Motor,Run,,X10A3,,Y10B3
Cutting,Unload,Cylinder,Cutting_Cylinder,Down,하강신호,X10B0,하강출력,Y10C0
Cutting,Unload,Conveyor,Cutting_Conveyor,Forward,,,,Y10C1
Assembly,PartIn,Gripper,Assembly_Gripper,Grip,그립신호,X20A0,그립출력,Y20B0
Assembly,PartIn,Gripper,Assembly_Gripper,Release,릴리즈신호,X20A1,릴리즈출력,Y20B1
Assembly,PartIn,Sensor,Assembly_Sensor,Detect,,X20A2,,
Assembly,Process,Press,Assembly_Press,Down,프레스하강,X20C0,프레스출력,Y20D0
Assembly,Process,Press,Assembly_Press,Up,프레스상승,X20C1,프레스상승출력,Y20D1
Assembly,PartOut,Ejector,Assembly_Ejector,Push,,X20E0,,Y20F0
Assembly,PartOut,Ejector,Assembly_Ejector,Return,,X20E1,,Y20F1";

    private const string SampleBasicCsv = @"FLOW,WORK,CALL
드릴링,가공,리프트.하강=2S>컨베이어.이송시작=3S>위치센서A.감지=100MS>컨베이어.이송정지=300MS>클램프.전진=800MS>드릴.회전시작=500MS>드릴축.하강=1.5S>드릴축.상승=1.5S>드릴.회전정지=500MS;컨베이어.이송시작=3S>위치센서B.감지=100MS>컨베이어.이송정지=300MS
드릴링,측정반출,클램프.후진=800MS>측정헤드.하강=1S>측정기.측정시작=2S>측정기.결과판정=500MS>측정헤드.상승=1S>리프트.상승=2S>로봇.제품파지=3S>로봇.반출위치이동=5S>로봇.제품해제=1S>로봇.원점복귀=4S";

    private const string LlmPromptBasic = @"너는 자동화 공정 사양을 DS2 기본 CSV(ds2-basic-csv/v1)로 변환하는 생성기다.
아래 규칙을 따르고, 사용자가 공법을 설명하면 CSV만 출력한다.

[출력]
- 설명, 마크다운, 코드 펜스 없이 CSV 본문만 출력한다.
- 헤더는 정확히 FLOW,WORK,CALL 3열이다.
- 한 행은 Work 하나이며 같은 Flow도 매 행 FLOW 값을 반복한다.
- 데이터 행 순서 = Work 실행 순서다. 인접 Work는 Flow 가 바뀌어도 끊기지 않고 StartReset 으로 이어진다.
  이 체인이 스테이션 사이의 이송이다. 다음 Work 가 시작하면 이전 Work 가 리셋되어 다음 제품을 받는다.
- 모든 구분자는 반각이다. 전각 문자(＞ ； ，)를 쓰지 않는다.

[FLOW 정하기 — 가장 자주 틀리는 부분]
- FLOW 는 '제품 1개가 라인을 통과하는 단위'다. Flow 하나에 제품 하나가 올라간다.
- 그래서 Flow 개수 = 그 라인이 동시에 물고 있을 수 있는 제품 수(동시작업 캐파)다.
    제품을 한 번에 1개만 처리하는 라인      → Flow 1개
    LH/RH 지그가 각각 1장씩 물는 라인       → Flow 2개
    스테이션 4개가 각각 제품을 물는 라인    → Flow 4개
- 공정 단계는 Flow 가 아니라 Work 다.
  투입·이송·고정·가공·검사·반출은 전부 한 Flow 안의 Work 로 늘어놓는다.
  단계마다 Flow 를 새로 만들면 안 된다 — 제품 1개짜리 라인을 6개 제품이 도는 라인으로 잘못 만드는 것이다.
- 판단이 서지 않으면 스스로 물어라: '이 두 덩어리가 서로 다른 제품인가?'
    같은 제품의 앞뒤 단계다  → 같은 Flow, Work 를 나눈다
    동시에 다른 제품이 올라간다 → Flow 를 나눈다
- 라인에 제품이 몇 개 동시에 올라가는지 설명에 없으면 Flow 1개로 만든다. 임의로 늘리지 않는다.
- 이송라인(스테이션 ST01→ST02→…)은 스테이션 수 = 동시에 올라간 차체 수이므로 Flow 를 그만큼 만든다.
  Flow 끼리는 행 순서대로 자동으로 이어지므로 따로 연결 지시를 쓰지 않는다.

[WORK 정하기 — Work 는 최소로]
- Work 는 '스텝' 이다. Work 가 바뀔 때마다 리셋 경계가 생긴다(인접 Work 는 StartReset 으로 이어짐).
  Work 를 잘게 쪼개면 실제 공정에 없는 리셋 경계가 잔뜩 생겨 스텝 설계가 불가능해진다.
- 기본 전략: 한 Work 안에 넣을 수 있는 Call 은 최대한 넣고, 순서는 CALL 셀 안에서 DAG('>' 와 ';')로 표현한다.
  Work 수는 최소로 만든다.
- Call 하나마다 Work 를 하나씩 만들지 마라. 그건 스텝 설계가 아니라 동작 나열이다.
- Work 를 나누는 근거는 다음 세 가지뿐이다. 해당 없으면 나누지 않는다.
    1. 같은 Call 이 한 사이클에 다시 나와야 할 때 (한 Work 안에서는 순환이라 못 쓴다)
    2. 공정 위상이 바뀔 때 (투입 ↔ 배출, 작업완료 전후처럼 되돌아가는 경계)
    3. 사용자가 스텝을 명시적으로 나눠 말했을 때
- 순차 동작이 길다는 이유로 나누지 않는다. 길면 한 Work 안에서 '>' 로 계속 잇는다.
- 예: 로봇이 랙방이동 → 랙방취출 → 실러자세이동 → 실러도포 → 지그이동 → 지그로딩 → 원위치복귀 하고,
      다른 로봇이 안티스패터도포 → 원위치복귀 하는 경우
    틀림(Call 1개짜리 Work 8개):
      도포,원위치에서랙방이동,로봇1.랙방이동=3S
      도포,랙방취출,로봇1.랙방취출=2S
      도포,실러자세이동,로봇1.실러자세이동=3S
      ... (이런 식으로 8행)
    맞음(Work 1개, 안쪽은 DAG. 로봇 2대는 서로 독립이라 ';' 로 병렬):
      도포,실러도포,로봇1.랙방이동=3S>로봇1.랙방취출=2S>로봇1.실러자세이동=3S>로봇1.실러도포=8S>로봇1.지그이동=3S>로봇1.지그로딩=2S>로봇1.원위치복귀=3S;로봇2.안티스패터도포=6S>로봇2.원위치복귀=3S

[CALL 문법]
- Call 이름은 반드시 '디바이스.액션' 형식이다(점 정확히 1개).
- '>' 는 순차(Start) 연결, ';' 는 별도 경로 구분이다.
- 여러 경로의 노드와 엣지는 합집합으로 병합되어 하나의 DAG가 된다.
- 같은 Call 이름은 동일 노드다. 공유·분기·합류는 전체 이름을 각 경로에 반복해 표현한다.
  예: 컨베이어.시작>센서A.감지>컨베이어.정지;컨베이어.시작>센서B.감지>컨베이어.정지
- 별칭 문법(ID=디바이스.액션)은 없다. '=' 는 아래 동작 시간 지정에만 쓴다.

[병렬로 쓸 것 — 가장 자주 틀리는 부분]
- 한 Work 안의 Call 을 무조건 '>' 로 한 줄에 잇지 마라. '>' 는 '앞이 끝나야 뒤가 시작된다'는 뜻이다.
  서로 기다릴 이유가 없는 동작을 '>' 로 이으면 실제보다 사이클이 길어진다.
- 동시에 움직일 수 있으면 ';' 로 갈라 병렬로 쓴다. 같은 이름은 같은 노드이므로 합류도 이름 반복으로 만든다.
- 병렬로 쓸 대표 상황:
    서로 다른 디바이스가 같은 조건에서 함께 나간다   클램프1·2·3 동시 전진
    좌우·상하 대칭 동작                              LH/RH, 상부/하부
    독립 축이 동시에 이동                            X축·Y축 동시 이송
    선행조건이 같은 동작들                           같은 신호로 함께 풀리는 클램프
- 순차로 남길 것(물리적으로 기다려야 하는 것만):
    같은 디바이스의 앞뒤 동작                        실린더.전진 > 실린더.후진
    간섭이 있는 동작                                 지그 전진 후에야 러너 전진
    앞 동작의 결과가 조건인 동작                     클램프 잠김 후 체결
- 쓰는 법:
    분기      A.동작>B.동작;A.동작>C.동작            A 끝나면 B 와 C 가 동시에
    합류      B.동작>D.동작;C.동작>D.동작            B 와 C 가 모두 끝나야 D (AND 합류)
    분기+합류 A.x>B.x>D.x;A.x>C.x>D.x               A → (B,C 병렬) → D
    독립      B.동작;C.동작                          아무 순서 관계 없이 함께
- 예: 클램프 3개를 동시에 물고 다 물리면 슬라이드가 나가는 경우
    맞음: 클램프1.전진=800MS>슬라이드.전진=1S;클램프2.전진=800MS>슬라이드.전진=1S;클램프3.전진=800MS>슬라이드.전진=1S
    틀림: 클램프1.전진=800MS>클램프2.전진=800MS>클램프3.전진=800MS>슬라이드.전진=1S
    (틀린 쪽은 2.4초가 더 걸린다. 셋은 서로 기다릴 이유가 없다.)

[동작 시간]
- 모든 Call 에 동작 시간을 적는다. 형식은 'Call 뒤에 = 시간+단위'.
  예: 실린더.전진=1000MS, 런너.체결=2.5S
- 단위 MS(밀리초) 또는 S(초)를 반드시 붙인다. 단위가 없으면 오류다. 대소문자는 가리지 않는다.
- 사용자가 시간을 말했으면 그 값을 그대로 쓴다.
- 말하지 않았으면 아래 표로 설비 종류에 맞게 추정해서 채운다. 비워두지 않는다.
    공압 실린더(클램프·스토퍼·소형 슬라이드)   300~800MS
    대형 슬라이드·리프트·턴테이블              1~3S
    서보 이송(축 이동)                         500MS~2S
    나사 체결(너트런너·드라이버)                2~6S
    로봇 이재·취출·안착                        5~10S
    컨베이어 이송                              2~10S
    솔레노이드 밸브·척 개폐                     200~500MS
    센서 감지 확인·신호 출력·리셋               100MS
- 시간은 Call 이 아니라 '디바이스.액션' 단위 속성이다. 같은 Call 이 여러 Work 에 나오면
  한 곳에만 적는다. 값이 서로 다르면 먼저 적은 값이 쓰이고 경고가 뜬다.
- 적지 않은 동작은 기본 500ms 로 들어간다(오류 아님). 추정치는 불러오기 후 현장 실측으로 보정한다.
- 합류 노드는 모든 선행 경로가 완료(AND)된 후 시작된다. OR 합류는 표현할 수 없다.
- 같은 Call의 별도 재실행은 한 Work 안에 표현할 수 없다(순환으로 거부). Work를 분할한다.
  같은 이름은 같은 노드로 병합되므로, 한 Work 안에서 A>B>A 처럼 되돌아오면 DAG002 오류다.
  왕복 동작(하강-상승을 두 번 하는 픽앤플레이스 등)은 반드시 Work를 나눈다.
    틀림: 실장,칩실장작업,헤드.하강>노즐.흡착ON>헤드.상승>헤드.하강>노즐.흡착OFF>헤드.상승
    맞음: 실장,픽업작업,헤드.하강=500MS>노즐.흡착ON=200MS>헤드.상승=500MS
          실장,실장작업,헤드.하강=500MS>노즐.흡착OFF=200MS>헤드.상승=500MS
- 자기 Edge, 순환, 빈 노드/경로를 만들지 않는다.

[디바이스 규칙]
- 실린더·모터류 구동 디바이스는 상보 동작 쌍(전진-후진, 상승-하강, ON-OFF, 클램프-언클램프)을 함께 기재한다.
  공법에 반대 동작이 있는데 누락하면 안 된다. API가 1개뿐인 디바이스는 불러오기 시 경고 대상이다(센서류는 예외).
- 디바이스/액션 이름에 . > ; = 쉼표 따옴표를 쓰지 않는다.
- 예약어: 디바이스 BUFFER·CLEAR, 액션 DO·'-', '@' 접두 금지.

[정확성]
- 입력에 없는 센서, 완료확인, 안전동작, 원점복귀를 임의로 추가하지 않는다.
- Work 순서는 사용자가 제시한 순서를 유지한다.
- Flow 개수는 설명에서 읽어낸 동시작업 제품 수로만 정한다. 공정 단계 수로 정하지 않는다.
- 실행 관계가 불명확하면 추측하지 말고 질문한다.
- 순서를 지어내지 않는다. 설명에 선후가 없으면 ';' 로 병렬로 둔다. '>' 는 근거가 있을 때만 쓴다.

[예시 1 — 제품을 한 개씩 처리하는 라인 → Flow 1개, Work 최소]
입력: 리프트가 하강해 제품을 받고, 클램프 2개가 물면 드릴이 회전시작·하강·상승·정지한다.
      끝나면 클램프를 풀고 리프트가 상승해 로봇이 반출한다. 한 번에 한 개씩 가공한다.
출력:
FLOW,WORK,CALL
가공라인,가공,리프트.하강=2S>클램프1.전진=800MS>드릴.회전시작=500MS>드릴축.하강=1.5S>드릴축.상승=1.5S>드릴.회전정지=500MS;리프트.하강=2S>클램프2.전진=800MS>드릴.회전시작=500MS
가공라인,반출,클램프1.후진=800MS>리프트.상승=2S>로봇.제품파지=3S>로봇.반출=5S;클램프2.후진=800MS>리프트.상승=2S
→ Flow 1개: 제품이 한 개씩만 올라간다.
→ Work 2개뿐: 리프트가 하강·상승 양쪽에 나와야 하는데 한 Work 안에서는 순환이라 못 쓴다.
   그 경계에서만 나누고, 나머지 동작은 전부 Work 안 DAG 로 넣었다.
→ 클램프 2개는 서로 기다릴 이유가 없어 ';' 로 병렬, 둘 다 물려야 드릴이 도니 이름 반복으로 합류.

[예시 2 — LH/RH 지그가 각각 1장씩 무는 라인 → Flow 2개]
입력: LH 지그와 RH 지그가 각각 패널을 물고 동시에 작업한다. 각 지그는 클램프 전진,
      슬라이드 전진, 너트 체결(5초), 슬라이드 후진, 클램프 후진 순으로 돈다.
출력:
FLOW,WORK,CALL
LH,LH작업,LH클램프1.전진=800MS>LH슬라이드.전진=1S>LH런너1.체결=5S>LH슬라이드.후진=1S>LH클램프1.후진=800MS;LH클램프2.전진=800MS>LH슬라이드.전진=1S>LH런너2.체결=5S>LH슬라이드.후진=1S>LH클램프2.후진=800MS
RH,RH작업,RH클램프1.전진=800MS>RH슬라이드.전진=1S>RH런너1.체결=5S>RH슬라이드.후진=1S>RH클램프1.후진=800MS;RH클램프2.전진=800MS>RH슬라이드.전진=1S>RH런너2.체결=5S>RH슬라이드.후진=1S>RH클램프2.후진=800MS
→ 동시에 서로 다른 제품이 올라가므로 Flow 2개. 지그가 3대면 Flow 3개다.
→ Flow 는 행 순서대로 StartReset 으로 이어진다(LH작업 → RH작업). 제품이 자리에서 자리로 넘어가는 이송이다.
→ Flow 당 Work 1개: 되돌아오는 Call 도 위상 변화도 없으니 나눌 근거가 없다. 전부 Work 안 DAG 로 넣는다.
→ 클램프·런너 2조는 ';' 로 병렬, 슬라이드는 이름 반복으로 분기·합류.

이제 공법을 설명해 주시면 위 규칙에 따라 CSV만 출력한다.";

    private CsvDocument? _document;
    private BasicCsvDocument? _basicDocument;
    private string _lastErrorText = "";
    private string _autoProjectName = DefaultImportedName;
    private string _autoSystemName = DefaultImportedName;
    private string _sourceDisplayName = "붙여넣기";
    private bool _loadingFileContent;

    public CsvImportDialog()
    {
        InitializeComponent();

        ProjectNameBox.Text = DefaultImportedName;
        SystemNameBox.Text = DefaultImportedName;
        SourceText.Text = DefaultSourceText;
        ResetPreview(EmptyPreviewText);
        UpdatePreviewGridVisibility();

        Loaded += (_, _) => ContentBox.Focus();
    }

    public string ProjectName => ProjectNameBox.Text.Trim();

    public string SystemName => SystemNameBox.Text.Trim();

    public CsvDocument Document =>
        _document ?? throw new InvalidOperationException("CSV document is not loaded.");

    public CsvImportMode SelectedMode =>
        BasicModeRadio?.IsChecked == true ? CsvImportMode.Basic3 : CsvImportMode.Standard9;

    /// 기본 3열 모드에서 Start/Clear Work 를 자동 추가할지. 기본 켜짐.
    public bool AutoAddStartClear => AutoStartClearCheck?.IsChecked == true;

    public BasicCsvDocument BasicDocument =>
        _basicDocument ?? throw new InvalidOperationException("Basic CSV document is not loaded.");

    public string SourceDisplayName => _sourceDisplayName;

    private static string BuildPreviewSummary(CsvImportPreview preview, int entryCount)
    {
        var sb = new StringBuilder()
            .AppendLine($"✓ Flow: {preview.FlowNames.Length}개")
            .AppendLine($"✓ Work: {preview.WorkNames.Length}개")
            .AppendLine($"✓ Call: {preview.CallNames.Length}개")
            .AppendLine($"✓ Passive Device System: {preview.PassiveSystemNames.Length}개")
            .AppendLine();

        AppendSample(sb, "Flow 샘플", preview.FlowNames, 5);
        AppendSample(sb, "Work 샘플", preview.WorkNames, 5);
        AppendSample(sb, "Call 샘플", preview.CallNames, 5);

        if (entryCount > 100)
        {
            sb.AppendLine();
            sb.AppendLine($"※ 총 {entryCount}개 항목 중 100개만 미리보기에 표시됩니다.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildSyntheticWarningText(int syntheticApiCount) =>
        syntheticApiCount > 0
            ? $"⚠ Api 열이 비어 있는 {syntheticApiCount}개 항목은 Signal_<addr> 형식으로 자동 생성됩니다."
            : "";

    private static void AppendSample(StringBuilder sb, string label, IEnumerable<string> items, int take)
    {
        var sample = string.Join(", ", items.Take(take));
        if (!string.IsNullOrWhiteSpace(sample))
            sb.AppendLine($"{label}: {sample}");
    }

    private static bool ValidateRequired(Window owner, TextBox textBox, string label)
    {
        if (!string.IsNullOrWhiteSpace(textBox.Text?.Trim()))
            return true;

        DialogHelpers.Info(owner, $"{label} 이름을 입력하세요.", "CSV 불러오기");
        textBox.Focus();
        return false;
    }

    private static string OptionText(FSharpOption<string> value) =>
        value.GetOrDefault("");

    private static CsvRowViewModel ToRowViewModel(CsvEntry entry) =>
        new()
        {
            FlowName = entry.FlowName,
            WorkName = entry.WorkName,
            DeviceName = entry.DeviceName,
            SystemName = entry.SystemName,
            ApiName = entry.ApiName,
            InName = OptionText(entry.InName),
            InAddress = OptionText(entry.InAddress),
            OutName = OptionText(entry.OutName),
            OutAddress = OptionText(entry.OutAddress)
        };

    private void SetSourceDisplay(string displayName, string description)
    {
        _sourceDisplayName = displayName;
        SourceText.Text = description;
    }

    private void ResetDirectInputPreview()
    {
        SetSourceDisplay("붙여넣기", DefaultSourceText);
        ResetPreview(EmptyPreviewText);
    }

    private void SetPreviewState(
        CsvDocument? document,
        IEnumerable<CsvRowViewModel>? rows,
        string previewText,
        string? errorText = null,
        string? warningText = null)
    {
        _document = document;
        _basicDocument = null;
        _lastErrorText = errorText ?? "";
        PreviewGrid.ItemsSource = rows?.ToList();
        BasicPreviewGrid.ItemsSource = null;
        PreviewText.Text = previewText;
        ErrorBorder.Visibility = string.IsNullOrWhiteSpace(errorText) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = errorText ?? "";
        WarningBorder.Visibility = string.IsNullOrWhiteSpace(warningText) ? Visibility.Collapsed : Visibility.Visible;
        WarningText.Text = warningText ?? "";
    }

    private void ShowPreviewFailure(string message)
    {
        SetPreviewState(null, null, PreviewFailureText, errorText: message);
    }

    private void ShowInfo(string message, string title = "CSV 불러오기") =>
        DialogHelpers.Info(this, message, title);

    private void ShowError(string message, string title = "오류") =>
        DialogHelpers.Error(this, message, title);

    private void ApplyPreview(CsvDocument document, CsvImportPreview preview)
    {
        SetPreviewState(
            document,
            document.Entries.Take(100).Select(ToRowViewModel),
            BuildPreviewSummary(preview, document.Entries.Length),
            warningText: BuildSyntheticWarningText(preview.SyntheticApiCount));
    }

    private void SetBasicPreviewState(
        BasicCsvDocument? document,
        IEnumerable<BasicCsvRowViewModel>? rows,
        string previewText,
        string? errorText = null,
        string? warningText = null)
    {
        _basicDocument = document;
        _document = null;
        _lastErrorText = errorText ?? "";
        BasicPreviewGrid.ItemsSource = rows?.ToList();
        PreviewGrid.ItemsSource = null;
        PreviewText.Text = previewText;
        ErrorBorder.Visibility = string.IsNullOrWhiteSpace(errorText) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = errorText ?? "";
        WarningBorder.Visibility = string.IsNullOrWhiteSpace(warningText) ? Visibility.Collapsed : Visibility.Visible;
        WarningText.Text = warningText ?? "";
    }

    private void ApplyBasicPreview(BasicCsvDocument document, BasicCsvPreview preview)
    {
        var warnings = document.Warnings.ToList();
        SetBasicPreviewState(
            document,
            document.Works.Take(100).Select(ToBasicRowViewModel),
            BuildBasicPreviewSummary(preview, document.Works.Length, document.Durations.Length),
            warningText: warnings.Count > 0 ? string.Join("\n", warnings) : null);
    }

    private static BasicCsvRowViewModel ToBasicRowViewModel(BasicCsvWork work)
    {
        var nodeKeys = work.Nodes.Select(node => node.Item1).ToList();
        var summary = string.Join(" · ", nodeKeys.Take(8));
        if (nodeKeys.Count > 8)
            summary += " …";

        return new BasicCsvRowViewModel
        {
            FlowName = work.FlowName,
            WorkName = work.WorkName,
            NodeCount = nodeKeys.Count,
            EdgeCount = work.Edges.Length,
            CallSummary = summary
        };
    }

    private static string BuildBasicPreviewSummary(BasicCsvPreview preview, int workCount, int durationCount)
    {
        var sb = new StringBuilder()
            .AppendLine($"✓ Flow: {preview.FlowNames.Length}개")
            .AppendLine($"✓ Work: {preview.WorkNames.Length}개 — 행 순서 StartReset 체인 {preview.WorkArrowCount}개")
            .AppendLine($"✓ Call 노드: {preview.CallNodeCount}개 / Start 엣지: {preview.CallEdgeCount}개")
            .AppendLine($"✓ Passive Device System: {preview.PassiveSystemNames.Length}개");

        // 동작 시간이 실제로 읽혔는지 확인할 수 있어야 한다 — 미지정은 기본 500ms 로 들어간다.
        sb.AppendLine(durationCount > 0
            ? $"✓ 동작 시간 지정: {durationCount}개 (나머지는 기본 500ms)"
            : "· 동작 시간 지정 없음 — 모든 동작이 기본 500ms (지정: 디바이스.액션=1000MS)");
        sb.AppendLine();

        AppendSample(sb, "Flow 샘플", preview.FlowNames, 5);
        AppendSample(sb, "Work 샘플", preview.WorkNames, 5);
        AppendSample(sb, "Device 샘플", preview.PassiveSystemNames, 5);

        if (workCount > 100)
        {
            sb.AppendLine();
            sb.AppendLine($"※ 총 {workCount}개 Work 중 100개만 미리보기에 표시됩니다.");
        }

        return sb.ToString().TrimEnd();
    }

    private void UpdatePreviewGridVisibility()
    {
        var basic = SelectedMode == CsvImportMode.Basic3;
        PreviewBorderOf(basic ? Visibility.Collapsed : Visibility.Visible,
                        basic ? Visibility.Visible : Visibility.Collapsed);
        if (CopyPromptButton != null)
            CopyPromptButton.Visibility = basic ? Visibility.Visible : Visibility.Collapsed;
        // Start/Clear 자동 추가는 기본 3열 매퍼에만 적용된다.
        if (AutoStartClearCheck != null)
            AutoStartClearCheck.Visibility = basic ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PreviewBorderOf(Visibility standard, Visibility basic)
    {
        if (PreviewGrid?.Parent is FrameworkElement standardBorder)
            standardBorder.Visibility = standard;
        if (BasicPreviewBorder != null)
            BasicPreviewBorder.Visibility = basic;
    }

    private void ImportMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        UpdatePreviewGridVisibility();

        if (string.IsNullOrWhiteSpace(ContentBox.Text))
        {
            ResetDirectInputPreview();
            return;
        }

        TryLoadDocument();
    }

    private void ResetPreview(string message)
    {
        SetPreviewState(null, null, message);
    }

    private void ShowErrors(IEnumerable<string> errors)
    {
        SetPreviewState(null, null, PreviewFailureText, errorText: string.Join("\n", errors));
    }

    private void ApplyAutoNames(string defaultName)
    {
        var normalized = string.IsNullOrWhiteSpace(defaultName)
            ? DefaultImportedName
            : defaultName.Trim();

        UpdateAutoName(ProjectNameBox, ref _autoProjectName, normalized);
        UpdateAutoName(SystemNameBox, ref _autoSystemName, normalized);
    }

    private static void UpdateAutoName(TextBox textBox, ref string previousAutoName, string nextAutoName)
    {
        var current = textBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(current) || string.Equals(current, previousAutoName, StringComparison.Ordinal))
            textBox.Text = nextAutoName;

        previousAutoName = nextAutoName;
    }

    private bool TryLoadDocument()
    {
        var content = ContentBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            ResetPreview(EmptyPreviewText);
            return false;
        }

        if (SelectedMode == CsvImportMode.Basic3)
        {
            var basicResult = CsvImporter.parseBasicContent(content);
            if (basicResult.IsError)
            {
                ShowErrors(basicResult.ErrorValue);
                return false;
            }

            var basicDocument = basicResult.ResultValue;
            ApplyBasicPreview(basicDocument, CsvImporter.previewBasic(basicDocument));
            return true;
        }

        if (!TryGetDocument(CsvImporter.parseContent(content), out var document))
            return false;

        ApplyPreview(document, CsvImporter.preview(document));
        return true;
    }

    private bool TryGetDocument(FSharpResult<CsvDocument, FSharpList<string>> result, out CsvDocument document)
    {
        if (result.IsError)
        {
            ShowErrors(result.ErrorValue);
            document = default!;
            return false;
        }

        document = result.ResultValue;
        return true;
    }

    private void CopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMode != CsvImportMode.Basic3)
            return;

        try
        {
            Clipboard.SetText(LlmPromptBasic);
            ShowInfo(
                "LLM 생성 지침이 클립보드에 복사되었습니다.\n\n" +
                "ChatGPT·Gemini 등 다른 LLM에 붙여넣은 뒤 공법을 설명하면 CSV가 생성됩니다.\n" +
                "생성된 CSV를 이 창의 'CSV 내용'에 붙여넣으세요.",
                "지침 복사 완료");
        }
        catch (Exception ex)
        {
            ShowError($"클립보드 복사 실패: {ex.Message}");
        }
    }

    private void SaveSample_Click(object sender, RoutedEventArgs e)
    {
        var basic = SelectedMode == CsvImportMode.Basic3;
        var picker = new SaveFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv",
            DefaultExt = FileExtensions.Csv,
            FileName = basic ? "sample_basic.csv" : "sample.csv"
        };

        if (picker.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(picker.FileName, basic ? SampleBasicCsv : SampleCsv, Encoding.UTF8);
            ShowInfo($"샘플 CSV 파일이 저장되었습니다.\n\n{picker.FileName}", "샘플 저장 완료");
        }
        catch (Exception ex)
        {
            ShowError($"샘플 저장 실패: {ex.Message}");
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"
        };

        if (picker.ShowDialog() != true)
            return;

        try
        {
            _loadingFileContent = true;
            ContentBox.Text = CsvFileHelper.ReadAllTextShared(picker.FileName);
            SetSourceDisplay(Path.GetFileName(picker.FileName), $"원본: {Path.GetFileName(picker.FileName)}");
            ApplyAutoNames(Path.GetFileNameWithoutExtension(picker.FileName));
        }
        catch (Exception ex)
        {
            ShowPreviewFailure($"파일 읽기 실패: {ex.Message}");
        }
        finally
        {
            _loadingFileContent = false;
        }
    }

    private void ContentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        if (string.IsNullOrWhiteSpace(ContentBox.Text))
        {
            ResetDirectInputPreview();
            return;
        }

        if (!_loadingFileContent)
            SetSourceDisplay("붙여넣기", "원본: 직접 입력");

        TryLoadDocument();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateRequired(this, ProjectNameBox, "Project") ||
            !ValidateRequired(this, SystemNameBox, "Active System"))
            return;

        if (!TryLoadDocument())
        {
            var detail = string.IsNullOrWhiteSpace(_lastErrorText) ? "" : $"\n\n{_lastErrorText}";
            ShowInfo($"유효한 CSV 내용을 먼저 입력하세요.{detail}");
            ContentBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
