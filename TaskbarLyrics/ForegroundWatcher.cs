using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarLyrics;

/// <summary>
/// Reports the display name of whatever app currently has focus.
/// Purely event-driven: a WinEvent hook fires only when focus actually moves to
/// another window, so sitting in one app costs literally nothing — no polling,
/// no timer. Name resolution runs off the UI thread; stale lookups are dropped.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    /// <summary>Raised (on a pool thread) only when the resolved name differs from the last one.</summary>
    public event Action<string>? AppChanged;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod,
        WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buf, int max);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr param);

    // The hook stores no managed reference — keep the delegate alive ourselves.
    private readonly WinEventProc _proc;
    private IntPtr _hook;
    private volatile string? _last;
    private int _token;

    public ForegroundWatcher() => _proc = OnWinEvent;

    /// <summary>Current name, or null before the first resolution.</summary>
    public string? Current => _last;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (_hook == IntPtr.Zero) Log.Write("appname: SetWinEventHook failed");
        Resolve(GetForegroundWindow());   // seed with whatever is focused right now
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        _last = null;
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild,
        uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero) return;
        Resolve(hwnd);
    }

    private void Resolve(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        // Reading process metadata can block; never do it on the hook's thread.
        var token = Interlocked.Increment(ref _token);
        _ = Task.Run(() =>
        {
            var name = DescribeWindow(hwnd);
            if (name == null) return;
            if (Volatile.Read(ref _token) != token) return;   // a newer switch already won
            if (name == _last) return;
            _last = name;
            try { AppChanged?.Invoke(name); }
            catch (Exception ex) { Log.Write($"appname: handler threw: {ex.Message}"); }
        });
    }

    private static string? DescribeWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        var cls = ClassOf(hwnd);
        try
        {
            using var proc = Process.GetProcessById((int)pid);

            // The desktop and the shell's own surfaces read as "Desktop" — the
            // rough equivalent of macOS falling back to Finder.
            if (proc.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
                cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                return "Desktop";

            // Store/UWP apps are hosted by ApplicationFrameHost; the app itself
            // owns a child window belonging to a different process.
            if (proc.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                var hosted = HostedChildPid(hwnd, pid);
                if (hosted != 0)
                {
                    using var real = Process.GetProcessById((int)hosted);
                    return FriendlyName(real);
                }
            }

            return FriendlyName(proc);
        }
        catch
        {
            return null;   // process died mid-switch, or is protected/elevated
        }
    }

    /// <summary>The FileDescription ("Google Chrome") if we can read it, else the exe name.</summary>
    private static string FriendlyName(Process proc)
    {
        try
        {
            var desc = proc.MainModule?.FileVersionInfo.FileDescription?.Trim();
            // Some apps (modern Notepad) put the bare filename in there.
            if (desc != null && desc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                desc = desc[..^4].Trim();
            if (!string.IsNullOrEmpty(desc)) return desc;
        }
        catch { /* cross-bitness or access denied — fall through */ }

        var n = proc.ProcessName;
        return n.Length > 0 ? char.ToUpper(n[0]) + n[1..] : n;
    }

    private static uint HostedChildPid(IntPtr host, uint hostPid)
    {
        uint found = 0;
        EnumChildWindows(host, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var cpid);
            if (cpid != 0 && cpid != hostPid) { found = cpid; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    public void Dispose() => Stop();
}
