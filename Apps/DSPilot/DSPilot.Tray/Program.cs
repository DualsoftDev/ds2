namespace DSPilot.Tray;

static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    static void Main()
    {
        // 단일 인스턴스 보장
        const string mutexName = "DSPilotTray_SingleInstance";
        _mutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
