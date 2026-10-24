using System.Runtime.InteropServices;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace DevOpsClient;

internal static class WindowPlacementStore
{
    private const string FileName = "window-placement.json";

    public static void Restore(Window window, string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize<Placement>(File.ReadAllText(path));
            if (saved == null) return;
            var screen = EnumerateMonitors().FirstOrDefault(s => string.Equals(s.DeviceName, saved.Monitor, StringComparison.OrdinalIgnoreCase));
            if (screen == null) return;
            var area = screen.WorkArea;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.WindowState = WindowState.Normal;
            window.Left = Math.Clamp(saved.Left, area.Left, area.Right - Math.Min(saved.Width, area.Width));
            window.Top = Math.Clamp(saved.Top, area.Top, area.Bottom - Math.Min(saved.Height, area.Height));
            window.Width = Math.Clamp(saved.Width, window.MinWidth, area.Width);
            window.Height = Math.Clamp(saved.Height, window.MinHeight, area.Height);
            window.Loaded += (_, _) => window.WindowState = WindowState.Maximized;
        }
        catch
        {
            // A missing, corrupt, or disconnected monitor placement must never prevent startup.
        }
    }

    public static void Save(Window window, string folder)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var screen = GetMonitor(monitor);
            if (screen == null) return;
            var bounds = window.RestoreBounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) bounds = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            var placement = new Placement(screen.DeviceName, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            var path = Path.Combine(folder, FileName);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(placement, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        catch
        {
            // Window placement is a convenience; a read-only folder must not block shutdown.
        }
    }

    private static MonitorInfo? GetMonitor(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        var native = new MonitorInfoNative { Size = Marshal.SizeOf<MonitorInfoNative>() };
        return GetMonitorInfo(handle, ref native) ? new MonitorInfo(native.DeviceName, native.Work.ToRect()) : null;
    }

    private static IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var monitor = GetMonitor(handle);
            if (monitor != null) monitors.Add(monitor);
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoNative info);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnum callback, IntPtr data);

    private delegate bool MonitorEnum(IntPtr monitor, IntPtr hdc, IntPtr rectangle, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoNative
    {
        public MonitorInfoNative() { Size = 0; Monitor = default; Work = default; Flags = 0; DeviceName = string.Empty; }
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName = "";
    }

    private sealed record MonitorInfo(string DeviceName, Rect WorkArea);
    private sealed record Placement(string Monitor, double Left, double Top, double Width, double Height);
}
