using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VocaLink.Infrastructure.AutoACE;

/// <summary>只管理 ACE 主窗口的可见性；不驱动工程内容，也不反复压回用户打开的窗口。</summary>
public static class AceWindowVisibility
{
    public static bool HasOpenEditor() => FindMainWindow() != IntPtr.Zero;

    public static bool BringToFront()
    {
        var mainWindow = FindMainWindow();
        if (mainWindow == IntPtr.Zero) return false;
        ShowWindowAsync(mainWindow, 9);
        BringWindowToTop(mainWindow);
        SetForegroundWindow(mainWindow);
        return true;
    }

    public static bool EnsureTaskbarWindow(bool minimiseVisible = false)
    {
        var mainWindow = FindMainWindow();
        if (mainWindow == IntPtr.Zero) return false;
        // SW_SHOWMINNOACTIVE：显示为任务栏中的最小化窗口，不抢焦点。
        // 已经可见的 ACE 保持用户当前状态；仅首次启动或修复旧隐藏窗口时最小化。
        if (minimiseVisible || !IsWindowVisible(mainWindow)) ShowWindowAsync(mainWindow, 7);
        return true;
    }

    private static IntPtr FindMainWindow()
    {
        var ids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("ACE Studio"))
        {
            using (process) ids.Add((uint)process.Id);
        }
        var mainWindow = IntPtr.Zero;
        long largestArea = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (!ids.Contains(processId) || GetWindow(window, 4) != IntPtr.Zero) return true;
            if (!IsWindowVisible(window)) return true;
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            if (!GetWindowPlacement(window, ref placement)) return true;
            var width = placement.Normal.Right - placement.Normal.Left;
            var height = placement.Normal.Bottom - placement.Normal.Top;
            var area = (long)width * height;
            // 同名进度窗也可能没有所有者；只选正常尺寸下最大的编辑主窗口。
            if (width >= 600 && height >= 400 && area > largestArea)
            {
                largestArea = area;
                mainWindow = window;
            }
            return true;
        }, IntPtr.Zero);
        return mainWindow;
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length, Flags, Show;
        public Point Min, Max;
        public Rect Normal;
    }
    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);
}
