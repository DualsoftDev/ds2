// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
using System.Net;
using System.Text;
using System.Text.Json;
using DSPilot.Services.CloudAuth;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DSPilot.Tests;

public sealed class ProvisioningAuthClientTests
{
    [Fact]
    public async Task TrialRegistrationUsesClaimEndpointAndInstanceCredential()
    {
        var handler = new CaptureHandler();
        var options = new CloudAuthOptions
        {
            BaseUrl = "https://pv.example",
            TrialInstanceId = "inst_trial",
            TrialClaimToken = "claim-secret"
        };
        var sut = new ProvisioningAuthClient(
            new HttpClient(handler), options, NullLogger<ProvisioningAuthClient>.Instance);

        var result = await sut.RegisterAsync("admin@example.com", "Abcdef1!", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("https://pv.example/api/provision/claim", handler.RequestUri);
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("inst_trial", json.RootElement.GetProperty("instance_id").GetString());
        Assert.Equal("claim-secret", json.RootElement.GetProperty("claim_token").GetString());
        Assert.Equal("admin@example.com", json.RootElement.GetProperty("login_id").GetString());
        Assert.Equal("Abcdef1!", json.RootElement.GetProperty("password").GetString());
        Assert.False(json.RootElement.TryGetProperty("display_name", out _));
        Assert.False(json.RootElement.TryGetProperty("company_name", out _));
    }

    [Fact]
    public async Task OrdinaryRegistrationUsesAdminEndpointWithOnlyCredentials()
    {
        var handler = new CaptureHandler();
        var options = new CloudAuthOptions { BaseUrl = "https://pv.example" };
        var sut = new ProvisioningAuthClient(
            new HttpClient(handler), options, NullLogger<ProvisioningAuthClient>.Instance);

        var result = await sut.RegisterAsync("admin@example.com", "Abcdef1!", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("https://pv.example/api/admin/register", handler.RequestUri);
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
        Assert.Equal("admin@example.com", json.RootElement.GetProperty("login_id").GetString());
        Assert.Equal("Abcdef1!", json.RootElement.GetProperty("password").GetString());
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"admin_id":"adm_trial","admin_session":"session-token"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
