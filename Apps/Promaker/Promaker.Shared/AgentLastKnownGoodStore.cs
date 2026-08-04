using System.Security.Cryptography;
using System.Text.Json;

namespace Promaker.Shared;

/// <summary>
/// 실제 기동까지 성공한 Agent 입력을 버전 디렉터리에 보관한다. current.json을 마지막에
/// 원자 교체하므로 저장 도중 프로세스가 종료되어도 이전 정상 스냅샷을 계속 가리킨다.
/// </summary>
public static class AgentLastKnownGoodStore
{
    private const int SchemaVersion = 1;
    private const string AasxName = "project.aasx";
    private const string PlcName = "PlcConnection.json";
    private const string UaName = "OpcUaServer.json";
    private const string SessionName = "session.json";
    private const string PointerName = "current.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed record Snapshot(AgentSession Session, OpcUaServerSettings UaSettings, string ModelHash);

    private sealed record Pointer(
        int Version,
        string SnapshotId,
        string ModelHash,
        string SessionHash,
        string UaSettingsHash,
        string? PlcSettingsHash,
        string CreatedAtUtc);

    public static bool TrySave(
        AgentSession sourceSession,
        OpcUaServerSettings uaSettings,
        string expectedModelHash,
        out string error)
        => TrySave(SharedPaths.AgentLastKnownGoodDirectory, sourceSession, uaSettings, expectedModelHash, out error);

    public static bool TrySave(
        string root,
        AgentSession sourceSession,
        OpcUaServerSettings uaSettings,
        string expectedModelHash,
        out string error)
    {
        error = "";
        string? staging = null;
        try
        {
            if (string.IsNullOrWhiteSpace(sourceSession.AasxPath) || !File.Exists(sourceSession.AasxPath))
            {
                error = $"Successful AASX source no longer exists at '{sourceSession.AasxPath}'.";
                return false;
            }

            var snapshotId = Guid.NewGuid().ToString("N");
            var snapshotsRoot = Path.Combine(Path.GetFullPath(root), "snapshots");
            Directory.CreateDirectory(snapshotsRoot);
            staging = Path.Combine(snapshotsRoot, $".staging-{snapshotId}");
            var finalDirectory = Path.Combine(snapshotsRoot, snapshotId);
            Directory.CreateDirectory(staging);

            var stagedAasx = Path.Combine(staging, AasxName);
            File.Copy(sourceSession.AasxPath, stagedAasx, overwrite: false);
            var copiedHash = ComputeSha256(stagedAasx);
            if (string.IsNullOrWhiteSpace(expectedModelHash)
                || !string.Equals(copiedHash, expectedModelHash, StringComparison.OrdinalIgnoreCase))
            {
                error = "AASX changed while the successful activation snapshot was being captured.";
                return false;
            }

            var hasPlc = !string.IsNullOrWhiteSpace(sourceSession.PlcConnectionPath)
                         && File.Exists(sourceSession.PlcConnectionPath);
            if (hasPlc)
                File.Copy(sourceSession.PlcConnectionPath, Path.Combine(staging, PlcName), overwrite: false);

            if (!uaSettings.TrySave(Path.Combine(staging, UaName)))
            {
                error = "Failed to save OPC UA settings into the activation snapshot.";
                return false;
            }

            var snapshotSession = new AgentSession
            {
                AasxPath = Path.Combine(finalDirectory, AasxName),
                PlcConnectionPath = hasPlc ? Path.Combine(finalDirectory, PlcName) : "",
                ActivatedAtUtc = sourceSession.ActivatedAtUtc,
                RequestedBy = "agent-recovery",
                RuntimeMode = sourceSession.RuntimeMode,
                IsRealPlcConnected = sourceSession.IsRealPlcConnected,
                SchemaVersion = sourceSession.SchemaVersion,
            };
            if (!snapshotSession.TrySave(Path.Combine(staging, SessionName)))
            {
                error = "Failed to save the activation session snapshot.";
                return false;
            }

            Directory.Move(staging, finalDirectory);
            staging = null;

            var pointer = new Pointer(
                SchemaVersion,
                snapshotId,
                copiedHash,
                ComputeSha256(Path.Combine(finalDirectory, SessionName)),
                ComputeSha256(Path.Combine(finalDirectory, UaName)),
                hasPlc ? ComputeSha256(Path.Combine(finalDirectory, PlcName)) : null,
                DateTime.UtcNow.ToString("o"));
            var pointerPath = Path.Combine(Path.GetFullPath(root), PointerName);
            var pointerTemp = pointerPath + $".tmp-{Guid.NewGuid():N}";
            File.WriteAllText(pointerTemp, JsonSerializer.Serialize(pointer, JsonOpts));
            File.Move(pointerTemp, pointerPath, overwrite: true);
            RemoveOldSnapshots(snapshotsRoot, snapshotId);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Last-known-good snapshot save failed: {ex.Message}";
            return false;
        }
        finally
        {
            try { if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { }
        }
    }

    public static bool TryLoad(out Snapshot? snapshot, out string error)
        => TryLoad(SharedPaths.AgentLastKnownGoodDirectory, out snapshot, out error);

    public static bool TryLoad(string root, out Snapshot? snapshot, out string error)
    {
        snapshot = null;
        error = "";
        try
        {
            var fullRoot = Path.GetFullPath(root);
            var pointerPath = Path.Combine(fullRoot, PointerName);
            if (!File.Exists(pointerPath))
            {
                error = "No last-known-good activation snapshot exists.";
                return false;
            }

            var pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllText(pointerPath), JsonOpts);
            if (pointer is null || pointer.Version != SchemaVersion
                || !Guid.TryParseExact(pointer.SnapshotId, "N", out _)
                || string.IsNullOrWhiteSpace(pointer.ModelHash)
                || string.IsNullOrWhiteSpace(pointer.SessionHash)
                || string.IsNullOrWhiteSpace(pointer.UaSettingsHash))
            {
                error = "The last-known-good snapshot pointer is invalid.";
                return false;
            }

            var directory = Path.Combine(fullRoot, "snapshots", pointer.SnapshotId);
            var sessionPath = Path.Combine(directory, SessionName);
            var uaSettingsPath = Path.Combine(directory, UaName);
            if (!HashMatches(sessionPath, pointer.SessionHash)
                || !HashMatches(uaSettingsPath, pointer.UaSettingsHash))
            {
                error = "The last-known-good session or OPC UA settings integrity check failed.";
                return false;
            }
            if (!AgentSession.TryLoadExact(sessionPath, out var session, out var sessionError)
                || session is null)
            {
                error = $"The last-known-good session is invalid: {sessionError}";
                return false;
            }
            if (!File.Exists(session.AasxPath))
            {
                error = "The last-known-good session or AASX is missing.";
                return false;
            }
            if (!IsInside(directory, session.AasxPath)
                || (!string.IsNullOrWhiteSpace(session.PlcConnectionPath)
                    && (!IsInside(directory, session.PlcConnectionPath)
                        || !File.Exists(session.PlcConnectionPath)
                        || string.IsNullOrWhiteSpace(pointer.PlcSettingsHash)
                        || !HashMatches(session.PlcConnectionPath, pointer.PlcSettingsHash))))
            {
                error = "The last-known-good session contains an unsafe or missing path.";
                return false;
            }

            var actualHash = ComputeSha256(session.AasxPath);
            if (!string.Equals(actualHash, pointer.ModelHash, StringComparison.OrdinalIgnoreCase))
            {
                error = "The last-known-good AASX integrity check failed.";
                return false;
            }
            if (!OpcUaServerSettings.TryLoadExact(uaSettingsPath, out var settings, out error)
                || settings is null)
                return false;

            snapshot = new Snapshot(session, settings, actualHash);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Last-known-good snapshot load failed: {ex.Message}";
            return false;
        }
    }

    private static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".."
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static bool HashMatches(string path, string? expected) =>
        File.Exists(path)
        && !string.IsNullOrWhiteSpace(expected)
        && string.Equals(ComputeSha256(path), expected, StringComparison.OrdinalIgnoreCase);

    private static void RemoveOldSnapshots(string snapshotsRoot, string currentId)
    {
        try
        {
            var stale = new DirectoryInfo(snapshotsRoot).EnumerateDirectories()
                .Where(directory => !directory.Name.StartsWith(".staging-", StringComparison.Ordinal)
                                    && !string.Equals(directory.Name, currentId, StringComparison.Ordinal))
                .OrderByDescending(directory => directory.CreationTimeUtc)
                .Skip(1);
            foreach (var directory in stale)
            {
                try { directory.Delete(recursive: true); } catch { }
            }
        }
        catch { }
    }
}
