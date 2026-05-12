using System.Text.Json;
using Promaker.Dialogs;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// M4 fix: F# emitter ↔ C# JSON payload ↔ HTML render 간 schema drift 회귀 fence.
/// `ModelDocPreviewDialog.BuildBlocksJsonFromYaml` 의 JSON property naming 이
/// `mermaid-view.html` 의 `data.type === 'render'` / `data.blocks[].title` /
/// `data.blocks[].mermaid` 가정과 동형임을 lock-in. property name 변경 시 silent broken
/// (HTML 측 block-title 빈 채 렌더) 회귀 방지.
/// </summary>
public sealed class ModelDocPreviewDialogPayloadTests
{
    private const string SampleYaml = @"
protocol: promaker/v0
project: M1
systems:
  - system: Cyl1
    kind: passive
    device: cylinder
  - system: Controller
    kind: active
    flow Run:
      works:
        W1: { calls: [Cyl1.ADV] }
";

    [Fact]
    public void Payload_top_level_keys_are_type_and_blocks()
    {
        var json = ModelDocPreviewDialog.BuildBlocksJsonFromYaml(SampleYaml);
        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        // HTML mermaid-view.html 가정: data.type === 'render'
        Assert.Equal(JsonValueKind.String, root.GetProperty("type").ValueKind);
        Assert.Equal("render", root.GetProperty("type").GetString());
        // HTML 가정: data.blocks 는 array
        Assert.Equal(JsonValueKind.Array, root.GetProperty("blocks").ValueKind);
    }

    [Fact]
    public void Each_block_has_lowercase_title_and_mermaid_keys()
    {
        var json = ModelDocPreviewDialog.BuildBlocksJsonFromYaml(SampleYaml);
        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var blocks = doc.RootElement.GetProperty("blocks");
        Assert.True(blocks.GetArrayLength() > 0, "최소 1 block 생성되어야");
        foreach (var block in blocks.EnumerateArray())
        {
            // HTML 측 `b.title` / `b.mermaid` 와 정확히 일치해야 함 (PascalCase 가 아닌 camelCase).
            Assert.True(block.TryGetProperty("title", out var titleEl), "block 에 'title' key 필수");
            Assert.True(block.TryGetProperty("mermaid", out var mermaidEl), "block 에 'mermaid' key 필수");
            Assert.Equal(JsonValueKind.String, titleEl.ValueKind);
            Assert.Equal(JsonValueKind.String, mermaidEl.ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(mermaidEl.GetString()),
                "mermaid 본문은 공백/빈 string 이면 안 됨");
        }
    }

    [Fact]
    public void Empty_or_invalid_yaml_returns_null()
    {
        Assert.Null(ModelDocPreviewDialog.BuildBlocksJsonFromYaml(""));
        Assert.Null(ModelDocPreviewDialog.BuildBlocksJsonFromYaml("   "));
        Assert.Null(ModelDocPreviewDialog.BuildBlocksJsonFromYaml(null));
    }
}
