// SPDX-License-Identifier: LicenseRef-Dualsoft-Commercial
// Copyright (c) 2026 Dualsoft Inc. All rights reserved.
using DSPilot.Services;
using Microsoft.AspNetCore.Mvc;

namespace DSPilot.Controllers;

[ApiController]
[Route("api/opcua-certificates")]
public sealed class OpcUaCertificatesController : ControllerBase
{
    private readonly OpcUaClientCertificateService _certificates;
    private readonly DemoAdminService _admin;

    public OpcUaCertificatesController(
        OpcUaClientCertificateService certificates,
        DemoAdminService admin)
    {
        _certificates = certificates;
        _admin = admin;
    }

    public sealed class IssueCertificateRequest
    {
        public string Password { get; set; } = string.Empty;
    }

    [HttpPost("user")]
    public IActionResult IssueUserCertificate([FromBody] IssueCertificateRequest request)
    {
        if (!CanManageCertificates()) return Unauthorized();
        if (string.IsNullOrEmpty(request.Password))
            return BadRequest("PFX 암호를 입력하세요.");

        try
        {
            var issued = _certificates.IssueUserCertificate(request.Password);
            Response.Headers["X-OPC-UA-Certificate-Thumbprint"] = issued.Thumbprint;
            Response.Headers["X-OPC-UA-Certificate-Expires"] = issued.NotAfterUtc.ToString("O");
            return File(issued.PfxBytes, "application/x-pkcs12", issued.FileName);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("rejected")]
    public ActionResult<IReadOnlyList<OpcUaRejectedCertificate>> GetRejected()
    {
        if (!CanManageCertificates()) return Unauthorized();
        return Ok(_certificates.ListRejectedApplicationCertificates());
    }

    [HttpPost("rejected/{thumbprint}/trust")]
    public IActionResult TrustRejected(string thumbprint)
    {
        if (!CanManageCertificates()) return Unauthorized();
        return _certificates.TrustRejectedApplicationCertificate(thumbprint)
            ? Ok(new { trusted = true })
            : NotFound("해당 거부 인증서를 찾을 수 없습니다.");
    }

    private bool CanManageCertificates() =>
        !_admin.IsEnabled
        || _admin.IsSessionValid(Request.Cookies[DemoAdminService.SessionCookieName]);
}
