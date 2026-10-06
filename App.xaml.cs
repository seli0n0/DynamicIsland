using System.IO;
using System.Windows;

namespace DynamicIsland;

public partial class App : Application
{
    const string InstanceName = "DynamicIsland.SingleInstance";
    const string LogFileName = "DynamicIsland.log";
    static readonly TimeSpan PredecessorExitTimeout = TimeSpan.FromSeconds(10);

    Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, InstanceName, out bool isOnlyInstance);
        if (!isOnlyInstance && e.Args.Contains(Updater.RestartedFlag)) isOnlyInstance = WaitForPredecessor(_mutex);
        if (!isOnlyInstance)
        {
            Shutdown();
            return;
        }
        Updater.CleanUp();

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };
        new MainWindow().Show();
    }

    public static void Log(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), LogFileName),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

    /// <summary>One line of what the machine is doing, in the same file its faults use. A phone that goes quiet, or is
    /// refused at the door, leaves nothing behind in a log made only for exceptions — which makes it indistinguishable
    /// from a phone that never appeared at all.</summary>
    public static void Note(string what)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), LogFileName),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what}\n");
        }
        catch { }
    }

    static bool WaitForPredecessor(Mutex mutex)
    {
        try { return mutex.WaitOne(PredecessorExitTimeout); }
        catch (AbandonedMutexException) { return true; }
    }
}
