using System;
using System.IO;
using System.Windows;
using AasxEditor.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AasxEditor.Desktop;

public partial class App : Application
{
    public static IServiceProvider ServiceProvider { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        var services = new ServiceCollection();

        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif

        services.AddSingleton<CircuitTracker>();
        services.AddSingleton<AasxConverterService>();
        services.AddSingleton<AasTreeBuilderService>();
        services.AddSingleton<AasEntityExtractor>();
        services.AddSingleton<IAasMetadataStore>(sp =>
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AasxEditor");
            Directory.CreateDirectory(dataDir);
            var dbPath = Path.Combine(dataDir, "aas_metadata.db");
            var store = new SqliteMetadataStore(dbPath);
            store.InitializeAsync().GetAwaiter().GetResult();
            return store;
        });

        ServiceProvider = services.BuildServiceProvider();

        base.OnStartup(e);
    }
}
