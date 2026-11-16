using System;
using System.IO;
using System.Threading;

internal static class StartupHook
{
    private static int started;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        try
        {
            Thread worker = new Thread(Brainmind.Runtime.RuntimeHost.Start);
            worker.Name = "Brainmind startup";
            worker.IsBackground = true;
            worker.Start();
        }
        catch { /* An extension must never block the original entry point. */ }
    }
}
