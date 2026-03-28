using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Promaker.Tests;

internal static class StaTestRunner
{
    private sealed class WorkItem(Action action)
    {
        public Action Action { get; } = action;
        public ManualResetEventSlim Done { get; } = new(false);
        public ExceptionDispatchInfo? Error { get; set; }
    }

    private static readonly BlockingCollection<WorkItem> Queue = [];
    private static readonly Thread StaThread;

    static StaTestRunner()
    {
        StaThread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "Promaker.Tests.STA"
        };
        StaThread.SetApartmentState(ApartmentState.STA);
        StaThread.Start();
    }

    public static void Run(Action action)
    {
        var item = new WorkItem(action);
        Queue.Add(item);
        item.Done.Wait();
        item.Error?.Throw();
    }

    public static bool WaitUntil(int timeoutMs, Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            if (predicate())
                return true;

            PumpPendingUi();
            Thread.Yield();
        }
        while (DateTime.UtcNow < deadline);

        return predicate();
    }

    public static void PumpPendingUi() =>
        Dispatcher.CurrentDispatcher.Invoke(
            () => { },
            DispatcherPriority.Background);

    private static void ThreadMain()
    {
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        if (Application.Current == null)
            _ = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };

        if (Application.Current!.Resources.MergedDictionaries.Count == 0)
        {
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/Promaker;component/Themes/Theme.Dark.xaml", UriKind.Relative)
            });
        }

        foreach (var item in Queue.GetConsumingEnumerable())
        {
            try
            {
                item.Action();
            }
            catch (Exception ex)
            {
                item.Error = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                PumpPendingUi();
                item.Done.Set();
            }
        }
    }
}
