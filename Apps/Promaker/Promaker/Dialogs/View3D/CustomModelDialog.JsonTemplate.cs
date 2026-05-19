using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace Promaker.Windows;

public partial class CustomModelDialog
{
    private string _lastGeneratedTemplate = "";

    /// <summary>
    /// 선택된 SystemType의 ApiDef 이름을 기반으로 JSON 템플릿 자동 생성
    /// </summary>
    private void GenerateJsonTemplate(string systemType)
    {
        var apiDefNames = GetEffectiveApiDefs(systemType);
        var apiDefsField = apiDefNames.Count > 0
            ? $"\n  \"apiDefs\": [{string.Join(", ", apiDefNames.Select(n => $"\"{n}\""))}],"
            : "";

        if (apiDefNames.Count == 0)
        {
            // ApiDef 정보 없음 → 기본 단일 애니메이션 템플릿
            JsonEditor.Text = $$"""
            {
              "name": "{{systemType}}",
              "height": 2.0,
              "parts": [
                {"id": "base", "shape": "box", "size": [1.2, 0.3, 1.2], "color": "#64748b"},
                {"id": "body", "shape": "box", "size": [0.8, 1.0, 0.8], "color": "#fbbf24", "glow": 0.3, "on": "base"}
              ],
              "animation": {
                "active": {"target": "body", "type": "move", "axis": "y", "min": 0.65, "max": 1.5}
              }
            }
            """;
        }
        else if (apiDefNames.Count == 1)
        {
            // ApiDef 1개 → 단일 애니메이션
            JsonEditor.Text = $$"""
            {
              "name": "{{systemType}}",{{apiDefsField}}
              "height": 2.0,
              "parts": [
                {"id": "base", "shape": "box", "size": [1.2, 0.3, 1.2], "color": "#64748b"},
                {"id": "body", "shape": "box", "size": [0.8, 1.0, 0.8], "color": "#fbbf24", "glow": 0.3, "on": "base"}
              ],
              "animation": {
                "active": {"target": "body", "type": "move", "axis": "y", "min": 0.65, "max": 1.5}
              }
            }
            """;
        }
        else
        {
            // ApiDef 2개 이상 → dirs 템플릿 자동 생성
            // dirs는 기본 loop="restart" (전진만 반복, 시작점 스냅) — 상반 방향이 명확히 구분됨
            var dirsEntries = string.Join(",\n    ",
                apiDefNames.Select(name =>
                    $"\"{name}\": {{\"target\": \"body\", \"type\": \"move\", \"axis\": \"x\", \"min\": 0, \"max\": 0.5, \"loop\": \"restart\"}}"));

            var apiDefComment = string.Join(", ", apiDefNames.Select((n, i) => $"{n}(#{i})"));

            JsonEditor.Text = $$"""
            {
              "name": "{{systemType}}",{{apiDefsField}}
              "height": 2.0,
              "parts": [
                {"id": "base", "shape": "box", "size": [1.5, 0.3, 1.0], "color": "#64748b"},
                {"id": "body", "shape": "box", "size": [0.6, 0.4, 0.9], "color": "#fbbf24", "glow": 0.3, "on": "base"}
              ],
              "dirs": {
                {{dirsEntries}}
              }
            }
            """;

            ShowMessage($"ApiDef 감지: {apiDefComment} — dirs가 자동 생성되었습니다. 파트와 애니메이션을 수정하세요.", false);
        }

        _lastGeneratedTemplate = JsonEditor.Text ?? "";
    }

    private void LoadFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Device JSON (*.device.json;*.json)|*.device.json;*.json|All Files (*.*)|*.*",
            Title = "JSON 디바이스 파일 선택"
        };

        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                JsonEditor.Text = File.ReadAllText(dlg.FileName);
            }
            catch (Exception ex)
            {
                ShowMessage($"파일 읽기 실패: {ex.Message}", true);
            }
        }
    }

    private void CopyAIPrompt_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var specPath = Path.Combine(_wwwrootPath, "DEVICE_JSON_SPEC.md");
            if (!File.Exists(specPath))
            {
                ShowMessage("DEVICE_JSON_SPEC.md 파일을 찾을 수 없습니다.", true);
                return;
            }

            var spec = File.ReadAllText(specPath);
            var systemType = GetSystemType();

            // 장비 맥락 정보 생성
            var context = BuildDeviceContext(systemType);

            var prompt = $"""
            {spec}

            ---

            {context}
            """;

            Clipboard.SetText(prompt.Trim());

            var msg = string.IsNullOrEmpty(systemType)
                ? "AI 프롬프트가 복사되었습니다. AI에게 붙여넣기 후 장비를 설명하세요."
                : $"AI 프롬프트가 복사되었습니다 ({systemType} 맥락 포함). AI에게 붙여넣기 후 원하는 모양을 설명하세요.";
            ShowMessage(msg, false);
        }
        catch (Exception ex)
        {
            ShowMessage($"클립보드 복사 실패: {ex.Message}", true);
        }
    }

    private string BuildDeviceContext(string systemType)
    {
        if (string.IsNullOrEmpty(systemType))
        {
            return "## 요청\n" +
                   "위 스펙에 따라 산업 장비의 3D 모델 JSON을 생성해주세요.\n" +
                   "장비 설명: [여기에 원하는 장비를 설명하세요]";
        }

        var apiDefNames = GetEffectiveApiDefs(systemType);

        if (apiDefNames.Count <= 1)
        {
            var apiNote = apiDefNames.Count == 1
                ? $"\n- ApiDef: \"{apiDefNames[0]}\" (1개) → \"animation\" 사용"
                : "";
            return "## 요청\n" +
                   "다음 장비의 3D 모델 JSON을 생성해주세요.\n\n" +
                   $"- SystemType 이름: \"{systemType}\"\n" +
                   $"- 출력 JSON의 \"name\"은 반드시 \"{systemType}\"으로 설정" +
                   apiNote + "\n\n" +
                   "장비 설명: [여기에 원하는 장비를 설명하세요]";
        }

        // ApiDef 2개 이상 → dirs 필수
        var apiDefList = string.Join("\n",
            apiDefNames.Select((n, i) => $"  #{i}: \"{n}\""));

        var dirsLines = string.Join(",\n      ",
            apiDefNames.Select(n =>
                $"\"{n}\": {{\"target\": \"...\", \"type\": \"move\", \"axis\": \"...\", \"min\": 0, \"max\": 0.5, \"loop\": \"restart\"}}"));

        return "## 요청\n" +
               "다음 장비의 3D 모델 JSON을 생성해주세요.\n\n" +
               $"- SystemType 이름: \"{systemType}\"\n" +
               $"- 출력 JSON의 \"name\"은 반드시 \"{systemType}\"으로 설정\n" +
               "- ApiDef 목록 (이 순서와 이름을 반드시 유지):\n" +
               apiDefList + "\n" +
               "- ApiDef가 2개 이상이므로 \"animation\" 대신 \"dirs\"를 사용:\n" +
               "  ```\n" +
               "  \"dirs\": {\n" +
               "      " + dirsLines + "\n" +
               "  }\n" +
               "  ```\n" +
               "- 각 dir의 애니메이션은 해당 ApiDef의 물리적 동작에 맞게 설정\n" +
               "- `loop` 필드로 진행 방식 지정 (상반 방향 구분에 중요):\n" +
               "  - \"restart\" (dirs 기본): 전진만 반복하고 끝나면 시작점으로 스냅 — 피스톤/컨베이어 등\n" +
               "  - \"once\": 한번 진행 후 정지, Idle 되면 원위치 복귀 — 리프터/도어 등\n" +
               "  - \"pingpong\": min↔max 왕복 (방향성 없음)\n\n" +
               "장비 설명: [여기에 원하는 장비를 설명하세요]";
    }

    private static string? ExtractJsonName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("name", out var prop) ? prop.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// JSON의 "name" 필드를 지정된 systemType으로 교체.
    /// AI가 생성한 JSON의 name이 달라도 SystemType에 맞게 강제 통일.
    /// </summary>
    private static string PatchJsonName(string json, string systemType)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // "name" 필드가 이미 일치하면 그대로 반환
            if (root.TryGetProperty("name", out var nameProp) &&
                nameProp.GetString() == systemType)
                return json;

            // JSON을 수정하여 "name"을 systemType으로 교체
            using var ms = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                // "name"을 먼저 쓰고
                writer.WriteString("name", systemType);
                // 나머지 프로퍼티를 복사 ("name" 제외)
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "name") continue;
                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            return json; // 패치 실패 시 원본 반환
        }
    }
}
