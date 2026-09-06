using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;

namespace AramOverlay.App;

/// <summary>
/// A tray icon over Shell_NotifyIcon directly.
///
/// WinForms' NotifyIcon would be three lines, but referencing WinForms drags in
/// 23 MB of framework for those three lines and blocks trimming outright
/// (NETSDK1175). The API underneath is small: a message-only window to receive
/// the callback, one struct, and three calls.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 1;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;

    private const int NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const int NifMessage = 0x01, NifIcon = 0x02, NifTip = 0x04, NifInfo = 0x10;

    private readonly HwndSource _source;
    private NOTIFYICONDATA _data;
    private bool _added;

    public event Action? DoubleClicked;
    public event Action? RightClicked;

    public TrayIcon(string tooltip)
    {
        // A message-only window: never shown, exists to receive the callback.
        _source = new HwndSource(new HwndSourceParameters("aram-overlay-tray")
        {
            ParentWindow = new IntPtr(-3),      // HWND_MESSAGE
            WindowStyle = 0,
        });
        _source.AddHook(WndProc);

        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = CallbackMessage,
            hIcon = LoadAppIcon(),
            szTip = tooltip,
        };
        _added = Shell_NotifyIcon(NimAdd, ref _data);
    }

    /// <summary>The icon compiled into this exe by ApplicationIcon.</summary>
    private static IntPtr LoadAppIcon()
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            if (exe.Length > 0 && ExtractIconEx(exe, 0, large, small, 1) > 0)
                return small[0] != IntPtr.Zero ? small[0] : large[0];
        }
        catch
        {
            // Falls through to the system icon below.
        }
        return LoadIcon(IntPtr.Zero, new IntPtr(32512));    // IDI_APPLICATION
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != CallbackMessage)
            return IntPtr.Zero;
        switch ((int)lParam)
        {
            case WmLButtonDblClk:
                DoubleClicked?.Invoke();
                handled = true;
                break;
            case WmRButtonUp:
                RightClicked?.Invoke();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    public void ShowBalloon(string title, string message)
    {
        if (!_added)
            return;
        _data.uFlags = NifInfo;
        _data.szInfoTitle = title;
        _data.szInfo = message;
        _data.dwInfoFlags = 0x01;                            // NIIF_INFO
        Shell_NotifyIcon(NimModify, ref _data);
        _data.uFlags = NifMessage | NifIcon | NifTip;
    }

    /// <summary>
    /// A tray menu has to be owned by a foreground window or it will not close
    /// when the user clicks away -- a Windows quirk older than WPF.
    /// </summary>
    public void ShowMenu(ContextMenu menu)
    {
        SetForegroundWindow(_source.Handle);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    public void Dispose()
    {
        if (_added)
        {
            Shell_NotifyIcon(NimDelete, ref _data);
            _added = false;
        }
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, int count);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
