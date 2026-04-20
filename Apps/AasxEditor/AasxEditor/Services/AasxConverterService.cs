using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.IO.Compression;
using AasCore.Aas3_1;
using Env = AasCore.Aas3_1.Environment;

namespace AasxEditor.Services;

public class AasxConverterService
{
    /// <summary>
    /// AASX 바이트 배열 → AAS Environment.
    /// Ds2.Aasx의 스트림 리더에 위임하여 v1.0/v2.0/v3.0 파일도 v3.1로 자동 정규화합니다.
    /// </summary>
    public Env? ReadEnvironmentFromBytes(byte[] aasxBytes)
    {
        using var ms = new MemoryStream(aasxBytes);
        var result = Ds2.Aasx.AasxFileIO.readEnvironmentFromStreamWithError(ms);
        if (result.IsOk) return result.ResultValue;
        throw new NotSupportedException(result.ErrorValue);
    }

    /// <summary>
    /// AAS Environment → 표준 AAS JSON 문자열
    /// </summary>
    public string EnvironmentToJson(Env env)
    {
        var jsonNode = Jsonization.Serialize.ToJsonObject(env);
        var options = new JsonSerializerOptions { WriteIndented = true };
        return jsonNode.ToJsonString(options);
    }

    /// <summary>
    /// 표준 AAS JSON 문자열 → AAS Environment
    /// </summary>
    public Env? JsonToEnvironment(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is null) return null;
        return Jsonization.Deserialize.EnvironmentFrom(node);
    }

    /// <summary>
    /// AAS Environment → AASX 바이트 배열
    /// </summary>
    public byte[] WriteEnvironmentToBytes(Env env)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(archive, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="utf-8"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
                  <Default Extension="xml" ContentType="text/xml" />
                  <Override PartName="/aasx/aasx-origin" ContentType="text/plain" />
                </Types>
                """);

            WriteTextEntry(archive, "_rels/.rels",
                """
                <?xml version="1.0" encoding="utf-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Type="http://www.admin-shell.io/aasx/relationships/aasx-origin" Target="/aasx/aasx-origin" Id="R1" />
                </Relationships>
                """);

            WriteTextEntry(archive, "aasx/aasx-origin", "Intentionally empty.");

            WriteTextEntry(archive, "aasx/_rels/aasx-origin.rels",
                """
                <?xml version="1.0" encoding="utf-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Type="http://www.admin-shell.io/aasx/relationships/aas-spec" Target="/aasx/aas/aas.aas.xml" Id="R2" />
                </Relationships>
                """);

            var xmlEntry = archive.CreateEntry("aasx/aas/aas.aas.xml");
            using (var xmlStream = xmlEntry.Open())
            {
                var settings = new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 };
                using var xmlWriter = XmlWriter.Create(xmlStream, settings);
                Xmlization.Serialize.To(env, xmlWriter);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// JSON 검증: 파싱 가능하고 AAS 구조가 맞는지 확인
    /// </summary>
    public (bool isValid, string? error) ValidateJson(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is null) return (false, "JSON 파싱 실패: null");

            var env = Jsonization.Deserialize.EnvironmentFrom(node);
            if (env is null) return (false, "AAS Environment 역직렬화 실패");

            return (true, null);
        }
        catch (JsonException ex)
        {
            return (false, $"JSON 구문 오류: {ex.Message}");
        }
        catch (Jsonization.Exception ex)
        {
            return (false, $"AAS 구조 오류: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"검증 실패: {ex.Message}");
        }
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
