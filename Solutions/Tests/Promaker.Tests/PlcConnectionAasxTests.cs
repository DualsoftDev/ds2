using System;
using System.IO;
using System.Linq;
using Ds2.Core.Store;
using Ds2.Editor;
using PromakerShared = Promaker.Shared;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// 프로젝트 파일(AASX/.sdf)에 실리는 PLC 접속 정보의 계약 검증.
///
/// 핵심 회귀 방지 대상:
/// ① 구 파일(접속 미설정)이 현장 로컬 설정을 덮어쓰지 않을 것,
/// ② 벤더 파싱 실패가 조용히 다른 벤더로 폴백되지 않을 것,
/// ③ 접속 정보 적용이 운영 튜닝값(스캔 주기 등)을 되돌리지 않을 것.
/// </summary>
public sealed class PlcConnectionAasxTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Promaker.Tests", nameof(PlcConnectionAasxTests), Guid.NewGuid().ToString("N"));

    private string SidecarPath
    {
        get
        {
            Directory.CreateDirectory(_root);
            return Path.Combine(_root, "PlcConnection.json");
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 임시 폴더 정리 실패는 테스트 결과와 무관 */ }
    }

    /// <summary>Project + ActiveSystem 을 가진 최소 store — ControlSystemProperties 를 담을 그릇.</summary>
    private static DsStore NewStoreWithProject()
    {
        var store = DsStore.empty();
        store.AddProject("TestProject");
        var projectId = Queries.allProjects(store).Head.Id;
        store.AddSystem("TestSystem", projectId, isActive: true);
        return store;
    }

    /// <summary>첫 Project 의 첫 ActiveSystem Id — 접속 정보와 UserTag 가 붙는 대상.</summary>
    private static Guid PrimarySystemId(DsStore store)
    {
        var projectId = Queries.allProjects(store).Head.Id;
        return Queries.activeSystemsOf(projectId, store).Head.Id;
    }

    private static PromakerShared.PlcConnectionSettings Sidecar(
        PromakerShared.PlcVendorChoice vendor, string ip, int port, int scanIntervalMs = 250,
        bool isUdp = false, byte networkNumber = 0, byte stationNumber = 255,
        bool localEthernet = true, int timeoutMs = 3000)
    {
        var s = new PromakerShared.PlcConnectionSettings
        {
            Vendor = vendor.ToString(),
            IpAddress = ip,
            Port = port,
            ScanIntervalMs = scanIntervalMs,
            IsUdp = isUdp,
            NetworkNumber = networkNumber,
            StationNumber = stationNumber,
            LocalEthernet = localEthernet,
            TimeoutMs = timeoutMs,
            WasPersisted = true,   // 파일에서 로드된 설정 = 이 PC 가 확정한 값
        };
        s.EnsureProfiles();
        return s;
    }

    /// <summary>버전 1(전 필드) 커넥션 — Apply/Matches 단위 검증용.</summary>
    private static PromakerShared.AasxPlcConnection FullConn(
        PromakerShared.PlcVendorChoice vendor, string ip, int port,
        bool isUdp = false, int network = 0, int station = 255,
        bool localEthernet = true, int timeoutMs = 3000)
        => new(vendor, ip, port, isUdp, network, station, localEthernet, timeoutMs,
               PromakerShared.PlcConnectionResolver.CurrentProfileVersion);

    // ─────────────────────────────────────────────────────────────────────────
    // 미설정 판정 — 구 파일 회귀 방지의 핵심
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsUnset_true_for_exact_constructor_defaults()
    {
        Assert.True(PromakerShared.PlcConnectionResolver.IsUnset(
            PromakerShared.PlcConnectionResolver.UnsetIpAddress,
            PromakerShared.PlcConnectionResolver.UnsetVendor,
            PromakerShared.PlcConnectionResolver.UnsetPort));
    }

    [Fact]
    public void IsUnset_true_for_blank_ip()
    {
        Assert.True(PromakerShared.PlcConnectionResolver.IsUnset("", "LsXgk", 2004));
        Assert.True(PromakerShared.PlcConnectionResolver.IsUnset("   ", "LsXgk", 2004));
    }

    /// <summary>기본 IP 라도 포트/벤더가 실제 설정 경로의 값이면 "사용자 지정" 으로 본다.
    /// 포트 5000 은 LS 2004 / MX 5007 어느 쪽도 아니라 판별 가능.</summary>
    [Theory]
    [InlineData("192.168.0.1", "Mitsubishi", 5007)]   // 포트가 다름
    [InlineData("192.168.0.1", "LsXgk", 5000)]        // 벤더가 다름
    [InlineData("192.168.9.100", "Mitsubishi", 5000)] // IP 가 다름
    public void IsUnset_false_when_any_field_differs_from_default(string ip, string vendor, int port)
    {
        Assert.False(PromakerShared.PlcConnectionResolver.IsUnset(ip, vendor, port));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // store 왕복
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryReadFromStore_returns_null_when_no_control_properties()
    {
        Assert.Null(PromakerShared.PlcConnectionResolver.TryReadFromStore(NewStoreWithProject()));
    }

    [Fact]
    public void TryReadFromStore_returns_null_for_untouched_default_properties()
    {
        var store = NewStoreWithProject();
        // ControlSystemProperties 를 만들기만 하고 접속은 지정하지 않은 상태
        // = 지금까지 export 된 모든 구 파일의 모습.
        Queries.getOrCreatePrimaryControlProps(store);

        Assert.Null(PromakerShared.PlcConnectionResolver.TryReadFromStore(store));
    }

    [Fact]
    public void Stamp_then_read_roundtrips()
    {
        var store = NewStoreWithProject();
        var stamped = PromakerShared.PlcConnectionResolver.StampToStore(
            store, Sidecar(PromakerShared.PlcVendorChoice.LsXgk, "192.168.9.100", 2004));

        Assert.True(stamped);

        var read = PromakerShared.PlcConnectionResolver.TryReadFromStore(store);
        Assert.NotNull(read);
        Assert.Equal(PromakerShared.PlcVendorChoice.LsXgk, read!.Vendor);
        Assert.Equal("192.168.9.100", read.IpAddress);
        Assert.Equal(2004, read.Port);
    }

    [Fact]
    public void ClearStampedConnection_clears_what_promaker_wrote()
    {
        var store = NewStoreWithProject();
        PromakerShared.PlcConnectionResolver.StampToStore(
            store, Sidecar(PromakerShared.PlcVendorChoice.Mitsubishi, "10.0.0.5", 5007));
        Assert.NotNull(PromakerShared.PlcConnectionResolver.TryReadFromStore(store));

        Assert.True(PromakerShared.PlcConnectionResolver.ClearStampedConnection(store));
        Assert.Null(PromakerShared.PlcConnectionResolver.TryReadFromStore(store));
    }

    /// <summary>손편집(AasxEditor)이나 외부 도구가 넣은 값(PlcProfileVersion=0)은 저장 시 지우지 않는다 —
    /// "AASX 에 접속 정보 포함" 옵트아웃이 남의 데이터를 조용히 파괴하면 안 된다.</summary>
    [Fact]
    public void ClearStampedConnection_preserves_hand_edited_values()
    {
        var store = NewStoreWithProject();
        var cp = Queries.getOrCreatePrimaryControlProps(store).Value;
        cp.PlcVendor = nameof(PromakerShared.PlcVendorChoice.LsXgk);
        cp.PlcIpAddress = "192.168.9.100";
        cp.PlcPort = 2004;
        cp.PlcProfileVersion = 0;      // 손편집 — Promaker 가 쓴 값이 아님

        Assert.False(PromakerShared.PlcConnectionResolver.ClearStampedConnection(store));

        var conn = PromakerShared.PlcConnectionResolver.TryReadFromStore(store);
        Assert.NotNull(conn);
        Assert.Equal("192.168.9.100", conn!.IpAddress);
    }

    [Fact]
    public void StampToStore_returns_false_when_no_project()
    {
        Assert.False(PromakerShared.PlcConnectionResolver.StampToStore(
            DsStore.empty(), Sidecar(PromakerShared.PlcVendorChoice.LsXgi, "10.0.0.9", 2004)));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 벤더별 접속 파라미터 (전송방식 / 국번 / LocalEthernet / Timeout)
    // IP 만 맞고 이 값들이 틀리면 접속은 실패한다 — 왕복 보존이 핵심.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Mitsubishi_udp_and_station_survive_real_aasx_roundtrip()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "mx-udp.aasx");

        var store = NewStoreWithProject();
        PromakerShared.PlcConnectionResolver.StampToStore(
            store, Sidecar(PromakerShared.PlcVendorChoice.Mitsubishi, "10.20.30.40", 5007,
                           isUdp: true, networkNumber: 3, stationNumber: 12, timeoutMs: 7000));

        Assert.True(Ds2.Aasx.AasxExporter.exportFromStore(
            store, path, "https://dualsoft.com/", false, false));

        var reloaded = DsStore.empty();
        Ds2.Aasx.AasxImporter.importIntoStore(reloaded, path);

        var conn = PromakerShared.PlcConnectionResolver.TryReadFromStore(reloaded);
        Assert.NotNull(conn);
        Assert.True(conn!.HasVendorParams);
        Assert.True(conn.IsUdp);
        Assert.Equal(3, conn.NetworkNumber);
        Assert.Equal(12, conn.StationNumber);
        Assert.Equal(7000, conn.TimeoutMs);
    }

    /// <summary>실측으로 확인된 회귀 지점 — 미쓰비시를 쓴 적 없는 PC 에서 열어도
    /// UDP·국번이 그 PC 의 기본값(TCP/0/255)으로 떨어지지 않아야 한다.</summary>
    [Fact]
    public void Mitsubishi_udp_reaches_gateway_on_a_machine_that_never_used_mitsubishi()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "mx-gateway.aasx");

        var authoring = NewStoreWithProject();
        var authoringSys = PrimarySystemId(authoring);
        authoring.AddUserTag(authoringSys, "T1", "Info", "D100", "Int16", "", "");
        PromakerShared.PlcConnectionResolver.StampToStore(
            authoring, Sidecar(PromakerShared.PlcVendorChoice.Mitsubishi, "10.20.30.40", 5007,
                               isUdp: true, networkNumber: 3, stationNumber: 12));
        Assert.True(Ds2.Aasx.AasxExporter.exportFromStore(
            authoring, path, "https://dualsoft.com/", false, false));

        // PC-B: LS 만 써온 머신 (미쓰비시 프로파일은 기본값)
        var store = DsStore.empty();
        Ds2.Aasx.AasxImporter.importIntoStore(store, path);
        var pcB = Sidecar(PromakerShared.PlcVendorChoice.LsXgi, "192.168.0.10", 2004);

        var conn = PromakerShared.PlcConnectionResolver.TryReadFromStore(store);
        Assert.NotNull(conn);
        PromakerShared.PlcConnectionResolver.ApplyToSettings(pcB, conn!);

        Assert.Equal(nameof(PromakerShared.PlcVendorChoice.Mitsubishi), pcB.Vendor);
        Assert.True(pcB.IsUdp);
        Assert.Equal(3, pcB.NetworkNumber);
        Assert.Equal(12, pcB.StationNumber);

        var ioMap = Ds2.Runtime.IO.SignalIOMapModule.build(store);
        var tags = store.GetAllUserTagsForProject().Select(r => r.TagAddress).ToList();
        var gateway = PromakerShared.PlcGatewayConfigBuilder.TryBuild(pcB, ioMap, out var errors, tags);

        Assert.Empty(errors);
        Assert.NotNull(gateway);
        var c = gateway!.Connections.Head;
        Assert.Equal(Ds2.Backend.Plc.PlcTransport.Udp, c.Transport);
        Assert.Equal(3, c.NetworkNumber);
        Assert.Equal(12, c.StationNumber);
    }

    [Fact]
    public void Ls_local_ethernet_survives_roundtrip()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "ls-remote.aasx");

        var store = NewStoreWithProject();
        PromakerShared.PlcConnectionResolver.StampToStore(
            store, Sidecar(PromakerShared.PlcVendorChoice.LsXgk, "192.168.9.100", 2004,
                           localEthernet: false));
        Assert.True(Ds2.Aasx.AasxExporter.exportFromStore(
            store, path, "https://dualsoft.com/", false, false));

        var reloaded = DsStore.empty();
        Ds2.Aasx.AasxImporter.importIntoStore(reloaded, path);

        var conn = PromakerShared.PlcConnectionResolver.TryReadFromStore(reloaded);
        Assert.NotNull(conn);
        Assert.False(conn!.LocalEthernet);
    }

    /// <summary>버전 게이트 — 벤더/IP/포트만 기록된 파일(PlcProfileVersion=0)은 전송방식·국번을
    /// 덮지 않는다. 이 게이트가 없으면 이전 빌드가 만든 AASX 가 미쓰비시 UDP 현장을 TCP 로 되돌린다.</summary>
    [Fact]
    public void Profile_version_zero_does_not_overwrite_local_vendor_params()
    {
        var store = NewStoreWithProject();
        var cp = Queries.getOrCreatePrimaryControlProps(store).Value;
        cp.PlcVendor = nameof(PromakerShared.PlcVendorChoice.Mitsubishi);
        cp.PlcIpAddress = "10.20.30.40";
        cp.PlcPort = 5007;
        cp.PlcProfileVersion = 0;      // 구버전 기록 형식

        var conn = PromakerShared.PlcConnectionResolver.TryReadFromStore(store);
        Assert.NotNull(conn);
        Assert.False(conn!.HasVendorParams);

        // 이 PC 는 UDP·국번 12 로 이미 맞춰져 있다 — 파일이 이걸 덮으면 안 된다.
        var local = Sidecar(PromakerShared.PlcVendorChoice.Mitsubishi, "192.168.1.1", 5007,
                            isUdp: true, networkNumber: 3, stationNumber: 12);
        PromakerShared.PlcConnectionResolver.ApplyToSettings(local, conn);

        Assert.Equal("10.20.30.40", local.IpAddress);   // IP 는 적용
        Assert.True(local.IsUdp);                       // 전송방식은 보존
        Assert.Equal(3, local.NetworkNumber);
        Assert.Equal(12, local.StationNumber);
    }

    [Theory]
    [InlineData(300, 12)]
    [InlineData(3, 300)]
    [InlineData(-1, 12)]
    public void Out_of_range_station_rejects_the_whole_connection(int network, int station)
    {
        var store = NewStoreWithProject();
        var cp = Queries.getOrCreatePrimaryControlProps(store).Value;
        cp.PlcVendor = nameof(PromakerShared.PlcVendorChoice.Mitsubishi);
        cp.PlcIpAddress = "10.20.30.40";
        cp.PlcPort = 5007;
        cp.PlcProfileVersion = PromakerShared.PlcConnectionResolver.CurrentProfileVersion;
        cp.PlcNetworkNumber = network;
        cp.PlcStationNumber = station;

        Assert.Null(PromakerShared.PlcConnectionResolver.TryReadFromStore(store));
    }

    [Fact]
    public void Matches_detects_vendor_param_difference()
    {
        var settings = Sidecar(PromakerShared.PlcVendorChoice.Mitsubishi, "10.20.30.40", 5007,
                               isUdp: false, networkNumber: 0, stationNumber: 255);

        Assert.True(PromakerShared.PlcConnectionResolver.Matches(
            settings, FullConn(PromakerShared.PlcVendorChoice.Mitsubishi, "10.20.30.40", 5007)));

        // IP/포트는 같지만 전송방식이 다르면 "동일" 이 아니다 — 재적용이 필요하다.
        Assert.False(PromakerShared.PlcConnectionResolver.Matches(
            settings, FullConn(PromakerShared.PlcVendorChoice.Mitsubishi, "10.20.30.40", 5007, isUdp: true)));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 잘못된 값은 폴백하지 않고 기각 — 오접속 방지
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Unknown_vendor_rejects_the_whole_connection_instead_of_falling_back()
    {
        var store = NewStoreWithProject();
        var cp = Queries.getOrCreatePrimaryControlProps(store).Value;
        cp.PlcVendor = "Siemens";           // 지원 목록에 없음
        cp.PlcIpAddress = "192.168.9.100";
        cp.PlcPort = 2004;

        // 폴백(LsXgi)했다면 미쓰비시 현장이 LS 프로토콜로 붙어 원인 불명 실패가 난다.
        Assert.Null(PromakerShared.PlcConnectionResolver.TryReadFromStore(store));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("192.168.0")]
    public void Invalid_ip_is_rejected(string ip)
    {
        var store = NewStoreWithProject();
        var cp = Queries.getOrCreatePrimaryControlProps(store).Value;
        cp.PlcVendor = "LsXgk";
        cp.PlcIpAddress = ip;
        cp.PlcPort = 2004;

        Assert.Null(PromakerShared.PlcConnectionResolver.TryReadFromStore(store));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Out_of_range_port_is_rejected(int port)
    {
        var store = NewStoreWithProject();
        var cp = Queries.getOrCreatePrimaryControlProps(store).Value;
        cp.PlcVendor = "LsXgk";
        cp.PlcIpAddress = "192.168.9.100";
        cp.PlcPort = port;

        Assert.Null(PromakerShared.PlcConnectionResolver.TryReadFromStore(store));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Resolve 우선순위
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_falls_back_to_sidecar_when_project_has_no_connection()
    {
        var path = SidecarPath;
        Sidecar(PromakerShared.PlcVendorChoice.LsXgi, "192.168.0.10", 2004).TrySave(path);

        var result = PromakerShared.PlcConnectionResolver.Resolve(NewStoreWithProject(), path);

        Assert.Equal(PromakerShared.PlcConnectionSource.Sidecar, result.Source);
        Assert.Equal("192.168.0.10", result.Settings.IpAddress);
    }

    [Fact]
    public void Resolve_prefers_project_connection_over_sidecar()
    {
        var path = SidecarPath;
        Sidecar(PromakerShared.PlcVendorChoice.LsXgi, "192.168.0.10", 2004).TrySave(path);

        var store = NewStoreWithProject();
        PromakerShared.PlcConnectionResolver.StampToStore(
            store, Sidecar(PromakerShared.PlcVendorChoice.LsXgk, "192.168.9.100", 2004));

        var result = PromakerShared.PlcConnectionResolver.Resolve(store, path);

        Assert.Equal(PromakerShared.PlcConnectionSource.Aasx, result.Source);
        Assert.Equal("192.168.9.100", result.Settings.IpAddress);
        Assert.Equal(nameof(PromakerShared.PlcVendorChoice.LsXgk), result.Settings.Vendor);
    }

    [Fact]
    public void Resolve_skips_project_connection_when_kill_switch_off()
    {
        var path = SidecarPath;
        var sidecar = Sidecar(PromakerShared.PlcVendorChoice.LsXgi, "192.168.0.10", 2004);
        sidecar.PreferAasxPlcConnection = false;
        sidecar.TrySave(path);

        var store = NewStoreWithProject();
        PromakerShared.PlcConnectionResolver.StampToStore(
            store, Sidecar(PromakerShared.PlcVendorChoice.LsXgk, "192.168.9.100", 2004));

        var result = PromakerShared.PlcConnectionResolver.Resolve(store, path);

        Assert.Equal(PromakerShared.PlcConnectionSource.Sidecar, result.Source);
        Assert.Equal("192.168.0.10", result.Settings.IpAddress);
    }

    [Fact]
    public void Resolve_with_null_store_keeps_sidecar()
    {
        var path = SidecarPath;
        Sidecar(PromakerShared.PlcVendorChoice.Mitsubishi, "10.1.2.3", 5007).TrySave(path);

        var result = PromakerShared.PlcConnectionResolver.Resolve(null, path);

        Assert.Equal(PromakerShared.PlcConnectionSource.Sidecar, result.Source);
        Assert.Equal("10.1.2.3", result.Settings.IpAddress);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 운영 튜닝값 보존 — 접속 정보 적용이 라이브 조정값을 되돌리면 안 된다
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyToSettings_preserves_scan_interval_and_tuning_values_across_vendor_switch()
    {
        var settings = Sidecar(PromakerShared.PlcVendorChoice.LsXgi, "192.168.0.10", 2004, scanIntervalMs: 250);
        settings.AutoDurationCalibrate = false;
        settings.GanttWindowMinutes = 60;

        PromakerShared.PlcConnectionResolver.ApplyToSettings(
            settings,
            FullConn(PromakerShared.PlcVendorChoice.Mitsubishi, "10.0.0.5", 5007));

        Assert.Equal(nameof(PromakerShared.PlcVendorChoice.Mitsubishi), settings.Vendor);
        Assert.Equal("10.0.0.5", settings.IpAddress);
        Assert.Equal(5007, settings.Port);

        // Agent 가 라이브로 조정해 영속화하는 값들 — 접속 적용이 건드리면 재시작마다 원복된다.
        Assert.Equal(250, settings.ScanIntervalMs);
        Assert.False(settings.AutoDurationCalibrate);
        Assert.Equal(60, settings.GanttWindowMinutes);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 실제 AASX 파일 왕복 — store 수준이 아니라 export → 파일 → import 전 구간.
    // AASX import 는 merge 가 아니라 replace(새 인스턴스 + 존재하는 element 만 set)라
    // 한 단계라도 빠뜨리면 조용히 유실된다.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Connection_survives_real_aasx_export_import_roundtrip()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "stamped.aasx");

        var store = NewStoreWithProject();
        PromakerShared.PlcConnectionResolver.StampToStore(
            store, Sidecar(PromakerShared.PlcVendorChoice.LsXgk, "192.168.9.100", 2004));

        Assert.True(Ds2.Aasx.AasxExporter.exportFromStore(
            store, path, "https://dualsoft.com/", false, false));

        var reloaded = DsStore.empty();
        Ds2.Aasx.AasxImporter.importIntoStore(reloaded, path);

        var conn = PromakerShared.PlcConnectionResolver.TryReadFromStore(reloaded);
        Assert.NotNull(conn);
        Assert.Equal(PromakerShared.PlcVendorChoice.LsXgk, conn!.Vendor);
        Assert.Equal("192.168.9.100", conn.IpAddress);
        Assert.Equal(2004, conn.Port);
    }

    /// <summary>
    /// .sdf 왕복 — AASX 는 내보내기 형식이고 실제 작업 파일은 .sdf 다. 저장 박제는 두 형식 모두에서
    /// 실행되므로(SaveToPath 진입부), .sdf 가 새 필드를 싣지 못하면 정작 주 작업 형식에서 기능이
    /// 조용히 동작하지 않는다. AASX 왕복과 별개로 확인해야 하는 이유 — 직렬화 경로가 다르다
    /// (AASX = 리플렉션 기반 SMC 변환, .sdf = JsonConverter 의 store 전체 직렬화).
    /// </summary>
    [Fact]
    public void Connection_survives_sdf_save_load_roundtrip()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "project.sdf");

        var store = NewStoreWithProject();
        PromakerShared.PlcConnectionResolver.StampToStore(
            store, Sidecar(PromakerShared.PlcVendorChoice.Mitsubishi, "10.20.30.40", 5007,
                           isUdp: true, networkNumber: 3, stationNumber: 12, timeoutMs: 7000));
        store.SaveToFile(path);

        var reloaded = DsStore.empty();
        reloaded.LoadFromFile(path);

        var conn = PromakerShared.PlcConnectionResolver.TryReadFromStore(reloaded);
        Assert.NotNull(conn);
        Assert.Equal(PromakerShared.PlcVendorChoice.Mitsubishi, conn!.Vendor);
        Assert.Equal("10.20.30.40", conn.IpAddress);
        Assert.Equal(5007, conn.Port);

        // 벤더 파라미터까지 — 버전 게이트가 열려야 전송방식·국번이 실제로 적용된다.
        Assert.True(conn.HasVendorParams);
        Assert.True(conn.IsUdp);
        Assert.Equal(3, conn.NetworkNumber);
        Assert.Equal(12, conn.StationNumber);
        Assert.Equal(7000, conn.TimeoutMs);
    }

    /// <summary>접속 정보를 기록하지 않고 내보낸 AASX = 지금까지 배포된 구 파일의 모습.
    /// 다시 읽었을 때 "미설정" 이어야 현장 로컬 설정이 보존된다.</summary>
    [Fact]
    public void Aasx_exported_without_stamp_reloads_as_unset()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "plain.aasx");

        Assert.True(Ds2.Aasx.AasxExporter.exportFromStore(
            NewStoreWithProject(), path, "https://dualsoft.com/", false, false));

        var reloaded = DsStore.empty();
        Ds2.Aasx.AasxImporter.importIntoStore(reloaded, path);

        Assert.Null(PromakerShared.PlcConnectionResolver.TryReadFromStore(reloaded));
    }

    /// <summary>autoCreateEmptySubmodels=ON 으로 내보낸 구 AASX 는 ControlSystemProperties 가
    /// 생성자 기본값으로 채워져 있다. 이것도 "미설정" 으로 읽혀야 한다 — 아니면 구 파일이
    /// 현장 접속 설정을 192.168.0.1 로 덮어쓴다.</summary>
    [Fact]
    public void Legacy_aasx_with_auto_created_properties_reloads_as_unset()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "legacy-autocreate.aasx");

        Assert.True(Ds2.Aasx.AasxExporter.exportFromStore(
            NewStoreWithProject(), path, "https://dualsoft.com/", false, autoCreateEmptySubmodels: true));

        var reloaded = DsStore.empty();
        Ds2.Aasx.AasxImporter.importIntoStore(reloaded, path);

        Assert.Null(PromakerShared.PlcConnectionResolver.TryReadFromStore(reloaded));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Agent 업로드 → 활성화 시퀀스 재현
    // MonitoringSupervisor.TryActivateAsync 의 AASX 로드 ~ 게이트웨이 빌드 구간과 동일한 호출 순서.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Agent 가 세션 AASX 로 하는 일을 그대로: import → IOMap → UserTag 주소 →
    /// 접속 해석 → 게이트웨이 빌드. 오류 없이 프로젝트가 지정한 PLC 로 구성되어야 한다.</summary>
    [Fact]
    public void Agent_activation_sequence_succeeds_with_stamped_aasx()
    {
        Directory.CreateDirectory(_root);
        var aasxPath = Path.Combine(_root, "project.aasx");
        var sidecarPath = SidecarPath;

        // Agent 가 붙을 sidecar 는 다른 PLC 를 가리키고 있다 — AASX 가 이겨야 한다.
        Sidecar(PromakerShared.PlcVendorChoice.LsXgi, "192.168.0.10", 2004).TrySave(sidecarPath);

        // Promaker 업로드 측: 접속 박제 + UserTag(구독 주소) 포함해 export.
        var authoring = NewStoreWithProject();
        var systemId = PrimarySystemId(authoring);
        authoring.AddUserTag(systemId, "TAG1", "Info", "%MX100", "Bool", "", "");
        PromakerShared.PlcConnectionResolver.StampToStore(
            authoring, Sidecar(PromakerShared.PlcVendorChoice.LsXgk, "192.168.9.100", 2004));
        Assert.True(Ds2.Aasx.AasxExporter.exportFromStore(
            authoring, aasxPath, "https://dualsoft.com/", false, false));

        // ── 여기부터 Agent 측 ──
        var store = DsStore.empty();
        var importResult = Ds2.Aasx.AasxImporter.importIntoStoreWithError(store, aasxPath);
        Assert.False(importResult.IsError);

        var ioMap = Ds2.Runtime.IO.SignalIOMapModule.build(store);
        var userTagAddresses = store.GetAllUserTagsForProject()
            .Select(r => r.TagAddress)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToList();
        Assert.NotEmpty(userTagAddresses);

        var resolution = PromakerShared.PlcConnectionResolver.Resolve(store, sidecarPath);
        Assert.Equal(PromakerShared.PlcConnectionSource.Aasx, resolution.Source);
        Assert.Empty(resolution.Warnings);

        var gateway = PromakerShared.PlcGatewayConfigBuilder.TryBuild(
            resolution.Settings, ioMap, out var errors, userTagAddresses);

        Assert.Empty(errors);
        Assert.NotNull(gateway);

        var connection = gateway!.Connections.Head;
        Assert.Equal("192.168.9.100", connection.IpAddress);
        Assert.Equal(2004, connection.Port);
        Assert.Equal(Ds2.Backend.Plc.PlcVendor.LsXgk, connection.Vendor);
    }

    /// <summary>접속 정보가 없는 구 AASX 를 Agent 가 받으면 로컬 sidecar 로 붙는다 — 기존 동작 그대로.</summary>
    [Fact]
    public void Agent_activation_sequence_falls_back_to_sidecar_for_legacy_aasx()
    {
        Directory.CreateDirectory(_root);
        var aasxPath = Path.Combine(_root, "legacy.aasx");
        var sidecarPath = SidecarPath;
        Sidecar(PromakerShared.PlcVendorChoice.Mitsubishi, "10.1.2.3", 5007).TrySave(sidecarPath);

        var authoring = NewStoreWithProject();
        var systemId = PrimarySystemId(authoring);
        authoring.AddUserTag(systemId, "TAG1", "Info", "D100", "Int16", "", "");
        Assert.True(Ds2.Aasx.AasxExporter.exportFromStore(
            authoring, aasxPath, "https://dualsoft.com/", false, false));

        var store = DsStore.empty();
        Assert.False(Ds2.Aasx.AasxImporter.importIntoStoreWithError(store, aasxPath).IsError);

        var ioMap = Ds2.Runtime.IO.SignalIOMapModule.build(store);
        var userTagAddresses = store.GetAllUserTagsForProject()
            .Select(r => r.TagAddress).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();

        var resolution = PromakerShared.PlcConnectionResolver.Resolve(store, sidecarPath);
        Assert.Equal(PromakerShared.PlcConnectionSource.Sidecar, resolution.Source);

        var gateway = PromakerShared.PlcGatewayConfigBuilder.TryBuild(
            resolution.Settings, ioMap, out var errors, userTagAddresses);

        Assert.Empty(errors);
        Assert.NotNull(gateway);
        Assert.Equal("10.1.2.3", gateway!.Connections.Head.IpAddress);
    }

    [Fact]
    public void Matches_detects_identical_connection()
    {
        var settings = Sidecar(PromakerShared.PlcVendorChoice.LsXgk, "192.168.9.100", 2004);

        Assert.True(PromakerShared.PlcConnectionResolver.Matches(
            settings, FullConn(PromakerShared.PlcVendorChoice.LsXgk, "192.168.9.100", 2004)));
        Assert.False(PromakerShared.PlcConnectionResolver.Matches(
            settings, FullConn(PromakerShared.PlcVendorChoice.LsXgk, "192.168.9.101", 2004)));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 저장된 적 없는 설정은 기록하지 않는다
    // — 아무도 고르지 않은 생성자 기본값이 파일을 타고 전파되는 것 방지.
    //   판별은 값 비교가 아니라 저장 이력으로 한다: 실제로 192.168.0.10:2004 을 쓰는 현장이 존재하므로
    //   값만 보면 "손댄 적 없음" 과 "손댔는데 마침 기본값" 을 구분할 수 없다.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>PlcConnection.json 이 없으면 로드 결과는 "저장된 적 없음" 이고, 저장하면 그 시점부터 true.</summary>
    [Fact]
    public void WasPersisted_tracks_whether_the_file_exists()
    {
        var path = SidecarPath;

        Assert.False(PromakerShared.PlcConnectionSettings.LoadOrDefault(path).WasPersisted);

        var s = new PromakerShared.PlcConnectionSettings();
        Assert.True(s.TrySave(path));
        Assert.True(s.WasPersisted);
        Assert.True(PromakerShared.PlcConnectionSettings.LoadOrDefault(path).WasPersisted);
    }

    /// <summary>손상되어 내용을 못 읽은 파일은 저장 이력으로 인정하지 않는다 — 파일이 "있다" 는 사실만으로
    /// 화면의 생성자 기본값이 프로젝트에 기록되면 안 된다.</summary>
    [Fact]
    public void Corrupt_file_does_not_count_as_persisted()
    {
        var path = SidecarPath;
        File.WriteAllText(path, "{ this is not valid json");
        Assert.False(PromakerShared.PlcConnectionSettings.LoadOrDefault(path).WasPersisted);

        File.WriteAllText(path, "null");   // 파싱은 되지만 내용이 없다
        Assert.False(PromakerShared.PlcConnectionSettings.LoadOrDefault(path).WasPersisted);
    }

    /// <summary>출처 표식은 설정이 아니다 — JSON 에 새어 나가면 Agent 의 설정 지문
    /// (MonitoringSupervisor.ComputeConfigFingerprint)이 흔들려 무의미한 BackendHost 재시작을 유발한다.
    /// 지문은 <b>기본 옵션</b>으로 직렬화하므로 파일 저장 경로(camelCase)와 따로 확인한다.</summary>
    [Fact]
    public void WasPersisted_is_not_serialized()
    {
        var path = SidecarPath;
        var settings = Sidecar(PromakerShared.PlcVendorChoice.LsXgk, "192.168.9.100", 2004);
        Assert.True(settings.TrySave(path));

        Assert.DoesNotContain("asPersisted", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("asPersisted",
            System.Text.Json.JsonSerializer.Serialize(settings), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PLC 설정을 한 번도 확정한 적 없는 PC 에서 저장해도 프로젝트에 접속 정보가 박히지 않아야 한다.
    /// 박히면 아무도 고른 적 없는 192.168.0.10 이 파일을 타고 다음 사람의 설정을 덮는다 —
    /// 이 기능이 없애려던 문제와 같은 부류. 부수 효과로 SequenceControl 서브모델도 생기지 않는다.
    /// </summary>
    [Fact]
    public void Never_persisted_settings_are_not_stamped()
    {
        var store = NewStoreWithProject();

        Assert.False(PromakerShared.PlcConnectionResolver.StampToStore(
            store, new PromakerShared.PlcConnectionSettings()));

        // ControlSystemProperties 자체가 만들어지지 않았다 = SequenceControl 서브모델도 없다.
        var cp = Queries.tryGetPrimaryControlProps(store);
        Assert.True(cp is null
                    || !Microsoft.FSharp.Core.FSharpOption<Ds2.Core.ControlSystemProperties>.get_IsSome(cp));
        Assert.Null(PromakerShared.PlcConnectionResolver.TryReadFromStore(store));
    }

    /// <summary>저장된 적 없는 설정이 이미 기록된 실제 접속을 덮어쓰지 않아야 한다.</summary>
    [Fact]
    public void Never_persisted_settings_do_not_overwrite_a_recorded_connection()
    {
        var store = NewStoreWithProject();
        PromakerShared.PlcConnectionResolver.StampToStore(
            store, Sidecar(PromakerShared.PlcVendorChoice.LsXgk, "192.168.9.100", 2004));

        Assert.False(PromakerShared.PlcConnectionResolver.StampToStore(
            store, new PromakerShared.PlcConnectionSettings()));

        var conn = PromakerShared.PlcConnectionResolver.TryReadFromStore(store);
        Assert.NotNull(conn);
        Assert.Equal("192.168.9.100", conn!.IpAddress);
    }

    /// <summary><b>회귀 방지의 핵심</b> — 실제로 기본값과 같은 주소(192.168.0.10:2004)를 쓰는 현장이라도,
    /// 설정을 확정한 PC 라면 프로젝트에 기록되어야 한다. 값 비교로 게이트를 만들면 여기서 조용히 누락된다.</summary>
    [Fact]
    public void Settings_equal_to_defaults_are_still_stamped_when_persisted()
    {
        var path = SidecarPath;
        var chosen = new PromakerShared.PlcConnectionSettings();   // 이 현장의 PLC 가 마침 기본값과 동일
        Assert.True(chosen.TrySave(path));

        var store = NewStoreWithProject();
        Assert.True(PromakerShared.PlcConnectionResolver.StampToStore(
            store, PromakerShared.PlcConnectionSettings.LoadOrDefault(path)));

        var conn = PromakerShared.PlcConnectionResolver.TryReadFromStore(store);
        Assert.NotNull(conn);
        Assert.Equal("192.168.0.10", conn!.IpAddress);
        Assert.Equal(2004, conn.Port);
    }

    /// <summary>확정된 설정은 서브모델이 없던 프로젝트에도 정상적으로 기록된다 —
    /// 게이트가 기능 자체를 막아버리지 않았는지 확인.</summary>
    [Fact]
    public void Persisted_settings_still_create_the_properties_when_missing()
    {
        var store = NewStoreWithProject();

        Assert.True(PromakerShared.PlcConnectionResolver.StampToStore(
            store, Sidecar(PromakerShared.PlcVendorChoice.LsXgk, "192.168.9.100", 2004)));
        Assert.NotNull(PromakerShared.PlcConnectionResolver.TryReadFromStore(store));
    }

    /// <summary>파일 수준 확인 — 설정을 확정한 적 없는 PC 에서 저장하면 AASX 에 SequenceControl 서브모델이
    /// 생기지 않아야 한다. (게이트 도입 전 실측: 서브모델이 없던 프로젝트가 25,778 → 26,318 bytes 로
    /// 커지며 SequenceControl 이 추가됐다.)</summary>
    [Fact]
    public void Never_persisted_save_adds_no_control_submodel_to_the_file()
    {
        Directory.CreateDirectory(_root);

        static string Export(string path, DsStore store)
        {
            Assert.True(Ds2.Aasx.AasxExporter.exportFromStore(store, path, "https://dualsoft.com/", false, false));
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var entry = zip.Entries.First(e => e.FullName.EndsWith(".aas.xml", StringComparison.OrdinalIgnoreCase));
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }

        var untouched = NewStoreWithProject();
        PromakerShared.PlcConnectionResolver.StampToStore(untouched, new PromakerShared.PlcConnectionSettings());
        Assert.DoesNotContain("<idShort>SequenceControl</idShort>",
            Export(Path.Combine(_root, "untouched.aasx"), untouched), StringComparison.Ordinal);

        // 확정된 설정 쪽은 여전히 서브모델이 생겨야 한다 — 게이트가 기능을 막지 않았는지 대조.
        var configured = NewStoreWithProject();
        PromakerShared.PlcConnectionResolver.StampToStore(
            configured, Sidecar(PromakerShared.PlcVendorChoice.LsXgk, "192.168.9.100", 2004));
        Assert.Contains("<idShort>SequenceControl</idShort>",
            Export(Path.Combine(_root, "configured.aasx"), configured), StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 적용 구현은 한 벌 — Promaker(파일 열기) 와 Agent(Resolve) 가 어긋나면 진단이 불가능해진다
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>같은 프로젝트 + 같은 로컬 설정이면 Promaker 화면과 Agent 실제 접속이 동일해야 한다.
    /// 두 경로가 갈라지면 "Promaker 는 A 를 보여주는데 현장은 B 에 붙어 있다" 가 되어
    /// 로그만으로는 원인을 짚을 수 없다.</summary>
    [Fact]
    public void Promaker_open_path_matches_agent_resolve_path()
    {
        var sidecarPath = SidecarPath;
        Sidecar(PromakerShared.PlcVendorChoice.LsXgi, "192.168.0.10", 2004, scanIntervalMs: 250)
            .TrySave(sidecarPath);

        var store = NewStoreWithProject();
        PromakerShared.PlcConnectionResolver.StampToStore(
            store, Sidecar(PromakerShared.PlcVendorChoice.Mitsubishi, "10.20.30.40", 5007,
                           isUdp: true, networkNumber: 3, stationNumber: 12, timeoutMs: 7000));

        // Agent 측: Resolve 한 방.
        var agent = PromakerShared.PlcConnectionResolver.Resolve(store, sidecarPath).Settings;

        // ⚠ 두 경로가 나란히 무동작이어도 "동일" 은 성립한다(둘 다 sidecar 값 유지). 비교가 공허해지지
        //    않도록, 프로젝트 값이 실제로 sidecar 를 이겼다는 것을 먼저 못박는다.
        Assert.Equal(nameof(PromakerShared.PlcVendorChoice.Mitsubishi), agent.Vendor);
        Assert.Equal("10.20.30.40", agent.IpAddress);
        Assert.True(agent.IsUdp);
        Assert.Equal(12, agent.StationNumber);

        // Promaker 측: 로컬 설정 로드 → 파일 열기 시 프로젝트 접속 적용.
        var vm = Promaker.ViewModels.PlcSettings.FromPoco(
            PromakerShared.PlcConnectionSettings.LoadOrDefault(sidecarPath));
        var conn = PromakerShared.PlcConnectionResolver.TryReadFromStore(store);
        Assert.NotNull(conn);
        vm.ApplyConnection(conn!);
        var promaker = vm.ToPoco();

        Assert.Equal(agent.Vendor, promaker.Vendor);
        Assert.Equal(agent.IpAddress, promaker.IpAddress);
        Assert.Equal(agent.Port, promaker.Port);
        Assert.Equal(agent.IsUdp, promaker.IsUdp);
        Assert.Equal(agent.NetworkNumber, promaker.NetworkNumber);
        Assert.Equal(agent.StationNumber, promaker.StationNumber);
        Assert.Equal(agent.LocalEthernet, promaker.LocalEthernet);
        Assert.Equal(agent.TimeoutMs, promaker.TimeoutMs);

        // 운영 튜닝값(스캔주기)은 양쪽 모두 로컬 값을 보존한다.
        Assert.Equal(250, agent.ScanIntervalMs);
        Assert.Equal(250, promaker.ScanIntervalMs);
    }
}
