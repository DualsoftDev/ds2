using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ds2.Core.Store;

namespace Promaker.Dialogs;

public partial class TagWizardDialog
{
    /// <summary>
    /// Step 1 초기화
    /// </summary>
    private void InitializeStep1()
    {
        // 프로젝트 통계 조회
        var flows = Queries.allFlows(_store);
        var works = flows.SelectMany(f => Queries.worksOf(f.Id, _store)).ToArray();
        var calls = works.SelectMany(w => Queries.callsOf(w.Id, _store)).ToArray();
        var allApiCalls = Queries.allApiCalls(_store);

        Step1Section.FlowCountText.Text = $"{flows.Length}";
        Step1Section.WorkCountText.Text = $"{works.Length}";
        Step1Section.CallCountText.Text = $"{calls.Length}";
        Step1Section.DeviceCountText.Text = $"{allApiCalls.Length}";

        // Step 1 이 곧바로 선두 주소 설정 편집 화면이므로 Flow/System base 데이터를 즉시 로드
        // (내부적으로 LoadSystemBase + LoadFlowBase 를 수행)
        LoadTemplateFileList();
    }

    /// <summary>
    /// 다음 버튼 클릭
    /// </summary>
    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep == 1)
        {
            // Step 1 → Step 2: 템플릿 편집
            LoadTemplateFileList();
            MoveToStep(2);
        }
        else if (_currentStep == 2)
        {
            // Step 2 → Step 3: 신호 생성 전 템플릿 유효성 검증 (API 이름 필수)
            if (!ValidateSignalTemplates())
                return;

            if (!GenerateSignals())
                return;

            MoveToStep(3);
        }
        else if (_currentStep == 3)
        {
            // Step 3 적용: ConfirmAndApplyPatterns 가 단일 통합 리포트까지 처리.
            if (!ConfirmAndApplyPatterns()) return;
            _appliedInStep3 = true;
            UpdateButtons();
        }
    }

    /// <summary>
    /// 이전 버튼 클릭
    /// </summary>
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
        {
            MoveToStep(_currentStep - 1);
        }
    }

    /// <summary>
    /// 닫기 버튼 클릭
    /// </summary>
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 단계 이동
    /// </summary>
    private void MoveToStep(int step)
    {
        _currentStep = step;
        if (step != 3) _appliedInStep3 = false;

        // 컨텐츠 표시
        Step1Section.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Section.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Section.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

        // 단계 인디케이터 업데이트
        UpdateStepIndicators();

        // 버튼 상태 업데이트
        UpdateButtons();
    }

    /// <summary>
    /// 단계 인디케이터 업데이트
    /// </summary>
    private void UpdateStepIndicators()
    {
        var accentBrush = (Brush)FindResource("AccentBrush");
        var greenBrush = (Brush)FindResource("GreenAccentBrush");
        var secondaryBrush = (Brush)FindResource("SecondaryBackgroundBrush");
        var lightText = (Brush)FindResource("AlwaysLightTextBrush");
        var secondaryText = (Brush)FindResource("SecondaryTextBrush");

        ApplyStepStyle(Step1Bar, Step1Text, 1, accentBrush, greenBrush, secondaryBrush, lightText, secondaryText);
        ApplyStepStyle(Step2Bar, Step2Text, 2, accentBrush, greenBrush, secondaryBrush, lightText, secondaryText);
        ApplyStepStyle(Step3Bar, Step3Text, 3, accentBrush, greenBrush, secondaryBrush, lightText, secondaryText);
    }

    /// <summary>
    /// 개별 스텝 인디케이터 스타일 적용
    /// </summary>
    private void ApplyStepStyle(
        Border bar, TextBlock text, int stepNumber,
        Brush accentBrush, Brush greenBrush, Brush secondaryBrush,
        Brush lightText, Brush secondaryText)
    {
        if (_currentStep == stepNumber)
        {
            bar.Background = accentBrush;
            text.Foreground = lightText;
            text.FontWeight = FontWeights.SemiBold;
        }
        else if (_currentStep > stepNumber)
        {
            bar.Background = greenBrush;
            text.Foreground = lightText;
            text.FontWeight = FontWeights.Normal;
        }
        else
        {
            bar.Background = secondaryBrush;
            text.Foreground = secondaryText;
            text.FontWeight = FontWeights.Normal;
        }
    }

    /// <summary>
    /// 버튼 상태 업데이트 — 3단계 구조: Step 3 적용 후엔 닫기 버튼만 노출.
    /// </summary>
    private void UpdateButtons()
    {
        BackButton.Visibility = _currentStep > 1 ? Visibility.Visible : Visibility.Collapsed;

        var atFinalApplied = _currentStep == 3 && _appliedInStep3;
        if (atFinalApplied)
        {
            NextButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
        }
        else
        {
            NextButton.Visibility = Visibility.Visible;
            CloseButton.Visibility = Visibility.Collapsed;
            if (_currentStep == 1)      NextButton.Content = "다음 ▶";
            else if (_currentStep == 2) NextButton.Content = "신호 생성 ▶";
            else if (_currentStep == 3) NextButton.Content = "적용 ▶";
        }
    }

    private bool _appliedInStep3;
}
