using System;
using System.Linq;
using System.Text;
using System.Windows;
using Ds2.Core;
using Scenario = Ds2.Core.SimulationResultSnapshotTypes.SimulationScenario;

namespace Promaker.Dialogs;

public partial class SimulationScenariosDialog
{
    /// <summary>요약 탭에 표시 + 복사 가능한 가독성 좋은 텍스트 생성.</summary>
    private static string BuildDetailText(Scenario scenario)
    {
        var meta = scenario.Meta ?? new SimulationResultSnapshotTypes.SimulationMeta();
        var sb = new StringBuilder();

        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine($"  시나리오: {meta.ScenarioName}");
        sb.AppendLine($"  실행 시각: {meta.RunDate:yyyy-MM-dd HH:mm:ss}    Duration: {FmtD(meta.RunDuration_s)} s");
        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine();

        sb.AppendLine("[ 시뮬레이터 정보 ]");
        sb.AppendLine($"  Simulator   : {meta.SimulatorName} v{meta.SimulatorVersion}");
        sb.AppendLine($"  ModelHash   : {meta.Ds2ModelHash}");
        sb.AppendLine($"  Seed        : {(Microsoft.FSharp.Core.FSharpOption<int>.get_IsSome(meta.Seed) ? meta.Seed.Value.ToString() : "(none)")}");
        sb.AppendLine();

        if (Microsoft.FSharp.Core.FSharpOption<SimulationResultSnapshotTypes.KpiThroughput>.get_IsSome(scenario.Throughput))
        {
            var throughput = scenario.Throughput.Value;
            sb.AppendLine("[ 처리량 (Throughput) ]");
            sb.AppendLine($"  완주 토큰   : {throughput.TotalUnitsProduced} 개");
            sb.AppendLine($"  시간당      : {FmtD(throughput.ThroughputPerHour)} /h");
            sb.AppendLine($"  평균 CT     : {FmtD(throughput.AverageCycleTime_s)} s    (모든 Work 사이클 평균)");
            sb.AppendLine();
        }

        if (Microsoft.FSharp.Core.FSharpOption<SimulationResultSnapshotTypes.KpiCapacity>.get_IsSome(scenario.Capacity))
        {
            var capacity = scenario.Capacity.Value;
            sb.AppendLine("[ 용량 (Capacity) ]");
            sb.AppendLine($"  Design      : {FmtD(capacity.DesignCapacity)}");
            sb.AppendLine($"  Effective   : {FmtD(capacity.EffectiveCapacity)}");
            sb.AppendLine($"  Actual      : {FmtD(capacity.ActualCapacity)}");
            sb.AppendLine($"  Util(eff)   : {FmtD(capacity.EffectiveUtilization_pct)} %");
            sb.AppendLine();
        }

        sb.AppendLine("[ 데이터 수집 ]");
        sb.AppendLine($"  CycleTimes  : {scenario.CycleTimes.Count} 항목");
        sb.AppendLine($"  PerToken    : {scenario.PerTokenKpis.Count} 종류");
        sb.AppendLine($"  OEE 항목    : {scenario.OeeItems.Count}");
        sb.AppendLine($"  Constraints : {scenario.Constraints.Count}");
        sb.AppendLine($"  Resources   : {scenario.ResourceUtilizations.Count}");
        sb.AppendLine();

        if (scenario.PerTokenKpis.Count > 0)
        {
            sb.AppendLine("[ 토큰 유형별 요약 ]   (Source 생성 → Sink 소멸)");
            sb.AppendLine();

            int idx = 0;
            foreach (var token in scenario.PerTokenKpis)
            {
                idx++;
                sb.AppendLine($"  {idx}. {token.OriginName}  ({token.SpecLabel})");
                sb.AppendLine($"     완주        : {token.CompletedCount} / {token.InstanceCount}");
                sb.AppendLine($"     통과 시간   : Avg {FmtD(token.AvgTraversalTime_s)} s    Min {FmtD(token.MinTraversalTime_s)} s    Max {FmtD(token.MaxTraversalTime_s)} s");
                sb.AppendLine($"     TP/h        : {FmtD(token.ThroughputPerHour)}");

                if (token.WorkBreakdown != null && token.WorkBreakdown.Count > 0)
                {
                    var sumAvg = token.WorkBreakdown.Sum(b => b.AvgGoingTime_s);
                    sb.AppendLine($"     경유 Work   : {token.WorkBreakdown.Count} 개   (Σ avg ≈ {FmtD(sumAvg)} s)");
                    foreach (var breakdown in token.WorkBreakdown)
                    {
                        sb.AppendLine($"        • {breakdown.WorkName,-32}  visits={breakdown.VisitCount,-3}  avgGoing={FmtD(breakdown.AvgGoingTime_s)} s");
                    }
                }

                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private void CopyDetail_Click(object sender, RoutedEventArgs e)
    {
        var text = DetailText.Text ?? "";
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            Clipboard.SetText(text);
            CopyHintText.Text = "✓ 클립보드에 복사됨";
        }
        catch (Exception ex)
        {
            CopyHintText.Text = $"복사 실패: {ex.Message}";
        }
    }
}
