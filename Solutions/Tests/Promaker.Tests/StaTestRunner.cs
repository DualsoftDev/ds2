using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;

namespace Promaker.Tests;

internal static class StaTestRunner
{
    public static void Run(Action action)
    {
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
