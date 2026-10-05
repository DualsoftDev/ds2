using Ds2.Aasx;
using Ds2.CSV;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Mermaid;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Engine.Core;
using Ds2.Runtime.Report;
using Ds2.Runtime.Report.Model;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var samplePath = Path.Combine(AppContext.BaseDirectory, "sample-line.mmd");
var outputDir = Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outputDir);

const string CsvSample =
    """
    Flow,Work,Device,System,Api,InName,InAddress,OutName,OutAddress
    Assembly,PickPart,Robot,Line,Pick,PickDone,%IX0.0,PickStart,%QX0.0
    Assembly,WeldJoint,Welder,Line,Start,WeldDone,%IX0.1,WeldStart,%QX0.1
    Assembly,PlacePart,Robot,Line,Place,PlaceDone,%IX0.2,PlaceStart,%QX0.2
    """;

Console.WriteLine("DualSoft-DS2 C# Tutorial");
Console.WriteLine("========================");
Console.WriteLine();

var store = LoadMermaid(samplePath);
PrintStore("Mermaid import", store);

RunCoreJsonRoundTrip(store, outputDir);
RunCsvSample(outputDir);
RunAasxSample(store, outputDir);
RunRuntimeSample(store, outputDir);

Console.WriteLine();
Console.WriteLine($"Output: {outputDir}");

static DsStore LoadMermaid(string filePath)
{
    var result = MermaidImporter.loadProjectFromFile(filePath);
    return RequireOk(result, "Mermaid import");
}

static void RunCoreJsonRoundTrip(DsStore store, string outputDir)
{
    var jsonPath = Path.Combine(outputDir, "sample-line.json");
    store.SaveToFile(jsonPath);

    var loaded = DsStore.empty();
    loaded.LoadFromFile(jsonPath);

    PrintFile("Core JSON save/load", jsonPath);
    PrintStore("JSON reload", loaded);
    Console.WriteLine();
}

static void RunCsvSample(string outputDir)
{
    var document = RequireOk(CsvImporter.parseContent(CsvSample), "CSV parse");
    var csvStore = RequireOk(CsvImporter.loadProject(document, "CsvSample", "Controller"), "CSV import");
    PrintStore("CSV import", csvStore);

    var bytes = PlcAasxFacade.ExportDs2CsvToAasxBytes(
        "CsvFacadeSample",
        CsvSample,
        "https://dualsoft.example/ds2/");

    if (bytes is { Length: > 0 })
    {
        var path = Path.Combine(outputDir, "csv-facade.aasx");
        File.WriteAllBytes(path, bytes);
        PrintFile("PLC CSV -> AASX facade", path);
    }

    Console.WriteLine();
}

static void RunAasxSample(DsStore store, string outputDir)
{
    var aasxPath = Path.Combine(outputDir, "sample-line.aasx");
    var exported = AasxExporter.exportFromStore(
        store,
        aasxPath,
        "https://dualsoft.example/ds2/",
        splitDeviceAasx: false,
        autoCreateEmptySubmodels: false);

    Console.WriteLine($"[AASX export] {(exported ? "OK" : "SKIPPED")}");
    if (exported)
    {
        PrintFile("AASX file", aasxPath);
    }

    Console.WriteLine();
}

static void RunRuntimeSample(DsStore store, string outputDir)
{
    var sourceWork = ConfigureRuntimeModel(store);
    var index = SimIndexModule.build(store, tickMs: 50);

    Console.WriteLine("[Runtime SimIndex]");
    Console.WriteLine($"  Works={index.AllWorkGuids.Length}, Calls={index.AllCallGuids.Length}, Tick={index.TickMs}ms");
    Console.WriteLine($"  TokenSources={index.TokenSourceGuids.Length}, TokenSinks={index.TokenSinkGuids.Count}");
    Console.WriteLine();

    PrintGraphValidation(index);

    var records = new List<StateChangeRecord>();
    var startTime = DateTime.Now;

    using var engine = new EventDrivenEngine(index, RuntimeMode.Simulation);
    var sim = (ISimulationEngine)engine;

    sim.WorkStateChanged += (_, e) =>
    {
        records.Add(new StateChangeRecord(
            nodeId: e.WorkGuid.ToString(),
            nodeName: e.WorkName,
            nodeType: "Work",
            systemId: "",
            state: StatusCode(e.NewState),
            timestamp: startTime + e.Clock,
            tokenItem: FSharpOption<int>.None,
            tokenOriginName: ""));

        if (records.Count <= 12)
        {
            Console.WriteLine($"  [{e.Clock:mm\\:ss\\.fff}] {e.WorkName}: {e.PreviousState} -> {e.NewState}");
        }
    };

    sim.TokenEvent += (_, e) =>
    {
        var target = e.TargetWorkName != null ? $" -> {e.TargetWorkName.Value}" : "";
        Console.WriteLine($"  [{e.Clock:mm\\:ss\\.fff}] token {e.Kind}: {e.WorkName}{target}");
    };

    Console.WriteLine("[Runtime run]");
    sim.SpeedMultiplier = 50.0;
    sim.TimeIgnore = true;

    try
    {
        sim.Start();
        sim.StartSourceWork(sourceWork.Id);
        Thread.Sleep(1000);
    }
    finally
    {
        sim.Stop();
    }

    var endTime = DateTime.Now;
    Console.WriteLine($"  State changes={records.Count}, CompletedTokens={sim.State.CompletedTokens.Length}");
    Console.WriteLine();

    var report = ReportService.fromStateChanges(startTime, endTime, records);
    var htmlPath = Path.Combine(outputDir, "runtime-report.html");
    var csvPath = Path.Combine(outputDir, "runtime-report.csv");

    ReportService.exportAuto(report, htmlPath);
    var csvOptions = ExportOptionsModule.defaults(ExportFormat.Csv, csvPath);
    ReportService.export(report, csvOptions);

    Console.WriteLine("[Runtime report]");
    Console.WriteLine($"  Entries={report.Entries.Length}, Duration={report.Metadata.TotalDuration}");
    PrintFile("HTML report", htmlPath);
    PrintFile("CSV report", csvPath);
}

static Work ConfigureRuntimeModel(DsStore store)
{
    foreach (var work in store.Works.Values)
    {
        work.Duration = TimeSpan.FromMilliseconds(250);
    }

    var source = FindWork(store, "PickPart");
    var sink = FindWork(store, "PlacePart");
    source.TokenRole = TokenRole.Source;
    sink.TokenRole = TokenRole.Sink;
    return source;
}

static Work FindWork(DsStore store, string localName)
{
    return store.Works.Values.FirstOrDefault(work =>
               string.Equals(work.LocalName, localName, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidOperationException($"Work not found: {localName}");
}

static void PrintGraphValidation(SimIndex index)
{
    Console.WriteLine("[GraphValidator]");
    Console.WriteLine($"  unresetWorks={GraphValidator.findUnresetWorks(index).Length}");
    Console.WriteLine($"  deadlockCandidates={GraphValidator.findDeadlockCandidates(index).Length}");
    Console.WriteLine($"  sourceCandidates={GraphValidator.findSourceCandidates(index).Length}");
    Console.WriteLine($"  sourcesWithPredecessors={GraphValidator.findSourcesWithPredecessors(index).Length}");
    Console.WriteLine($"  groupWithoutIgnore={GraphValidator.findGroupWorksWithoutIgnore(index).Length}");
    Console.WriteLine($"  tokenUnreachable={GraphValidator.findTokenUnreachableWorks(index).Length}");
    Console.WriteLine();
}

static T RequireOk<T>(FSharpResult<T, FSharpList<string>> result, string operation)
{
    if (result.IsOk)
    {
        return result.ResultValue;
    }

    var errors = string.Join(Environment.NewLine, result.ErrorValue);
    throw new InvalidOperationException($"{operation} failed:{Environment.NewLine}{errors}");
}

static string StatusCode(Status4 status) =>
    status switch
    {
        Status4.Ready => "R",
        Status4.Going => "G",
        Status4.Finish => "F",
        Status4.Homing => "H",
        _ => status.ToString()
    };

static void PrintStore(string label, DsStore store)
{
    Console.WriteLine($"[{label}] Projects={store.Projects.Count}, Systems={store.Systems.Count}, Flows={store.Flows.Count}, Works={store.Works.Count}, Calls={store.Calls.Count}, ApiDefs={store.ApiDefs.Count}");
}

static void PrintFile(string label, string path)
{
    var info = new FileInfo(path);
    Console.WriteLine($"  {label}: {info.Length:N0} bytes ({info.Name})");
}
