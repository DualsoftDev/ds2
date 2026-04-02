using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ds2.Core;
using Ds2.Editor;
using Ds2.Store;
using Microsoft.FSharp.Core;

namespace CostSim;

internal static class DemoLibraryBuilder
{
    private const int SequenceStep = 10;

    public static string CreateDemoLibrary(string rootPath)
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);

        Directory.CreateDirectory(rootPath);

        foreach (var document in BuildDemoLibrarySeeds())
        {
            var store = BuildDemoStore(document);
            var outputPath = Path.Combine(rootPath, document.FileName);
            store.SaveToFile(outputPath);
        }

        return rootPath;
    }

    private static DsStore BuildDemoStore(DemoDocumentSeed document)
    {
        var store = new DsStore();
        var projectId = store.AddProject(document.ProjectName);
        ConfigureDemoProject(store.Projects[projectId], document);

        foreach (var systemSeed in document.Systems)
        {
            var systemId = store.AddSystem(systemSeed.Name, projectId, isActive: true);
            ConfigureDemoSystem(store.Systems[systemId], document, systemSeed);

            foreach (var flowSeed in systemSeed.Flows)
            {
                var flowId = store.AddFlow(flowSeed.Name, systemId);
                ConfigureDemoFlow(store.Flows[flowId], flowSeed);

                var nextSequenceOrder = SequenceStep;
                foreach (var workSeed in flowSeed.Works)
                {
                    var workId = store.AddWork(workSeed.Name, flowId);
                    ConfigureDemoWork(store, workId, nextSequenceOrder, workSeed);
                    nextSequenceOrder += SequenceStep;
                }

                FinalizeDemoFlow(store, flowId);
            }
        }

        return store;
    }

    private static void ConfigureDemoProject(Project project, DemoDocumentSeed document)
    {
        project.Author = document.Author;
        project.Version = document.Version;
        project.DateTime = DateTimeOffset.Now;
    }

    private static void ConfigureDemoSystem(DsSystem system, DemoDocumentSeed document, DemoSystemSeed seed)
    {
        system.SimulationProperties = FSharpOption<SimulationSystemProperties>.Some(new SimulationSystemProperties
        {
            Description = CostSimStoreHelper.ToOption(seed.Description),
            EngineVersion = CostSimStoreHelper.ToOption("CostSim EventDriven 2026"),
            LangVersion = CostSimStoreHelper.ToOption("DS2 Runtime 9.0"),
            Author = CostSimStoreHelper.ToOption(document.Author),
            DateTime = FSharpOption<DateTimeOffset>.Some(DateTimeOffset.Now),
            IRI = CostSimStoreHelper.ToOption($"https://demo.costsim.local/{SanitizeName(document.ProjectName)}/{SanitizeName(seed.Name)}"),
            SystemType = CostSimStoreHelper.ToOption(seed.SystemType),
            SimulationMode = "EventDriven",
            EnablePhysicsSimulation = false,
            EnableCollisionDetection = false,
            EnableBreakpoints = true,
            RandomSeed = FSharpOption<int>.Some(seed.RandomSeed),
            UseRandomVariation = true,
            VariationPercentage = 6.5,
            EnableCostSimulation = true,
            DefaultCurrency = "KRW",
            EnableOEETracking = true,
            OEECalculationInterval = TimeSpan.FromMinutes(30),
            TargetOEE = seed.TargetOee,
            EnableCapacitySimulation = true,
            ProductionLineCount = 1,
            ShiftPattern = SimShiftPattern.TwoShift,
            ShiftDuration = TimeSpan.FromHours(10),
            EnableBOMTracking = true,
            EnableInventorySimulation = true
        });
    }

    private static void ConfigureDemoFlow(Flow flow, DemoFlowSeed seed)
    {
        flow.SimulationProperties = FSharpOption<SimulationFlowProperties>.Some(new SimulationFlowProperties
        {
            Description = CostSimStoreHelper.ToOption(seed.Description),
            FlowSimulationEnabled = true,
            FlowSimulationMode = seed.Mode
        });
    }

    private static void ConfigureDemoWork(DsStore store, Guid workId, int sequenceOrder, DemoWorkSeed seed)
    {
        CostSimStoreHelper.UpdateWorkProperties(
            store,
            workId,
            seed.OperationCode,
            seed.DurationSeconds,
            seed.WorkerCount,
            seed.LaborCostPerHour,
            seed.EquipmentCostPerHour,
            seed.OverheadCostPerHour,
            seed.UtilityCostPerHour,
            seed.YieldRate,
            seed.DefectRate,
            "Demo Work Seed",
            emitHistory: false);

        var work = store.Works[workId];
        var props = CostSimStoreHelper.GetOrCreateProps(work);
        props.Description = CostSimStoreHelper.ToOption(seed.Description);
        props.Motion = CostSimStoreHelper.ToOption(seed.Motion);
        props.Script = CostSimStoreHelper.ToOption(
            $"// {seed.OperationCode} {seed.Name}{Environment.NewLine}" +
            "AcquireFixture();\n" +
            "ExecuteMainStep();\n" +
            "ValidateProcessWindow();\n" +
            "CommitProductionResult();");
        props.ExternalStart = seed.IsSource;
        props.IsFinished = false;
        props.NumRepeat = 1;
        props.EstimatedDuration = TimeSpan.FromSeconds(seed.DurationSeconds * 1.05);
        props.RecordStateChanges = true;
        props.EnableResourceContention = true;
        props.SequenceOrder = sequenceOrder;
        props.ResourceLockDuration = FSharpOption<TimeSpan>.Some(TimeSpan.FromSeconds(Math.Max(12.0, seed.DurationSeconds * 0.35)));
        props.SkillLevel = seed.SkillLevel;
        props.ReworkRate = seed.ActualProductionQty > 0 ? (double)seed.ReworkQty / seed.ActualProductionQty : 0.0;
        props.PlannedOperatingTime = TimeSpan.FromHours(seed.PlannedOperatingHours);
        props.ActualOperatingTime = TimeSpan.FromHours(seed.ActualOperatingHours);
        props.StandardCycleTime = Math.Max(1.0, seed.DurationSeconds * 0.92);
        props.ActualCycleTime = seed.DurationSeconds;
        props.PlannedProductionQty = seed.PlannedProductionQty;
        props.ActualProductionQty = seed.ActualProductionQty;
        props.GoodProductQty = seed.GoodProductQty;
        props.DefectQty = seed.DefectQty;
        props.ReworkQty = seed.ReworkQty;

        var availability = SimulationHelpers.calculateAvailability(props.ActualOperatingTime, props.PlannedOperatingTime);
        var performance = SimulationHelpers.calculatePerformance(props.ActualProductionQty, props.StandardCycleTime, props.ActualOperatingTime);
        var quality = SimulationHelpers.calculateQuality(props.GoodProductQty, props.ActualProductionQty);
        props.Availability = FSharpOption<double>.Some(availability);
        props.Performance = FSharpOption<double>.Some(performance);
        props.Quality = FSharpOption<double>.Some(quality);
        props.OEE = FSharpOption<double>.Some(SimulationHelpers.calculateOEE(availability, performance, quality));

        work.TokenRole = TokenRole.None;
        if (seed.IsSource)
            work.TokenRole |= TokenRole.Source;
        if (seed.IsSink)
            work.TokenRole |= TokenRole.Sink;

        CostSimStoreHelper.ApplyCalculatedTotals(work);
    }

    private static void FinalizeDemoFlow(DsStore store, Guid flowId)
    {
        var orderedWorks = CostSimStoreHelper.GetOrderedWorksInFlow(store, flowId).ToList();
        if (orderedWorks.Count == 0)
            return;

        if (!orderedWorks.Any(work => work.TokenRole.HasFlag(TokenRole.Source)))
            orderedWorks[0].TokenRole |= TokenRole.Source;

        if (!orderedWorks.Any(work => work.TokenRole.HasFlag(TokenRole.Sink)))
            orderedWorks[^1].TokenRole |= TokenRole.Sink;

        store.SyncOrderedWorkChainDirect(orderedWorks.Select(work => work.Id), ArrowType.Start);
    }

    private static string SanitizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
            else if (ch is ' ' or '-' or '_')
                builder.Append('-');
        }

        return builder.Length == 0 ? "demo" : builder.ToString().Trim('-');
    }

    private static IReadOnlyList<DemoDocumentSeed> BuildDemoLibrarySeeds()
        =>
        [
            new(
                "EV_BatteryPack_MainLine.json",
                "EV Battery Pack Main Line",
                "CostSim Demo Lab",
                "2026.04",
                [
                    new(
                        "Module Prep Cell",
                        "BatteryAssembly",
                        "배터리 셀 스테이징과 탭 용접 전처리를 포함한 모듈 준비 셀",
                        4101,
                        0.86,
                        [
                            new(
                                "Cell Staging",
                                "셀 트레이 로딩부터 전기적 선별, 버퍼 적재까지 처리",
                                "TreeOrder",
                                [
                                    new("ReceiveCellTray", "BP-101", 65, 2, 42000, 18000, 12000, 3800, 0.998, 0.002, SimSkillLevel.Intermediate, 8.0, 7.4, 480, 472, 470, 2, 1, "Load + Align", "셀 트레이를 투입하고 포지셔닝 기준면을 맞춘다.", true, false),
                                    new("BarcodeAndOCVCheck", "BP-102", 42, 1, 40000, 16000, 11000, 2500, 0.997, 0.003, SimSkillLevel.Advanced, 8.0, 7.2, 480, 472, 469, 3, 1, "Scan + Measure", "바코드 이력과 OCV를 동시에 검증해 선별한다.", false, false),
                                    new("TabSurfaceClean", "BP-103", 38, 1, 41000, 22000, 12500, 3100, 0.996, 0.004, SimSkillLevel.Advanced, 8.0, 7.0, 480, 470, 466, 4, 1, "Laser Clean", "탭 용접 전 표면 산화막을 제거한다.", false, false),
                                    new("ModuleBufferLoad", "BP-104", 54, 1, 39500, 17000, 11800, 2600, 0.998, 0.002, SimSkillLevel.Intermediate, 8.0, 7.5, 480, 470, 468, 2, 1, "Buffer Transfer", "정렬 완료된 셀을 모듈 버퍼로 이송한다.", false, true)
                                ]),
                            new(
                                "Tab Welding",
                                "탭 정렬, 레이저 용접, 비전 검사, 저항 샘플링 공정",
                                "TreeOrder",
                                [
                                    new("AlignTabPair", "BP-201", 58, 2, 43500, 25000, 13200, 4200, 0.995, 0.004, SimSkillLevel.Advanced, 8.0, 7.3, 420, 414, 410, 4, 2, "Precision Align", "양극/음극 탭 간 간격과 평행도를 맞춘다.", true, false),
                                    new("DualLaserWeld", "BP-202", 96, 2, 46000, 36000, 14800, 6900, 0.991, 0.007, SimSkillLevel.Expert, 8.0, 7.1, 420, 414, 407, 7, 3, "Dual Laser Weld", "양측 탭을 동시 용접해 전기적 연결을 형성한다.", false, false),
                                    new("WeldVisionInspect", "BP-203", 44, 1, 40500, 21500, 12200, 2900, 0.994, 0.005, SimSkillLevel.Advanced, 8.0, 7.0, 420, 414, 409, 5, 2, "Vision AOI", "비드 폭과 스패터를 비전으로 검증한다.", false, false),
                                    new("ResistanceSampling", "BP-204", 36, 1, 39800, 19000, 11600, 2400, 0.997, 0.003, SimSkillLevel.Advanced, 8.0, 7.4, 420, 413, 411, 2, 1, "Electrical Sample", "용접 저항 샘플을 측정해 공정 창을 확인한다.", false, true)
                                ])
                        ]),
                    new(
                        "Pack Final Cell",
                        "PackAssembly",
                        "모듈 적층부터 EOL 검사와 출하 승인까지 수행하는 최종 셀",
                        4102,
                        0.88,
                        [
                            new(
                                "Pack Stack",
                                "모듈 적층, 압착, 냉각 플레이트 체결 공정",
                                "TreeOrder",
                                [
                                    new("ModuleStacking", "BP-301", 72, 2, 44500, 23000, 13000, 4100, 0.996, 0.003, SimSkillLevel.Advanced, 8.0, 7.6, 180, 176, 174, 2, 1, "Robot Stack", "모듈을 패크 케이스 안에 적층 배치한다.", true, false),
                                    new("PackCompressionFit", "BP-302", 88, 2, 45200, 31500, 14500, 5300, 0.994, 0.004, SimSkillLevel.Advanced, 8.0, 7.3, 180, 176, 173, 3, 1, "Compression Fit", "패크 클램프와 볼트 체결로 적층 하중을 맞춘다.", false, false),
                                    new("CoolingPlateMount", "BP-303", 66, 1, 43000, 24800, 13600, 4600, 0.995, 0.003, SimSkillLevel.Intermediate, 8.0, 7.5, 180, 175, 173, 2, 1, "Plate Mount", "냉각 플레이트와 열전도 패드를 조립한다.", false, true)
                                ]),
                            new(
                                "EOL Audit",
                                "누설 검사, BMS 플래시, 출하 승인 공정",
                                "TreeOrder",
                                [
                                    new("PackLeakTest", "BP-401", 64, 1, 41800, 26500, 14100, 4300, 0.997, 0.002, SimSkillLevel.Advanced, 8.0, 7.2, 180, 174, 173, 1, 0, "Pressure Decay", "패크 누설량과 실링 상태를 점검한다.", true, false),
                                    new("BmsFlashAndTrace", "BP-402", 49, 1, 40800, 18500, 12000, 2100, 0.998, 0.001, SimSkillLevel.Intermediate, 8.0, 7.0, 180, 174, 174, 0, 0, "Flash + Trace", "BMS 펌웨어와 생산 이력 정보를 기록한다.", false, false),
                                    new("FinalRelease", "BP-403", 34, 1, 39200, 12000, 9800, 1500, 0.999, 0.001, SimSkillLevel.Intermediate, 8.0, 7.4, 180, 174, 174, 0, 0, "Release Gate", "최종 승인 후 출하 영역으로 이송한다.", false, true)
                                ])
                        ])
                ]),
            new(
                "DoorModule_TrimStation.json",
                "Door Module Trim Station",
                "CostSim Demo Lab",
                "2026.04",
                [
                    new(
                        "Door Trim Install Cell",
                        "DoorModule",
                        "도어 하니스, 래치, 스피커, 품질 게이트를 포함한 조립 셀",
                        4201,
                        0.84,
                        [
                            new(
                                "Harness Routing",
                                "도어 하니스 배선과 클립 체결 공정",
                                "TreeOrder",
                                [
                                    new("LoadInnerPanel", "DM-101", 48, 1, 37200, 9800, 8400, 900, 0.998, 0.002, SimSkillLevel.Intermediate, 8.0, 7.5, 520, 514, 512, 2, 1, "Panel Load", "내측 패널을 지그에 안착하고 기준점을 맞춘다.", true, false),
                                    new("RouteMainHarness", "DM-102", 76, 2, 38800, 11200, 9100, 1100, 0.994, 0.004, SimSkillLevel.Advanced, 8.0, 7.1, 520, 514, 509, 5, 2, "Manual Routing", "메인 하니스를 채널과 클립 포인트에 배선한다.", false, false),
                                    new("ClipTorqueVerify", "DM-103", 32, 1, 36500, 7600, 7900, 650, 0.997, 0.002, SimSkillLevel.Intermediate, 8.0, 7.4, 520, 513, 511, 2, 0, "Torque Verify", "클립 체결 상태와 조임력을 점검한다.", false, true)
                                ]),
                            new(
                                "Latch And Speaker Install",
                                "래치, 스피커, 실링 패드 조립 공정",
                                "TreeOrder",
                                [
                                    new("MountLatchBody", "DM-201", 54, 1, 37800, 10800, 8700, 800, 0.996, 0.003, SimSkillLevel.Intermediate, 8.0, 7.3, 500, 494, 492, 2, 1, "Latch Install", "래치 바디를 장착하고 프리토크를 준다.", true, false),
                                    new("SpeakerBracketInstall", "DM-202", 41, 1, 37100, 9200, 8200, 700, 0.997, 0.002, SimSkillLevel.Intermediate, 8.0, 7.6, 500, 494, 493, 1, 0, "Bracket Fit", "스피커 브래킷과 방진 패드를 장착한다.", false, false),
                                    new("DoorSealPress", "DM-203", 58, 2, 38600, 9500, 8800, 950, 0.995, 0.003, SimSkillLevel.Advanced, 8.0, 7.2, 500, 494, 491, 3, 1, "Seal Press", "실링 패드를 프레스 롤러로 균일하게 압착한다.", false, false),
                                    new("ElectricalFunctionCheck", "DM-204", 44, 1, 38000, 12500, 9100, 1200, 0.998, 0.002, SimSkillLevel.Advanced, 8.0, 7.0, 500, 493, 492, 1, 0, "Function Check", "스위치와 스피커 회로를 전기적으로 점검한다.", false, true)
                                ]),
                            new(
                                "Door Quality Gate",
                                "비전 외관, Gap/Flush 측정, 출하 승인 공정",
                                "TreeOrder",
                                [
                                    new("VisionAppearanceScan", "DM-301", 36, 1, 36800, 13800, 8600, 980, 0.998, 0.001, SimSkillLevel.Advanced, 8.0, 7.3, 500, 492, 492, 0, 0, "Vision Scan", "스크래치와 클립 누락을 비전으로 확인한다.", true, false),
                                    new("GapFlushMeasure", "DM-302", 47, 1, 37400, 14800, 8900, 1020, 0.997, 0.002, SimSkillLevel.Advanced, 8.0, 7.1, 500, 492, 491, 1, 0, "Gap/Flush", "도어 외곽 Gap과 Flush를 측정한다.", false, false),
                                    new("ShippingRelease", "DM-303", 22, 1, 35200, 6400, 7600, 400, 0.999, 0.001, SimSkillLevel.Intermediate, 8.0, 7.5, 500, 492, 492, 0, 0, "Release", "품질 게이트 통과 후 다음 공정으로 출하 승인한다.", false, true)
                                ])
                        ])
                ]),
            new(
                "SeatFrame_WeldingCell.json",
                "Seat Frame Welding Cell",
                "CostSim Demo Lab",
                "2026.04",
                [
                    new(
                        "Seat Frame Weld Line",
                        "SeatFrame",
                        "시트 프레임 택용접, 본용접, 치수 감사 공정",
                        4301,
                        0.82,
                        [
                            new(
                                "Side Frame Tack",
                                "측면 프레임 로드와 택 용접 공정",
                                "TreeOrder",
                                [
                                    new("LoadLeftRightFrame", "SF-101", 57, 2, 40200, 17400, 9800, 2600, 0.997, 0.002, SimSkillLevel.Intermediate, 8.0, 7.4, 360, 354, 353, 1, 1, "Robot Load", "좌우 프레임을 지그에 안착한다.", true, false),
                                    new("ClampAndTackWeld", "SF-102", 83, 2, 42800, 28600, 11800, 4800, 0.992, 0.006, SimSkillLevel.Advanced, 8.0, 7.0, 360, 354, 349, 5, 2, "Tack Weld", "변형을 억제하며 택 용접으로 고정한다.", false, false),
                                    new("TackQualityCheck", "SF-103", 31, 1, 38900, 11200, 9100, 1500, 0.996, 0.003, SimSkillLevel.Advanced, 8.0, 7.2, 360, 353, 351, 2, 0, "Visual Check", "초기 비드 형상과 누락점을 점검한다.", false, true)
                                ]),
                            new(
                                "Cross Member Weld",
                                "크로스 멤버 위치 결정과 본용접 공정",
                                "TreeOrder",
                                [
                                    new("InsertCrossMember", "SF-201", 49, 1, 39500, 16200, 9300, 1800, 0.997, 0.002, SimSkillLevel.Intermediate, 8.0, 7.5, 340, 335, 334, 1, 0, "Insert", "크로스 멤버를 기준 핀에 맞춰 삽입한다.", true, false),
                                    new("MainMigWeld", "SF-202", 102, 2, 43600, 33400, 12500, 5200, 0.991, 0.007, SimSkillLevel.Expert, 8.0, 6.9, 340, 335, 329, 6, 2, "MIG Weld", "주요 접합부를 본용접해 강성을 확보한다.", false, false),
                                    new("SpatterClean", "SF-203", 41, 1, 38200, 9800, 8700, 1400, 0.995, 0.003, SimSkillLevel.Intermediate, 8.0, 7.3, 340, 334, 332, 2, 1, "Spatter Clean", "스패터를 제거하고 재도장 면을 정리한다.", false, true)
                                ]),
                            new(
                                "Dimensional Audit",
                                "치수 측정과 강성 검증, 출하 승인 공정",
                                "TreeOrder",
                                [
                                    new("CmmDimensionScan", "SF-301", 62, 1, 40800, 19800, 10200, 1700, 0.998, 0.001, SimSkillLevel.Advanced, 8.0, 7.1, 340, 333, 333, 0, 0, "CMM Scan", "주요 기준 치수와 홀 위치를 측정한다.", true, false),
                                    new("RigiditySampleTest", "SF-302", 53, 1, 41400, 21400, 10800, 1900, 0.997, 0.002, SimSkillLevel.Advanced, 8.0, 7.0, 340, 333, 332, 1, 0, "Rigidity Test", "샘플 강성 시험으로 용접 품질을 검증한다.", false, false),
                                    new("ReleaseToPaint", "SF-303", 24, 1, 36000, 6200, 7600, 600, 0.999, 0.001, SimSkillLevel.Intermediate, 8.0, 7.5, 340, 333, 333, 0, 0, "Release", "도장 공정으로 이송 승인한다.", false, true)
                                ])
                        ])
                ])
        ];

    private readonly record struct DemoDocumentSeed(
        string FileName,
        string ProjectName,
        string Author,
        string Version,
        DemoSystemSeed[] Systems);

    private readonly record struct DemoSystemSeed(
        string Name,
        string SystemType,
        string Description,
        int RandomSeed,
        double TargetOee,
        DemoFlowSeed[] Flows);

    private readonly record struct DemoFlowSeed(
        string Name,
        string Description,
        string Mode,
        DemoWorkSeed[] Works);

    private readonly record struct DemoWorkSeed(
        string Name,
        string OperationCode,
        double DurationSeconds,
        int WorkerCount,
        double LaborCostPerHour,
        double EquipmentCostPerHour,
        double OverheadCostPerHour,
        double UtilityCostPerHour,
        double YieldRate,
        double DefectRate,
        SimSkillLevel SkillLevel,
        double PlannedOperatingHours,
        double ActualOperatingHours,
        int PlannedProductionQty,
        int ActualProductionQty,
        int GoodProductQty,
        int DefectQty,
        int ReworkQty,
        string Motion,
        string Description,
        bool IsSource,
        bool IsSink);
}
