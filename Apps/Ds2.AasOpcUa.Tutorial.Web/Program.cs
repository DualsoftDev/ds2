using Ds2.AasOpcUa.Tutorial.Web.Components;
using Ds2.AasOpcUa.Tutorial.Web.Services;
using Ds2.OpcUa.Server.Server;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server (Interactive Server rendering).
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// In-process OPC UA server — starts automatically with the web app.
// PilotAssetSeeder 는 이 튜토리얼 프로젝트가 소유. Server 는 자산 명세를 모름.
builder.Services.AddSingleton<IUaAssetSeeder, PilotAssetSeeder>();
builder.Services.AddHostedService<DsUaServerService>();

// Tutorial services.
builder.Services.AddSingleton<UaLiveClientService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
