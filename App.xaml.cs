using System.Windows;

namespace GearGlass;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\GearGlass.SingleInstance";
    private const string RevealEventName = @"Local\GearGlass.Reveal";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _revealEvent;
    private RegisteredWaitHandle? _revealWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A second copy would fail to register Ctrl+Alt+F12 and stack another HUD on top.
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            SignalRunningInstance();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // Another launch sets this event; the running HUD then opens itself instead of
        // the new process quitting without any visible response.
        _revealEvent = new EventWaitHandle(false, EventResetMode.AutoReset, RevealEventName);
        _revealWait = ThreadPool.RegisterWaitForSingleObject(
            _revealEvent,
            (_, _) => Dispatcher.BeginInvoke(window.RevealHud),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    private static void SignalRunningInstance()
    {
        try
        {
            using var reveal = EventWaitHandle.OpenExisting(RevealEventName);
            reveal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first instance is still starting up and has not created the event yet.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _revealWait?.Unregister(null);
        _revealWait = null;
        _revealEvent?.Dispose();
        _revealEvent = null;

        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }
}
