using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
// SSOT 직접 참조 — 같은 네임스페이스(Promaker.Services)에 forwarder SharedPaths(AasxFilePath 만 전달)가
// 있어 'SharedPaths' alias 는 그 네임스페이스 멤버에 가려진다. 충돌 안 나는 이름(SP)으로 alias.
using SP = Promaker.Shared.SharedPaths;

namespace Promaker.Services;

/// <summary>
/// 'Agent에 업로드 ▸ 네트워크' 전송 클라이언트. SaveToSharedLocation 이 로컬 공유 폴더에 만들어둔
/// 모델/설정/세션(project.aasx · PlcConnection.json · session.json)을 zip 으로 묶어 원격 Agent
/// (http://ip:5050/upload)로 POST 한다. 원격 Agent(AgentUploadReceiver)가 풀어서 자기 공유 폴더에
/// 배치하고 active.flag 를 세워 모니터링을 시작한다 — 받는 쪽이 session 경로를 자기 로컬로 교정하므로
/// 보내는 쪽은 경로를 신경 쓰지 않는다.
/// </summary>
public static class AgentUploadClient
{
    /// <summary>업로드 수신 포트. Promaker.Agent 의 AgentUploadReceiver.Port 와 동일.</summary>
    public const int Port = 5050;

    public static async Task<(bool ok, string message)> UploadAsync(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return (false, "네트워크 대상 IP 주소를 입력하세요.");

        var aasx = SP.AasxFilePath;
        var plc  = SP.PlcConnectionFilePath;
        var sess = SP.AgentSessionJsonPath;
        if (!File.Exists(aasx))
            return (false, "업로드할 모델(project.aasx)이 없습니다 — 저장이 선행되어야 합니다.");

        var tmpZip = Path.Combine(Path.GetTempPath(), $"agent-upload-{Guid.NewGuid():N}.zip");
        try
        {
            using (var zip = ZipFile.Open(tmpZip, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(aasx, "project.aasx");
                if (File.Exists(plc))  zip.CreateEntryFromFile(plc,  "PlcConnection.json");
                if (File.Exists(sess)) zip.CreateEntryFromFile(sess, "session.json");
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var content = new ByteArrayContent(await File.ReadAllBytesAsync(tmpZip).ConfigureAwait(false));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

            var resp = await http.PostAsync($"http://{ip}:{Port}/upload", content).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return resp.IsSuccessStatusCode
                ? (true, $"원격 Agent({ip})에 업로드됨.")
                : (false, $"원격 Agent 응답 오류 ({(int)resp.StatusCode}): {body}");
        }
        catch (Exception ex)
        {
            return (false, $"원격 Agent({ip}:{Port}) 전송 실패 — 우분투 Agent 가 실행 중이고 {Port} 포트가 열렸는지 확인하세요.\n{ex.Message}");
        }
        finally
        {
            try { File.Delete(tmpZip); } catch { /* 임시 파일 정리 실패 무시 */ }
        }
    }
}
