using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace ClassroomControl.Platform;

/// <summary>Mouse and keyboard injection with SendInput. Cannot control elevated (administrator) windows - a Windows restriction.</summary>
[SupportedOSPlatform("windows")]
public sealed class SendInputInjector : IInputInjector
{
    private const int AbsoluteRange = 65_535;

    public void Inject(MouseEventMessage m)
    {
        var x = (int)Math.Round(m.X * AbsoluteRange);
        var y = (int)Math.Round(m.Y * AbsoluteRange);
        var move = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE;

        switch (m.Action)
        {
            case MouseAction.Move:
                Send(Mouse(x, y, move, 0));
                break;
            case MouseAction.Down or MouseAction.Up:
                var down = m.Action == MouseAction.Down;
                var flag = (m.Button, down) switch
                {
                    (MouseButtonKind.Left, true) => Native.MOUSEEVENTF_LEFTDOWN,
                    (MouseButtonKind.Left, false) => Native.MOUSEEVENTF_LEFTUP,
                    (MouseButtonKind.Right, true) => Native.MOUSEEVENTF_RIGHTDOWN,
                    (MouseButtonKind.Right, false) => Native.MOUSEEVENTF_RIGHTUP,
                    (MouseButtonKind.Middle, true) => Native.MOUSEEVENTF_MIDDLEDOWN,
                    (MouseButtonKind.Middle, false) => Native.MOUSEEVENTF_MIDDLEUP,
                    _ => 0u,
                };
                if (flag == 0) return;
                Send(Mouse(x, y, move, 0), Mouse(x, y, flag | Native.MOUSEEVENTF_ABSOLUTE, 0));
                break;
            case MouseAction.Wheel:
                Send(Mouse(x, y, Native.MOUSEEVENTF_WHEEL, unchecked((uint)m.WheelDelta)));
                break;
        }
    }

    public void Inject(KeyboardEventMessage k)
    {
        var flags = (k.IsDown ? 0u : Native.KEYEVENTF_KEYUP) | (k.IsExtended ? Native.KEYEVENTF_EXTENDEDKEY : 0u);
        var input = new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = (ushort)k.VirtualKey, wScan = (ushort)Native.MapVirtualKey((uint)k.VirtualKey, 0), dwFlags = flags } },
        };
        Send(input);
    }

    private static Native.INPUT Mouse(int x, int y, uint flags, uint data) => new()
    {
        type = Native.INPUT_MOUSE,
        u = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = x, dy = y, dwFlags = flags, mouseData = data } },
    };

    private static void Send(params Native.INPUT[] inputs)
    {
        if (Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>()) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}

/// <summary>Restart / shutdown through the standard Windows shutdown.exe (with a countdown the student sees), and application control
/// limited to the student's own session.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemControl : ISystemControl
{
    private const int MaxReason = 100;
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "winlogon", "csrss", "lsass", "services", "svchost", "smss", "wininit", "system", "dwm", "fontdrvhost", "sihost", "ctfmon",
        "ClassroomControl.Student", "ClassroomControl.Teacher",
    };
    private readonly ILogger<WindowsSystemControl> _logger;

    public WindowsSystemControl(ILogger<WindowsSystemControl> logger) => _logger = logger;

    public void Restart(int delaySeconds, string? reason) => Power("/r", delaySeconds, reason);
    public void Shutdown(int delaySeconds, string? reason) => Power("/s", delaySeconds, reason);

    private void Power(string mode, int delaySeconds, string? reason)
    {
        var comment = string.IsNullOrWhiteSpace(reason) ? "O‘qituvchi buyrug‘i" : new string(reason.Where(c => !char.IsControl(c) && c != '"').Take(MaxReason).ToArray());
        var info = new ProcessStartInfo("shutdown.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { mode, "/t", delaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "/c", comment }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info);
        _logger.LogWarning("Windows {Mode} scheduled in {Delay} s on the Teacher's request.", mode, delaySeconds);
    }

    public StartApplicationResult StartApplication(string target, string? arguments)
    {
        try
        {
            var info = new ProcessStartInfo(target) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(arguments)) info.Arguments = arguments;
            using var process = Process.Start(info);
            _logger.LogInformation("Application started on the Teacher's request: {Target}", target);
            return new StartApplicationResult(true, "Application started");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new StartApplicationResult(false, "Dasturni ishga tushirib bo‘lmadi: " + ex.Message);
        }
    }

    public int StopApplication(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        if (Protected.Contains(name)) return 0;
        var session = Process.GetCurrentProcess().SessionId;
        var stopped = 0;
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session) continue;
                    if (!process.CloseMainWindow() || !process.WaitForExit(2000)) process.Kill();
                    stopped++;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    _logger.LogDebug(ex, "Process {Name} could not be stopped.", name);
                }
            }
        }
        if (stopped > 0) _logger.LogInformation("{Count} process(es) of {Name} stopped on the Teacher's request.", stopped, name);
        return stopped;
    }
}

/// <summary>Swallows the keys a student could use to hide a lock screen (Windows key, Alt+Tab, Alt+F4, Ctrl+Esc). Ctrl+Alt+Delete is
/// handled by Windows itself and always works, so the student can still reach Task Manager. Injected (remote control) keys pass.</summary>
[SupportedOSPlatform("windows")]
public sealed class KeyboardBlocker : IDisposable
{
    private const int VkTab = 0x09, VkEscape = 0x1B, VkF4 = 0x73, VkLWin = 0x5B, VkRWin = 0x5C, VkControl = 0x11;
    private readonly Native.LowLevelKeyboardProc _callback;
    private IntPtr _hook;

    public KeyboardBlocker() => _callback = Hook;

    public bool IsEnabled => _hook != IntPtr.Zero;

    public void Enable()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _callback, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Disable()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            var injected = (key.flags & Native.LLKHF_INJECTED) != 0;
            var alt = (key.flags & Native.LLKHF_ALTDOWN) != 0;
            var ctrl = (Native.GetAsyncKeyState(VkControl) & 0x8000) != 0;
            var vk = (int)key.vkCode;
            if (!injected && (vk is VkLWin or VkRWin || (alt && vk is VkTab or VkF4) || (ctrl && vk == VkEscape) || (alt && vk == VkEscape)))
                return 1;
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => Disable();
}

/// <summary>CPU and memory use for the Teacher's grid.</summary>
public sealed class SystemMetrics : ISystemMetrics
{
    private readonly object _gate = new();
    private ulong _lastIdle, _lastTotal;
    private long _lastCpuTicks;
    private int _lastCpu;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    public SystemSnapshot Read()
    {
        lock (_gate)
        {
            var cpu = ReadCpu();
            var (total, used) = ReadMemory();
            return new SystemSnapshot(cpu, total, used);
        }
    }

    private int ReadCpu()
    {
        if (Stopwatch.GetElapsedTime(_lastCpuTicks) < MinInterval && _lastCpuTicks != 0) return _lastCpu;
        ulong idle, total;
        if (OperatingSystem.IsWindows())
        {
            if (!Native.GetSystemTimes(out var i, out var k, out var u)) return _lastCpu;
            idle = i.Value;
            total = k.Value + u.Value; // kernel time already includes idle time
        }
        else if (File.Exists("/proc/stat"))
        {
            var parts = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(ulong.Parse).ToArray();
            idle = parts[3] + (parts.Length > 4 ? parts[4] : 0);
            total = parts.Aggregate(0UL, (a, b) => a + b);
        }
        else
        {
            return 0;
        }

        if (_lastTotal != 0 && total > _lastTotal)
        {
            var busy = 1.0 - (double)(idle - _lastIdle) / (total - _lastTotal);
            _lastCpu = (int)Math.Clamp(Math.Round(busy * 100), 0, 100);
        }
        _lastIdle = idle;
        _lastTotal = total;
        _lastCpuTicks = Stopwatch.GetTimestamp();
        return _lastCpu;
    }

    private static (long Total, long Used) ReadMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
            return Native.GlobalMemoryStatusEx(ref status) ? ((long)status.ullTotalPhys, (long)(status.ullTotalPhys - status.ullAvailPhys)) : (0, 0);
        }
        if (File.Exists("/proc/meminfo"))
        {
            long Kb(string key) => File.ReadLines("/proc/meminfo").Where(l => l.StartsWith(key, StringComparison.Ordinal))
                .Select(l => long.Parse(l.Split(':')[1].Trim().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault() * 1024;
            var total = Kb("MemTotal");
            return (total, total - Kb("MemAvailable"));
        }
        return (0, 0);
    }
}
