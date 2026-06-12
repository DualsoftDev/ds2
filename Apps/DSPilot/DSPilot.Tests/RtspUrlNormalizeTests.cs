// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
// Commercial license required for use. See Apps/DSPilot/LICENSE.
using DSPilot.Services;
using Xunit;

namespace DSPilot.Tests;

/// <summary>
/// <see cref="CctvMediaMtxService.NormalizeRtspSourceUrl"/> 단위 테스트 — 비밀번호에 `@` 등
/// 특수문자가 든 RTSP 주소를 MediaMTX(Go net/url)가 받을 수 있게 userinfo 만 percent-encode 하는
/// 규칙을 고정한다: 마지막 `@`(호스트 구분자)는 보존, 기 인코딩 `%XX` 는 이중 인코딩 금지(멱등).
/// </summary>
public class RtspUrlNormalizeTests
{
    [Fact]
    public void Password_with_at_sign_is_encoded_and_host_separator_preserved()
    {
        // 실기 사례: dual@soft 비밀번호 — 마지막 @ 만 호스트 구분자로 남는다.
        Assert.Equal(
            "rtsp://dualsoft1:dual%40soft@192.168.9.13/stream1",
            CctvMediaMtxService.NormalizeRtspSourceUrl("rtsp://dualsoft1:dual@soft@192.168.9.13/stream1"));
    }

    [Fact]
    public void Already_encoded_url_is_unchanged()
    {
        const string url = "rtsp://dualsoft1:dual%40soft@192.168.9.13/stream1";
        Assert.Equal(url, CctvMediaMtxService.NormalizeRtspSourceUrl(url));
    }

    [Fact]
    public void Normalization_is_idempotent()
    {
        var once = CctvMediaMtxService.NormalizeRtspSourceUrl("rtsp://u:p@ss w@rd@host/stream");
        Assert.Equal(once, CctvMediaMtxService.NormalizeRtspSourceUrl(once));
    }

    [Fact]
    public void Mixed_encoded_and_raw_chars_encode_only_raw()
    {
        Assert.Equal(
            "rtsp://u:a%40b%40c@host/stream",
            CctvMediaMtxService.NormalizeRtspSourceUrl("rtsp://u:a%40b@c@host/stream"));
    }

    [Fact]
    public void Percent_not_followed_by_hex_is_escaped()
    {
        Assert.Equal(
            "rtsp://u:50%25off@host/stream",
            CctvMediaMtxService.NormalizeRtspSourceUrl("rtsp://u:50%off@host/stream"));
    }

    [Fact]
    public void Colon_in_password_is_allowed_unencoded()
    {
        // RFC 3986 userinfo 에서 ':' 는 합법 — 첫 ':' 이후는 비밀번호의 일부.
        const string url = "rtsp://u:a:b@host/stream";
        Assert.Equal(url, CctvMediaMtxService.NormalizeRtspSourceUrl(url));
    }

    [Fact]
    public void Url_without_credentials_is_unchanged()
    {
        const string url = "rtsp://192.168.9.13:554/stream1";
        Assert.Equal(url, CctvMediaMtxService.NormalizeRtspSourceUrl(url));
    }

    [Fact]
    public void At_sign_in_path_or_query_is_not_treated_as_credentials()
    {
        const string url = "rtsp://host/stream?token=a@b";
        Assert.Equal(url, CctvMediaMtxService.NormalizeRtspSourceUrl(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("192.168.9.13/stream1")]
    public void Non_url_input_passes_through(string input)
    {
        Assert.Equal(input, CctvMediaMtxService.NormalizeRtspSourceUrl(input));
    }

    [Fact]
    public void Space_and_hash_like_specials_in_password_are_encoded()
    {
        Assert.Equal(
            "rtsp://u:p%20w%5Bd%5D@host/stream",
            CctvMediaMtxService.NormalizeRtspSourceUrl("rtsp://u:p w[d]@host/stream"));
    }

    // ── 복구 모드: 비밀번호 속 '/'·'?'·'#' 가 authority 를 조기 종결시킨 경우 ──

    [Fact]
    public void Slash_in_password_is_recovered_and_encoded()
    {
        Assert.Equal(
            "rtsp://dualsoft1:dual%2Fsoft@192.168.9.13/stream1",
            CctvMediaMtxService.NormalizeRtspSourceUrl("rtsp://dualsoft1:dual/soft@192.168.9.13/stream1"));
    }

    [Fact]
    public void Question_mark_in_password_is_recovered_and_encoded()
    {
        Assert.Equal(
            "rtsp://u:p%3Fss@host/stream",
            CctvMediaMtxService.NormalizeRtspSourceUrl("rtsp://u:p?ss@host/stream"));
    }

    [Fact]
    public void Hash_in_password_with_port_is_recovered_and_encoded()
    {
        Assert.Equal(
            "rtsp://u:p%23ss@host:554/stream",
            CctvMediaMtxService.NormalizeRtspSourceUrl("rtsp://u:p#ss@host:554/stream"));
    }

    [Fact]
    public void Slash_and_at_sign_combined_in_password_are_both_encoded()
    {
        Assert.Equal(
            "rtsp://u:a%2Fb%40c@host/stream",
            CctvMediaMtxService.NormalizeRtspSourceUrl("rtsp://u:a/b@c@host/stream"));
    }

    [Fact]
    public void Recovered_url_is_idempotent()
    {
        var once = CctvMediaMtxService.NormalizeRtspSourceUrl("rtsp://u:p/w?d@host/stream");
        Assert.Equal(once, CctvMediaMtxService.NormalizeRtspSourceUrl(once));
    }

    [Fact]
    public void Valid_credential_less_url_with_at_in_query_never_enters_recovery()
    {
        // authority(host:port)가 유효하면 복구 모드 자체가 발동하지 않아야 한다 — 쿼리의 @ 미간섭.
        const string url = "rtsp://192.168.9.13:554/cam/realmonitor?channel=1&user=a@b";
        Assert.Equal(url, CctvMediaMtxService.NormalizeRtspSourceUrl(url));
    }

    [Fact]
    public void Recovery_without_plausible_host_candidate_leaves_url_unchanged()
    {
        // 비숫자 포트로 깨졌지만 뒤에 @ 가 없으면 복구 불가 — 원문 유지(어차피 접속 불가였던 URL).
        const string url = "rtsp://host:notaport/stream";
        Assert.Equal(url, CctvMediaMtxService.NormalizeRtspSourceUrl(url));
    }

    [Fact]
    public void Numeric_password_prefix_before_slash_is_a_known_miss()
    {
        // 한계 명문화: '/' 앞 비밀번호 조각이 순수 숫자면 우연히 유효한 host:port 로 읽혀
        // 깨짐을 감지할 수 없다 — 이 경우만 사용자가 %2F 직접 입력 필요.
        const string url = "rtsp://u:554/ab@host/stream";
        Assert.Equal(url, CctvMediaMtxService.NormalizeRtspSourceUrl(url));
    }
}
