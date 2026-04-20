using System.ServiceProcess;

namespace DSPilot.Tray;

internal static class ServiceManager
{
    private const string ServiceName = "DSPilotService";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static void StopService()
    {
        using var sc = new ServiceController(ServiceName);
        if (sc.Status == ServiceControllerStatus.Running ||
            sc.Status == ServiceControllerStatus.StartPending)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
        }
    }

    public static void StartService()
    {
        using var sc = new ServiceController(ServiceName);
        if (sc.Status == ServiceControllerStatus.Stopped ||
            sc.Status == ServiceControllerStatus.StopPending)
        {
            if (sc.Status == ServiceControllerStatus.StopPending)
                sc.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);

            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, Timeout);
        }
    }

    public static void RestartService()
    {
        using var sc = new ServiceController(ServiceName);
        if (sc.Status == ServiceControllerStatus.Running ||
            sc.Status == ServiceControllerStatus.StartPending)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
        }

        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, Timeout);
    }

    public static ServiceControllerStatus GetStatus()
    {
        using var sc = new ServiceController(ServiceName);
        return sc.Status;
    }
}
