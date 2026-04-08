using AasxEditor.Components;
using AasxEditor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<AasxConverterService>();
builder.Services.AddSingleton<AasTreeBuilderService>();
builder.Services.AddSingleton<AasEntityExtractor>();
builder.Services.AddSingleton<IAasMetadataStore>(sp =>
{
    var dbPath = Path.Combine(builder.Environment.ContentRootPath, "aas_metadata.db");
    var store = new SqliteMetadataStore(dbPath);
    store.InitializeAsync().GetAwaiter().GetResult();
    return store;
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
