using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GeneralMaintenanceManager.App.Services;

/// <summary>
/// Keeps Maintenance Manager windows inside the current monitor's usable work area.
/// This is especially important on smaller displays and when Windows DPI scaling
/// reduces the number of available WPF device-independent pixels.
/// </summary>
internal static class ResponsiveWindowSizing
{
    private const uint MonitorDefaultToNearest = 2;
    private const double WorkAreaMargin = 12;
    private static readonly Lazy<NativeApi?> User32Api = new(ResolveNativeApi);

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            FitToWorkArea(window);
            return;
        }

        window.SourceInitialized += WindowOnSourceInitialized;
    }

    private static void WindowOnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.SourceInitialized -= WindowOnSourceInitialized;
        FitToWorkArea(window);
    }

    private static void FitToWorkArea(Window window)
    {
        Rect workArea = GetCurrentMonitorWorkArea(window);
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            workArea = SystemParameters.WorkArea;
        }

        double availableWidth = Math.Max(1, workArea.Width - (WorkAreaMargin * 2));
        double availableHeight = Math.Max(1, workArea.Height - (WorkAreaMargin * 2));

        // A XAML minimum must never force the window beyond a smaller/scaled screen.
        window.MinWidth = Math.Min(window.MinWidth, availableWidth);
        window.MinHeight = Math.Min(window.MinHeight, availableHeight);

        double preferredWidth = ResolvePreferredDimension(window.Width, window.ActualWidth, window.MinWidth);
        double preferredHeight = ResolvePreferredDimension(window.Height, window.ActualHeight, window.MinHeight);

        window.Width = Math.Clamp(preferredWidth, window.MinWidth, availableWidth);
        window.Height = Math.Clamp(preferredHeight, window.MinHeight, availableHeight);

        double desiredLeft;
        double desiredTop;

        if (window.Owner is { IsVisible: true } owner)
        {
            desiredLeft = owner.Left + ((owner.ActualWidth - window.Width) / 2);
            desiredTop = owner.Top + ((owner.ActualHeight - window.Height) / 2);
        }
        else
        {
            desiredLeft = workArea.Left + ((workArea.Width - window.Width) / 2);
            desiredTop = workArea.Top + ((workArea.Height - window.Height) / 2);
        }

        double minimumLeft = workArea.Left + WorkAreaMargin;
        double minimumTop = workArea.Top + WorkAreaMargin;
        double maximumLeft = workArea.Right - WorkAreaMargin - window.Width;
        double maximumTop = workArea.Bottom - WorkAreaMargin - window.Height;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = ClampPosition(desiredLeft, minimumLeft, maximumLeft);
        window.Top = ClampPosition(desiredTop, minimumTop, maximumTop);
    }

    private static double ResolvePreferredDimension(double configured, double actual, double minimum)
    {
        if (!double.IsNaN(configured) && !double.IsInfinity(configured) && configured > 0)
        {
            return configured;
        }

        if (!double.IsNaN(actual) && !double.IsInfinity(actual) && actual > 0)
        {
            return actual;
        }

        return Math.Max(1, minimum);
    }

    private static double ClampPosition(double value, double minimum, double maximum)
    {
        // On extremely small work areas the fitted window can consume the entire usable
        // span, making maximum == minimum (or differ only by floating-point rounding).
        return maximum <= minimum ? minimum : Math.Clamp(value, minimum, maximum);
    }

    private static Rect GetCurrentMonitorWorkArea(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        NativeApi? api = User32Api.Value;
        if (handle == IntPtr.Zero || api is null)
        {
            return Rect.Empty;
        }

        IntPtr monitor = api.MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return Rect.Empty;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };

        if (!api.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return Rect.Empty;
        }

        HwndSource? source = HwndSource.FromHwnd(handle);
        CompositionTarget? compositionTarget = source?.CompositionTarget;
        if (compositionTarget is null)
        {
            return Rect.Empty;
        }

        Matrix fromDevice = compositionTarget.TransformFromDevice;
        Point topLeft = fromDevice.Transform(new Point(monitorInfo.Work.Left, monitorInfo.Work.Top));
        Point bottomRight = fromDevice.Transform(new Point(monitorInfo.Work.Right, monitorInfo.Work.Bottom));

        return new Rect(topLeft, bottomRight);
    }

    private static NativeApi? ResolveNativeApi()
    {
        if (!NativeLibrary.TryLoad(
                "user32.dll",
                typeof(ResponsiveWindowSizing).Assembly,
                DllImportSearchPath.System32,
                out IntPtr libraryHandle))
        {
            return null;
        }

        if (!NativeLibrary.TryGetExport(libraryHandle, "MonitorFromWindow", out IntPtr monitorFromWindowPointer)
            || !NativeLibrary.TryGetExport(libraryHandle, "GetMonitorInfoW", out IntPtr getMonitorInfoPointer))
        {
            NativeLibrary.Free(libraryHandle);
            return null;
        }

        MonitorFromWindowDelegate monitorFromWindow =
            Marshal.GetDelegateForFunctionPointer<MonitorFromWindowDelegate>(monitorFromWindowPointer);
        GetMonitorInfoDelegate getMonitorInfo =
            Marshal.GetDelegateForFunctionPointer<GetMonitorInfoDelegate>(getMonitorInfoPointer);

        return new NativeApi(libraryHandle, monitorFromWindow, getMonitorInfo);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr MonitorFromWindowDelegate(IntPtr windowHandle, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool GetMonitorInfoDelegate(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    private sealed class NativeApi
    {
        private readonly IntPtr _libraryHandle;
        private readonly MonitorFromWindowDelegate _monitorFromWindow;
        private readonly GetMonitorInfoDelegate _getMonitorInfo;

        public NativeApi(
            IntPtr libraryHandle,
            MonitorFromWindowDelegate monitorFromWindow,
            GetMonitorInfoDelegate getMonitorInfo)
        {
            _libraryHandle = libraryHandle;
            _monitorFromWindow = monitorFromWindow;
            _getMonitorInfo = getMonitorInfo;
        }

        public IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags)
        {
            return _libraryHandle == IntPtr.Zero
                ? IntPtr.Zero
                : _monitorFromWindow(windowHandle, flags);
        }

        public bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo)
        {
            return _libraryHandle != IntPtr.Zero && _getMonitorInfo(monitorHandle, ref monitorInfo);
        }
    }
}
