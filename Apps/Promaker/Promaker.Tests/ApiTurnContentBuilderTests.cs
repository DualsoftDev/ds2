using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.LlmAgent;
using Microsoft.Extensions.AI;
using Promaker.LlmAgent.Api;
using Xunit;

namespace Promaker.Tests;

// Round-trip 최적화 — doc: Apps/Promaker/Docs/todo-promaker-llm-roundtrip-optimization.md
//
// `ApiTurnContentBuilder` (ApiChatProvider 에서 추출된 helper) 의 핵심 분기 회귀 방어.
// 검증 항목:
//   - §H1 sticky snapshot 갱신 정책 (null/empty 시 유지, non-empty 시 교체)
//   - §C1 prompt-for-history 의 non-text attachment summary prepend
//   - §5.2/§J2 multi-content 빌드 순서 (snapshot → prompt → attachments) + cache_control 람다 호출 여부
public class ApiTurnContentBuilderTests
{
    private static readonly Attachment[] NoAttachments = Array.Empty<Attachment>();

    // ────────────────────────────────────────────────────────────────────
    // UpdateStickySnapshot — §H1
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void UpdateSticky_NullCurrent_NullIncoming_ReturnsNull()
    {
        var result = ApiTurnContentBuilder.UpdateStickySnapshot(null, null);
        Assert.Null(result);
    }

    [Fact]
    public void UpdateSticky_NullCurrent_NonEmptyIncoming_AdoptsIncoming()
    {
        var result = ApiTurnContentBuilder.UpdateStickySnapshot(null, "<snap rev=1/>");
        Assert.Equal("<snap rev=1/>", result);
    }

    [Fact]
    public void UpdateSticky_ExistingCurrent_NullIncoming_PreservesCurrent()
    {
        // revision 무변경 turn — incoming = null 이지만 sticky 는 유지 (LLM context 보존).
        var result = ApiTurnContentBuilder.UpdateStickySnapshot("<snap rev=1/>", null);
        Assert.Equal("<snap rev=1/>", result);
    }

    [Fact]
    public void UpdateSticky_ExistingCurrent_EmptyIncoming_PreservesCurrent()
    {
        // empty string 도 null 과 동일 — silent reset 방지.
        var result = ApiTurnContentBuilder.UpdateStickySnapshot("<snap rev=1/>", string.Empty);
        Assert.Equal("<snap rev=1/>", result);
    }

    [Fact]
    public void UpdateSticky_ExistingCurrent_NewIncoming_AdoptsNew()
    {
        // revision 변경 turn — incoming 으로 교체.
        var result = ApiTurnContentBuilder.UpdateStickySnapshot("<snap rev=1/>", "<snap rev=2/>");
        Assert.Equal("<snap rev=2/>", result);
    }

    // ────────────────────────────────────────────────────────────────────
    // BuildPromptForHistory — §C1
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void PromptForHistory_NoAttachments_ReturnsTextUnchanged()
    {
        var result = ApiTurnContentBuilder.BuildPromptForHistory("hello", NoAttachments);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void PromptForHistory_WithAttachment_PrependsSummary()
    {
        var attachments = new[] { Attachment.NewImage("a.png", new byte[] { 1, 2, 3 }, ImageFormat.Png) };
        var result = ApiTurnContentBuilder.BuildPromptForHistory("hello", attachments);
        // summary 가 prepend 되고 '\n' 으로 본문과 분리 — bytes 자체는 history 에 들어가지 않음.
        Assert.NotEqual("hello", result);
        Assert.EndsWith("\nhello", result);
    }

    [Fact]
    public void PromptForHistory_MultipleAttachments_JoinsSummariesWithSpace()
    {
        var attachments = new[]
        {
            Attachment.NewImage("a.png", new byte[] { 1 }, ImageFormat.Png),
            Attachment.NewImage("b.jpg", new byte[] { 2 }, ImageFormat.Jpeg),
        };
        var result = ApiTurnContentBuilder.BuildPromptForHistory("body", attachments);
        var head = result.Substring(0, result.Length - "\nbody".Length);
        // 두 summary 가 single space 로 join (newline 아님).
        Assert.DoesNotContain("\n", head);
        Assert.Contains(' ', head);
    }

    // ────────────────────────────────────────────────────────────────────
    // BuildMultiContents — §5.2 / §J2 / §C1
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildMulti_SnapshotOnly_TwoTextContentsInOrder()
    {
        var contents = ApiTurnContentBuilder.BuildMultiContents(
            "<snap rev=1/>", "prompt body", NoAttachments, applyCacheControlForSnapshot: null);
        Assert.Equal(2, contents.Count);
        Assert.Equal("<snap rev=1/>", Assert.IsType<TextContent>(contents[0]).Text);
        Assert.Equal("prompt body", Assert.IsType<TextContent>(contents[1]).Text);
    }

    [Fact]
    public void BuildMulti_NullSnapshot_OnlyPromptText()
    {
        // hasSnapshot=false 분기 — snapshot 부재 시에는 attachment 만으로 호출됨 (caller 가 hasAttachments 분기).
        var attachments = new[] { Attachment.NewImage("a.png", new byte[] { 1 }, ImageFormat.Png) };
        var contents = ApiTurnContentBuilder.BuildMultiContents(
            null, "prompt", attachments, applyCacheControlForSnapshot: null);
        Assert.Equal(2, contents.Count);
        Assert.Equal("prompt", Assert.IsType<TextContent>(contents[0]).Text);
        Assert.IsType<DataContent>(contents[1]);
    }

    [Fact]
    public void BuildMulti_EmptySnapshot_TreatedAsAbsent()
    {
        // empty string 도 null 과 동등하게 snapshot 미부착.
        var contents = ApiTurnContentBuilder.BuildMultiContents(
            string.Empty, "prompt", NoAttachments, applyCacheControlForSnapshot: null);
        Assert.Single(contents);
        Assert.Equal("prompt", Assert.IsType<TextContent>(contents[0]).Text);
    }

    [Fact]
    public void BuildMulti_SnapshotWithCacheLambda_AppliesLambdaOnceToSnapshotOnly()
    {
        var applied = new List<AIContent>();
        Func<AIContent, AIContent> applyCache = c =>
        {
            applied.Add(c);
            return c;
        };

        ApiTurnContentBuilder.BuildMultiContents(
            "<snap/>", "prompt", NoAttachments, applyCache);

        // capability 비트 분기 의도 — 람다는 snapshot TextContent 에만 1회 호출.
        Assert.Single(applied);
        Assert.Equal("<snap/>", Assert.IsType<TextContent>(applied[0]).Text);
    }

    [Fact]
    public void BuildMulti_NullCacheLambda_NoLambdaInvoked()
    {
        // OpenAI/Groq/Ollama capability 미지원 — 람다 null 이면 cache_control 부착 skip.
        // 람다 자체 미호출 → AIContent 가 원본 TextContent 그대로 (별도 wrapping 없음).
        var contents = ApiTurnContentBuilder.BuildMultiContents(
            "<snap/>", "prompt", NoAttachments, applyCacheControlForSnapshot: null);
        Assert.IsType<TextContent>(contents[0]);
    }

    [Fact]
    public void BuildMulti_SnapshotAndImageAttachment_OrdersSnapshotPromptDataContent()
    {
        var attachments = new[] { Attachment.NewImage("a.png", new byte[] { 1, 2 }, ImageFormat.Png) };
        var contents = ApiTurnContentBuilder.BuildMultiContents(
            "<snap/>", "prompt", attachments, applyCacheControlForSnapshot: null);

        Assert.Equal(3, contents.Count);
        Assert.Equal("<snap/>", Assert.IsType<TextContent>(contents[0]).Text);
        Assert.Equal("prompt", Assert.IsType<TextContent>(contents[1]).Text);

        var data = Assert.IsType<DataContent>(contents[2]);
        Assert.Equal("a.png", data.Name);
        Assert.Equal("image/png", data.MediaType);
    }

    [Fact]
    public void BuildMulti_PdfAttachment_UsesApplicationPdfMediaType()
    {
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46 };
        var attachments = new[] { Attachment.NewPdf("doc.pdf", pdfBytes) };
        var contents = ApiTurnContentBuilder.BuildMultiContents(
            null, "prompt", attachments, applyCacheControlForSnapshot: null);

        Assert.Equal(2, contents.Count);
        var data = Assert.IsType<DataContent>(contents[1]);
        Assert.Equal("doc.pdf", data.Name);
        Assert.Equal("application/pdf", data.MediaType);
    }

    [Fact]
    public void BuildMulti_MixedAttachments_PreservesOrder()
    {
        var attachments = new[]
        {
            Attachment.NewImage("a.png", new byte[] { 1 }, ImageFormat.Png),
            Attachment.NewPdf("b.pdf", new byte[] { 2 }),
            Attachment.NewImage("c.jpg", new byte[] { 3 }, ImageFormat.Jpeg),
        };
        var contents = ApiTurnContentBuilder.BuildMultiContents(
            "<snap/>", "prompt", attachments, applyCacheControlForSnapshot: null);

        Assert.Equal(5, contents.Count);
        var dataItems = contents.OfType<DataContent>().ToList();
        Assert.Equal(3, dataItems.Count);
        Assert.Equal("a.png", dataItems[0].Name);
        Assert.Equal("b.pdf", dataItems[1].Name);
        Assert.Equal("c.jpg", dataItems[2].Name);
    }
}
