using System.Net;
using System.Net.Sockets;
using System.Windows;
using AramOverlay.Core;

namespace AramOverlay.App;

public partial class App : Application
{
    // A second launch should raise the window that is already running, not start
    // a rival that then fails to bind the widget port. The listening socket
    // doubles as the single-instance lock, so there is no lock file to go stale.
    private const int SignalPort = 8778;

    private Socket? _lock;
    private OverlayRunner? _runner;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Asked to shut down whatever is already running -- the graceful path,
        // so its tray icon goes with it.
        if (e.Args.Contains("--stop"))
        {
            Signal("quit"u8.ToArray());
            Shutdown();
            return;
        }

        if (!TryTakeLock())
        {
            Signal("show"u8.ToArray());
            Shutdown();
            return;
        }

        Settings.Load().Apply();
        Log.StartFile();

        _runner = new OverlayRunner();
        var window = new MainWindow(_runner);
        TrayHost.Install(window, _runner);
        window.Show();
        _runner.Start();
    }

    private bool TryTakeLock()
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, SignalPort));
            socket.Listen(1);
            _lock = socket;
            _ = Task.Run(ServeSignalsAsync);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task ServeSignalsAsync()
    {
        while (_lock is not null)
        {
            Socket client;
            try
            {
                client = await _lock.AcceptAsync();
            }
            catch
            {
                return;
            }
            try
            {
                var buffer = new byte[16];
                client.ReceiveTimeout = 1000;
                int read = client.Receive(buffer);
                string command = System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Max(0, read));
                if (command.StartsWith("quit"))
                    Dispatcher.Invoke(TrayHost.Quit);
                else
                    TrayHost.Show();
            }
            catch
            {
                // A signal that arrives malformed is ignored.
            }
            finally
            {
                client.Dispose();
            }
        }
    }

    private static void Signal(byte[] command)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, SignalPort);
            client.GetStream().Write(command);
        }
        catch
        {
            // Nothing running: nothing to tell.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _runner?.Stop();
        _lock?.Dispose();
        base.OnExit(e);
    }
}
