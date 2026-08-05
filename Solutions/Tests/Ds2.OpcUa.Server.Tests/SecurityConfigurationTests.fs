module Ds2.OpcUa.Server.Tests.SecurityConfigurationTests

open System
open System.IO
open Opc.Ua
open Xunit
open Ds2.OpcUa.Server.Server

[<Fact>]
let ``locked configuration removes anonymous and unsecured endpoints`` () =
    let root = Path.Combine(Path.GetTempPath(), "ds2-opcua-security-" + Guid.NewGuid().ToString("N"))
    let cfg =
        { ServerConfiguration.defaultConfig root with
            AllowAnonymous = false
            AllowUnsecuredEndpoint = false
            AutoAcceptUntrustedCertificates = false }

    let app = ServerConfiguration.build cfg
    let hasNoneEndpoint =
        app.ServerConfiguration.SecurityPolicies
        |> Seq.exists (fun policy -> policy.SecurityMode = MessageSecurityMode.None)
    let hasAnonymousToken =
        app.ServerConfiguration.UserTokenPolicies
        |> Seq.exists (fun policy -> policy.TokenType = UserTokenType.Anonymous)
    let hasUserNameToken =
        app.ServerConfiguration.UserTokenPolicies
        |> Seq.exists (fun policy -> policy.TokenType = UserTokenType.UserName)

    Assert.False(hasNoneEndpoint)
    Assert.False(hasAnonymousToken)
    Assert.False(hasUserNameToken)
    Assert.False(app.SecurityConfiguration.AutoAcceptUntrustedCertificates)
    Assert.True(app.SecurityConfiguration.RejectSHA1SignedCertificates)
    Assert.EndsWith("trustedUser", app.SecurityConfiguration.TrustedUserCertificates.StorePath)
    Assert.EndsWith("issuerUser", app.SecurityConfiguration.UserIssuerCertificates.StorePath)

[<Fact>]
let ``standalone defaults are locked`` () =
    let cfg = ServerConfiguration.defaultConfig(Path.GetTempPath())
    Assert.False(cfg.AllowAnonymous)
    Assert.False(cfg.AllowUnsecuredEndpoint)
    Assert.False(cfg.AutoAcceptUntrustedCertificates)
