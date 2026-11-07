using Avalonia;
using System;
using System.Threading;
using MediaUnseal.Services;

namespace MediaUnseal;

sealed class Program
{
    internal static SingleInstanceCoordinator? Instance { get; private set; }
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        using var instance = new SingleInstanceCoordinator();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!instance.TryAcquire())
        {
            if (instance.NotifyPrimaryAsync(TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult()) return;
            if (DateTime.UtcNow >= deadline) return;
            Thread.Sleep(100);
        }

        Instance = instance;
        instance.StartServer();
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { Instance = null; }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
