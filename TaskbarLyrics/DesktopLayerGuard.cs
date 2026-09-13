using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace TaskbarLyrics;

/// <summary>
/// Keeps live-wallpaper windows (Aura Wallpaper) visible across virtual-desktop switches.
///
/// On Windows 11 24H2+ the desktop is Progman → [SHELLDLL_DefView, WorkerW, …]: the WorkerW
/// paints the static Windows picture, and live wallpapers parent their window into Progman
/// just above it. Every virtual-desktop switch Explorer rebuilds that WorkerW *on top of* the
/// live window, so the wallpaper vanishes behind the static picture and Aura never notices.
/// Whenever Explorer touches that stack we put any foreign window that ended up under a
/// WorkerW back directly beneath the desktop icons. No-op when no live wallpaper is running.
/// </summary>
public sealed class DesktopLayerGuard : IDisposable
{
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_REORDER = 0x8004;   // CREATE, DESTROY, SHOW, HIDE, REORDER
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const uint GA_PARENT = 1;
    private const uint GW_HWNDNEXT = 2;
    private const uint GW_CHILD = 5;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod,
        WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buf, int max);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // The hook stores no managed reference — keep the delegate alive ourselves.
    private readonly WinEventProc _proc;
    // Re-hooks after an Explorer restart (new Progman, new pid) and doubles as a safety net.
    private readonly DispatcherTimer _timer;
    private IntPtr _hook;
    private IntPtr _progman;
    private uint _explorerPid;
    private bool _pending;

    /// <summary>Create on the UI thread: the out-of-context hook is delivered through its message loop.</summary>
    public DesktopLayerGuard()
    {
        _proc = OnWinEvent;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => { Rehook(); Fix(); };
    }

    public void Start()
    {
        Rehook();
        Fix();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        _explorerPid = 0;
    }

    private void Rehook()
    {
        var progman = FindWindow("Progman", null);
        GetWindowThreadProcessId(progman, out var pid);
        _progman = progman;
        if (pid == _explorerPid && _hook != IntPtr.Zero) return;

        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        _explorerPid = pid;
        if (pid == 0) return;   // Explorer is restarting; the timer tries again
        _hook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_REORDER, IntPtr.Zero,
            _proc, pid, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (_hook == IntPtr.Zero) Log.Write("desktop: SetWinEventHook failed");
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild,
        uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero || _progman == IntPtr.Zero) return;
        // Only the desktop stack matters: Progman itself (REORDER) or one of its children.
        if (hwnd != _progman && GetAncestor(hwnd, GA_PARENT) != _progman) return;
        if (_pending) return;
        // Explorer builds the new layer over a burst of events; fix once the burst settles.
        _pending = true;
        _timer.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _pending = false; Fix(); });
    }

    private void Fix()
    {
        if (_progman == IntPtr.Zero) return;

        // Children enumerate top → bottom. Anything of ours-to-protect below a WorkerW is buried.
        IntPtr defView = IntPtr.Zero;
        var sawWorkerW = false;
        List<IntPtr>? buried = null;
        for (var h = GetWindow(_progman, GW_CHILD); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
        {
            var cls = ClassOf(h);
            if (cls == "SHELLDLL_DefView") { defView = h; continue; }
            if (cls == "WorkerW") { sawWorkerW = true; continue; }
            if (!sawWorkerW || !IsWindowVisible(h)) continue;
            GetWindowThreadProcessId(h, out var pid);
            if (pid != 0 && pid != _explorerPid) (buried ??= new()).Add(h);
        }
        if (defView == IntPtr.Zero || buried == null) return;

        // Each insert lands directly under the icons; go bottom-up to keep their relative order.
        for (var i = buried.Count - 1; i >= 0; i--)
            SetWindowPos(buried[i], defView, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        Log.Write($"desktop: raised {buried.Count} live-wallpaper window(s) above the static picture");
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    public void Dispose() => Stop();
}
