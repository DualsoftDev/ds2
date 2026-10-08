using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Runtime.Engine;

namespace Ds2.Wasm;

/// <summary>
/// VS Code 확장(호스트)이 부르는 유일한 표면.
///
/// 설계 규칙 하나: <b>모델을 경계 너머로 실어 나르지 않는다.</b>
/// store 와 엔진은 WASM 안에 남고 JS 는 정수 핸들만 쥔다 —
/// 48 Work 모델을 16ms 마다 JSON 으로 왕복시키면 그게 곧 병목이 된다.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("browser")]
public static partial class Interop
{
    private static readonly Dictionary<int, DsStore> Stores = [];
    private static readonly Dictionary<int, SimSession> Sims = [];
    private static int _nextId = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ─────────────────────────────────────────────────────────── 모델

    /// <summary>.sdf(gzip JSON) 바이트를 읽어 store 핸들을 돌려준다.</summary>
    [JSExport]
    internal static int LoadSdf(byte[] sdf)
    {
        using var input = new MemoryStream(sdf);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        gz.CopyTo(plain);
        var loaded = Ds2.Serialization.JsonConverter.deserialize<DsStore>(
            Encoding.UTF8.GetString(plain.ToArray()));
        if (loaded is null) throw new InvalidDataException("Loaded store is null.");

        // Use the same ApplyNewStore pipeline as DsStore.LoadFromFile, without a
        // temporary browser file. Raw deserialization deliberately omits the JsonIgnore
        // ApiCalls index. Without rebuilding it a Call loses its Tx/Rx targets and the
        // runtime's legacy empty-target completion path may finish immediately.
        // ReplaceStore also rewires references, migrates names/SystemType/AID and bumps
        // the revision; do not reproduce only one of those load invariants here.
        var store = new DsStore();
        store.ReplaceStore(loaded);

        var id = _nextId++;
        Stores[id] = store;
        return id;
    }

    /// <summary>Read-only membership check against one Call-body ApiCall InputSpec.
    /// This does not evaluate contact/edge timing, permit state, or missing inputs.</summary>
    [JSExport]
    internal static bool EvaluateApiCallInput(int handle, string apiCallId, string rawValue)
    {
        ArgumentNullException.ThrowIfNull(rawValue);
        if (rawValue.Length > 1048576)
            throw new ArgumentException("Input value exceeds the observation limit.", nameof(rawValue));
        var store = Require(handle);
        if (!Guid.TryParseExact(apiCallId, "D", out var id) || id == Guid.Empty
            || !store.ApiCalls.TryGetValue(id, out var apiCall))
            throw new ArgumentException("Call-body ApiCall identity was not found.", nameof(apiCallId));
        return ValueSpecModule.evaluate(apiCall.InputSpec, rawValue);
    }

    /// <summary>store 를 .sdf 바이트로 직렬화한다 (컴파일 결과 저장용).</summary>
    [JSExport]
    internal static byte[] SaveSdf(int handle)
    {
        var store = Require(handle);
        var temp = Path.Combine(Path.GetTempPath(), $"ds2_{Guid.NewGuid():N}.sdf");
        try
        {
            store.SaveToFile(temp);
            return File.ReadAllBytes(temp);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* 임시 파일 정리 실패는 무시 */ }
        }
    }

    /// <summary>store 를 닫는다. 열려 있던 시뮬도 함께 정리한다.</summary>
    [JSExport]
    internal static void CloseModel(int handle)
    {
        foreach (var simId in Sims.Where(kv => kv.Value.StoreHandle == handle).Select(kv => kv.Key).ToArray())
            SimClose(simId);
        Stores.Remove(handle);
    }

    /// <summary>정규 DS2 Text v4 원문.</summary>
    [JSExport]
    internal static string BuildDs2Text(int handle)
    {
        var store = Require(handle);
        return Ds2.Text.Ds2TextWriter.write(store, FirstProjectId(store));
    }

    /// <summary>Declaration identity-preserving DS2 Text; metadata bundle is separate.</summary>
    [JSExport]
    internal static string BuildDs2TextWithIdentity(int handle)
    {
        var store = Require(handle);
        return Ds2.Text.Ds2TextWriter.writeWithOptions(store, FirstProjectId(store), new Ds2.Text.Ds2TextOptions(true));
    }

    /// <summary>모델 요약 — 확장이 상태 표시줄·제목에 쓴다.</summary>
    [JSExport]
    internal static string ModelSummary(int handle)
    {
        var store = Require(handle);
        var projectId = FirstProjectId(store);
        var project = store.Projects[projectId];
        return JsonSerializer.Serialize(new
        {
            name = project.Name,
            version = project.Version,
            systems = store.Systems.Count,
            flows = store.Flows.Count,
            works = store.Works.Count,
            calls = store.Calls.Count,
            apiDefs = store.ApiDefs.Count,
            apiCalls = store.ApiCalls.Count,
        }, Json);
    }

    // ─────────────────────────────────────────────────────────── 시뮬레이션

    /// <summary>
    /// 시뮬 세션을 연다. 엔진은 여기서 만들어지고 <see cref="SimStart"/> 전까지 멈춰 있다.
    /// </summary>
    [JSExport]
    internal static int SimOpen(int handle)
    {
        var store = Require(handle);
        var session = SimSession.Create(handle, store);
        var id = _nextId++;
        Sims[id] = session;
        return id;
    }

    /// <summary>Open with an immutable, explicit simulation execution contract.
    /// Existing SimOpen retains legacy startup/recovery behavior. Unknown fields,
    /// duplicate fields and unsupported pairs fail before allocating a session.</summary>
    [JSExport]
    internal static int SimOpenWithOptions(int handle, string optionsJson)
    {
        var store = Require(handle);
        using var document = JsonDocument.Parse(optionsJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Simulation options must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
        {
            if (!names.Add(field.Name) || (field.Name != "version" && field.Name != "initialization" && field.Name != "rearming")
                || field.Value.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Simulation options require exact, unique string fields.");
        }
        if (names.Count != 3 || root.GetProperty("version").GetString() != "ds2-sim-options/v1")
            throw new ArgumentException("Expected ds2-sim-options/v1 with initialization and rearming.");
        var initialization = root.GetProperty("initialization").GetString();
        var rearming = root.GetProperty("rearming").GetString();
        var options = (initialization, rearming) switch
        {
            ("model-only", "reset-edges-only") => SimulationExecutionOptions.Explicit,
            ("legacy-auto-homing", "legacy-device-recovery") => SimulationExecutionOptions.Legacy,
            _ => throw new ArgumentException("Unsupported simulation initialization/rearming pair."),
        };
        var session = SimSession.Create(handle, store, options);
        var id = _nextId++;
        Sims[id] = session;
        return id;
    }

    [JSExport]
    internal static string SimOptions(int simId) => RequireSim(simId).ReadOptions();

    /// <summary>
    /// 시작 절차를 <b>한 덩어리로</b> 실행한다 — 호출자가 순서를 틀릴 수 없게 봉인한 것이다.
    /// 이 순서가 아니면 아무것도 돌지 않는다 (Promaker Runner.Start.cs / ForceWork.cs 와 동일).
    /// 돌려주는 것은 전체 스냅샷.
    /// </summary>
    [JSExport]
    internal static string SimStart(int simId) => RequireSim(simId).Start();

    /// <summary>논리 시간을 targetMs 까지 전진시키고 <b>바뀐 것만</b> 돌려준다.</summary>
    [JSExport]
    internal static string SimAdvanceTo(int simId, double targetMs) => RequireSim(simId).AdvanceTo((long)targetMs);

    /// <summary>전체 스냅샷을 다시 받는다 (탭이 다시 보일 때).</summary>
    [JSExport]
    internal static string SimSnapshot(int simId) => RequireSim(simId).Snapshot();

    [JSExport]
    internal static void SimReset(int simId) => RequireSim(simId).Reset();

    /// <summary>Read current Work result bits and ApiCall input presence/raw
    /// values without evaluating conditions, synthesizing defaults or draining events.</summary>
    [JSExport]
    internal static string SimReadObservations(int simId) => RequireSim(simId).ReadObservations();

    /// <summary>V2 adds explicit engine-synthesis provenance; not sensor authentication.</summary>
    [JSExport]
    internal static string SimReadObservationsWithOrigins(int simId) => RequireSim(simId).ReadObservationsWithOrigins();

    /// <summary>
    /// Read the bounded Work/Call engine event stream without consuming it.
    /// ds2-sim-events/v1 records distinguish observations, transitions and session boundaries.
    /// Overflow/drop counters must be checked before claiming a complete transition trace.
    /// Snapshot/Delta do not consume these records and their existing JSON is unchanged.
    /// </summary>
    [JSExport]
    internal static string SimReadEvents(int simId) => RequireSim(simId).ReadEvents();

    /// <summary>Return queued events and drain them. Overflow counters remain visible.</summary>
    [JSExport]
    internal static string SimDrainEvents(int simId) => RequireSim(simId).ReadEvents(drain: true);

    /// <summary>
    /// Reset only event capture (capacity 1..262144 records), not simulation state or clock.
    /// Adds a capture-reset boundary and current observations; counts discarded pending data.
    /// Sequence numbers continue across this call and SimReset. SimReset begins a new epoch.
    /// </summary>
    [JSExport]
    internal static string SimResetEvents(int simId, int capacity) => RequireSim(simId).ResetEvents(capacity);

    [JSExport]
    internal static void SimClose(int simId)
    {
        if (Sims.Remove(simId, out var session)) session.Dispose();
    }

    // ─────────────────────────────────────────────────────────── 진단

    /// <summary>엔진이 드라이버 스레드 없이 도는가 (브라우저면 true). 호스트가 펌프를 걸어야 한다.</summary>
    [JSExport]
    internal static bool IsHostDrivenLoop(int simId) => RequireSim(simId).IsHostDriven;

    [JSExport]
    internal static string RuntimeInfo() => JsonSerializer.Serialize(new
    {
        core = typeof(DsStore).Assembly.GetName().Version?.ToString(),
        isBrowser = OperatingSystem.IsBrowser(),
        models = Stores.Count,
        sims = Sims.Count,
    }, Json);

    // ─────────────────────────────────────────────────────────── 내부

    private static DsStore Require(int handle) =>
        Stores.TryGetValue(handle, out var s) ? s : throw new ArgumentException($"알 수 없는 모델 핸들: {handle}");

    private static SimSession RequireSim(int simId) =>
        Sims.TryGetValue(simId, out var s) ? s : throw new ArgumentException($"알 수 없는 시뮬 핸들: {simId}");

    private static Guid FirstProjectId(DsStore store) =>
        store.Projects.Keys.FirstOrDefault() is var id && id != Guid.Empty
            ? id
            : throw new InvalidOperationException("모델에 프로젝트가 없습니다.");
}
