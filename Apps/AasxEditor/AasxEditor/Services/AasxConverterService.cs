using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.IO.Compression;
using AasCore.Aas3_0;
using Env = AasCore.Aas3_0.Environment;

namespace AasxEditor.Services;

public class AasxConverterService
{
    /// <summary>
    /// AASX 바이트 배열 → AAS Environment
    /// </summary>
    public Env? ReadEnvironmentFromBytes(byte[] aasxBytes)
    {
        using var ms = new MemoryStream(aasxBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        var aasPath = ResolveAasPath(archive);
        if (aasPath is null) return null;

        var entry = archive.GetEntry(aasPath);
        if (entry is null) return null;

        using var stream = entry.Open();

        if (aasPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            // 먼저 네임스페이스를 확인하여 AAS 버전 검증
            using var preReadStream = new MemoryStream();
            stream.CopyTo(preReadStream);
            preReadStream.Position = 0;

            var detectedNs = DetectAasNamespace(preReadStream);
            if (detectedNs is not null && detectedNs != "https://admin-shell.io/aas/3/0")
                throw new NotSupportedException(
                    $"이 파일은 AAS v3.0이 아닙니다 (감지된 네임스페이스: {detectedNs}). " +
                    $"AAS v1.0/v2.0 파일은 먼저 v3.0으로 변환해야 합니다.");

            preReadStream.Position = 0;
            using var xmlReader = XmlReader.Create(preReadStream);
            xmlReader.MoveToContent();
            return Xmlization.Deserialize.EnvironmentFrom(xmlReader);
        }
        else
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var json = reader.ReadToEnd();
            var node = JsonNode.Parse(json);
            return Jsonization.Deserialize.EnvironmentFrom(node!);
        }
    }

    private static string? DetectAasNamespace(Stream xmlStream)
    {
        try
        {
            using var xmlReader = XmlReader.Create(xmlStream);
            while (xmlReader.Read())
            {
                if (xmlReader.NodeType == XmlNodeType.Element)
                    return xmlReader.NamespaceURI;
            }
        }
        catch { /* 무시 */ }
        return null;
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
        catch (AasCore.Aas3_0.Jsonization.Exception ex)
        {
            return (false, $"AAS 구조 오류: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"검증 실패: {ex.Message}");
        }
    }

    private static readonly string[] AasSpecTypes =
    [
        "http://www.admin-shell.io/aasx/relationships/aas-spec",
        "http://admin-shell.io/aasx/relationships/aas-spec"
    ];

    private static string? ResolveAasPath(ZipArchive archive)
    {
        var relsEntry = archive.GetEntry("aasx/_rels/aasx-origin.rels");
        if (relsEntry is null) return null;

        using var stream = relsEntry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var xml = reader.ReadToEnd();

        var doc = new XmlDocument();
        doc.LoadXml(xml);

        var nsm = new XmlNamespaceManager(doc.NameTable);
        nsm.AddNamespace("r", "http://schemas.openxmlformats.org/package/2006/relationships");

        // www 있는 버전과 없는 버전 모두 지원
        foreach (var type in AasSpecTypes)
        {
            var node = doc.SelectSingleNode(
                $"//r:Relationship[@Type='{type}']", nsm);
            if (node is not null)
                return node.Attributes?["Target"]?.Value?.TrimStart('/');
        }

        return null;
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
