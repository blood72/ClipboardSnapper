using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace ClipboardSnapper;

internal static class Program
{
    private const string InstanceKey = "ClipboardSnapper.MainWindow";
    private static App? _app;
    private static int _pendingActivation;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
                if (instance is null) continue; // The owner may have exited during registration.
                if (instance.IsCurrent)
                {
                    instance.Activated += OnActivated;
                    try
                    {
                        Microsoft.UI.Xaml.Application.Start(_ =>
                        {
                            SynchronizationContext.SetSynchronizationContext(
                                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                            var app = new App();
                            Volatile.Write(ref _app, app);
                            if (Interlocked.Exchange(ref _pendingActivation, 0) != 0) app.RequestActivation();
                        });
                    }
                    finally
                    {
                        Volatile.Write(ref _app, null);
                        instance.Activated -= OnActivated;
                        // MainWindow has finished accepted saves and settings writes before the UI exits.
                        instance.UnregisterKey();
                    }
                    return 0;
                }

                try
                {
                    RedirectActivation(instance, activation);
                    return 0;
                }
                catch when (!IsRunning(instance.ProcessId))
                {
                    // Retry ownership only after the former owner has exited.
                }
            }
            throw new InvalidOperationException("The application owner exited during activation. Try launching again.");
        }
        catch (Exception exception)
        {
            var catalog = LanguageCatalog.Load(Path.Combine(AppContext.BaseDirectory, "lang"));
            var code = catalog.Resolve(null, CultureInfo.CurrentUICulture.Name);
            var language = catalog.Languages.First(option => option.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            var message = language.Strings.TryGetValue("SingleInstanceFailed", out var translated)
                ? translated : UiText.EnglishStrings["SingleInstanceFailed"];
            WindowActivation.ShowError(message + "\n\n" + exception.Message);
            return 1;
        }
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        Interlocked.Exchange(ref _pendingActivation, 1);
        var app = Volatile.Read(ref _app);
        if (app is not null && Interlocked.Exchange(ref _pendingActivation, 0) != 0) app.RequestActivation();
    }

    private static bool IsRunning(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
    }

    private static void RedirectActivation(AppInstance instance, AppActivationArguments activation)
    {
        // Keep the STA pumping COM/window messages while forwarding on an MTA thread.
        using var completed = new ManualResetEvent(false);
        var redirect = Task.Run(async () => await instance.RedirectActivationToAsync(activation))
            .WaitAsync(TimeSpan.FromSeconds(10));
        var signal = redirect.ContinueWith(_ =>
        {
            try { completed.Set(); }
            catch (ObjectDisposedException) { } // CoWait failed and the launch is already exiting.
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        const uint dispatchCallsAndMessages = 0x8 | 0x10;
        var result = CoWaitForMultipleHandles(dispatchCallsAndMessages, uint.MaxValue, 1,
            [completed.SafeWaitHandle.DangerousGetHandle()], out _);
        Marshal.ThrowExceptionForHR(result);
        signal.GetAwaiter().GetResult();
        redirect.GetAwaiter().GetResult();
    }

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleHandles(uint flags, uint timeout, uint count,
        [In] nint[] handles, out uint index);
}
