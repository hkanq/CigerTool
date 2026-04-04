using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CigerTool.App;

public partial class MainWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;
    private const double WindowPadding = 12;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_OnSourceInitialized(object? sender, EventArgs e)
    {
        ClampToWorkingArea();
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        ClampToWorkingArea();
    }

    private void ClampToWorkingArea()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                ClampWithPrimaryWorkArea(SystemParameters.WorkArea);
                return;
            }

            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                ClampWithPrimaryWorkArea(SystemParameters.WorkArea);
                return;
            }

            var info = new MonitorInfo();
            info.Size = Marshal.SizeOf<MonitorInfo>();

            if (!GetMonitorInfo(monitor, ref info))
            {
                ClampWithPrimaryWorkArea(SystemParameters.WorkArea);
                return;
            }

            var source = PresentationSource.FromVisual(this);
            var transformFromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var workArea = Rect.Transform(
                new Rect(
                    info.WorkArea.Left,
                    info.WorkArea.Top,
                    info.WorkArea.Right - info.WorkArea.Left,
                    info.WorkArea.Bottom - info.WorkArea.Top),
                transformFromDevice);

            ClampWithPrimaryWorkArea(workArea);
        }
        catch
        {
            ClampWithPrimaryWorkArea(SystemParameters.WorkArea);
        }
    }

    private void ClampWithPrimaryWorkArea(Rect workArea)
    {
        var availableWidth = Math.Max(MinWidth, workArea.Width - (WindowPadding * 2));
        var availableHeight = Math.Max(MinHeight, workArea.Height - (WindowPadding * 2));

        if (Width > availableWidth)
        {
            Width = availableWidth;
        }

        if (Height > availableHeight)
        {
            Height = availableHeight;
        }

        MaxWidth = availableWidth;
        MaxHeight = availableHeight;

        var preferredLeft = workArea.Left + Math.Max(WindowPadding, (workArea.Width - Width) / 2);
        var preferredTop = workArea.Top + Math.Max(WindowPadding, (workArea.Height - Height) / 2);

        Left = Math.Max(workArea.Left + WindowPadding, preferredLeft);
        Top = Math.Max(workArea.Top + WindowPadding, preferredTop);

        if (Left + Width > workArea.Right - WindowPadding)
        {
            Left = Math.Max(workArea.Left + WindowPadding, workArea.Right - Width - WindowPadding);
        }

        if (Top + Height > workArea.Bottom - WindowPadding)
        {
            Top = Math.Max(workArea.Top + WindowPadding, workArea.Bottom - Height - WindowPadding);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RectInt MonitorArea;
        public RectInt WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectInt
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
