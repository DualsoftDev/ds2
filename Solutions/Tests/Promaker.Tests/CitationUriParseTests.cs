using Promaker.Controls.Llm;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// C6 — <c>attachment:///&lt;fileId&gt;/&lt;ref&gt;</c> URI parse 정합. fileId 가 SSOT 형식
/// (`&lt;collection-guid&gt;:&lt;docId&gt;`) 의 콜론을 port 로 해석하지 않도록 한 string-only parser 검증.
/// </summary>
public class CitationUriParseTests
{
    [Fact]
    public void TryParseAttachmentUri_3슬래시_기본형_fileId와_ref_분리()
    {
        var ok = LlmChatPanel.TryParseAttachmentUri(
            "attachment:///abc/p=4", out var fileId, out var @ref);
        Assert.True(ok);
        Assert.Equal("abc", fileId);
        Assert.Equal("p=4", @ref);
    }

    [Fact]
    public void TryParseAttachmentUri_fileId에_콜론_포함_SSOT_정합()
    {
        // 사양 fileId = "<collection-guid>:<docId>". 콜론을 port 로 해석하지 않아야 함.
        var ok = LlmChatPanel.TryParseAttachmentUri(
            "attachment:///3a7f1bcd-aaaa-bbbb-cccc-deadbeef0001:doc-42/p=4",
            out var fileId, out var @ref);
        Assert.True(ok);
        Assert.Equal("3a7f1bcd-aaaa-bbbb-cccc-deadbeef0001:doc-42", fileId);
        Assert.Equal("p=4", @ref);
    }

    [Fact]
    public void TryParseAttachmentUri_ref에_샵_파운드_포함_RefLocator_EBNF()
    {
        // ref 는 `p=14#img=2` 같은 RefLocator EBNF — `#` 가 fragment 가 아닌 ref 의 일부.
        var ok = LlmChatPanel.TryParseAttachmentUri(
            "attachment:///abc:doc-1/p=14#img=2", out var fileId, out var @ref);
        Assert.True(ok);
        Assert.Equal("abc:doc-1", fileId);
        Assert.Equal("p=14#img=2", @ref);
    }

    [Fact]
    public void TryParseAttachmentUri_ref에_등호_콜론_느낌표_BOM시트_RefLocator()
    {
        // ref 는 `sheet=BOM!A1:D40` 등 임의 char 포함.
        var ok = LlmChatPanel.TryParseAttachmentUri(
            "attachment:///abc:doc-1/sheet=BOM!A1:D40", out var fileId, out var @ref);
        Assert.True(ok);
        Assert.Equal("abc:doc-1", fileId);
        Assert.Equal("sheet=BOM!A1:D40", @ref);
    }

    [Fact]
    public void TryParseAttachmentUri_2슬래시_back_compat_허용()
    {
        // back-compat — LLM 이 2-slash 박제 시 fileId 콜론이 port 해석되지 않도록 같은 string-parse path.
        var ok = LlmChatPanel.TryParseAttachmentUri(
            "attachment://abc:doc-1/p=4", out var fileId, out var @ref);
        Assert.True(ok);
        Assert.Equal("abc:doc-1", fileId);
        Assert.Equal("p=4", @ref);
    }

    [Fact]
    public void TryParseAttachmentUri_다른_scheme_거부()
    {
        Assert.False(LlmChatPanel.TryParseAttachmentUri(
            "https://example.com/p=4", out _, out _));
        Assert.False(LlmChatPanel.TryParseAttachmentUri(
            "file:///c:/foo", out _, out _));
        Assert.False(LlmChatPanel.TryParseAttachmentUri(
            "", out _, out _));
    }

    [Fact]
    public void TryParseAttachmentUri_slash_없는_fileId_only_ref_빈문자()
    {
        // edge case — LLM 이 ref 누락 박제. fileId 만 도출 + ref 빈 문자열. 후속 cache miss path.
        var ok = LlmChatPanel.TryParseAttachmentUri(
            "attachment:///abc:doc-1", out var fileId, out var @ref);
        Assert.True(ok);
        Assert.Equal("abc:doc-1", fileId);
        Assert.Equal("", @ref);
    }

    [Fact]
    public void TryParseAttachmentUri_percent_encoding_decode()
    {
        // robust — LLM 이 fileId 에 percent-encoding 잘못 적용한 경우도 복원.
        var ok = LlmChatPanel.TryParseAttachmentUri(
            "attachment:///abc%3Adoc-1/p%3D4", out var fileId, out var @ref);
        Assert.True(ok);
        Assert.Equal("abc:doc-1", fileId);
        Assert.Equal("p=4", @ref);
    }
}
