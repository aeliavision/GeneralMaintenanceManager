using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GeneralMaintenanceManager.App.Services;

internal static class NativeWindowAppearance
{
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private static readonly Lazy<NativeApi?> DwmApi = new(ResolveNativeApi);

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (SystemParameters.HighContrast || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Apply(window);
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
        Apply(window);
    }

    private static void Apply(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        NativeApi? api = DwmApi.Value;
        if (api is null)
        {
            return;
        }

        int? captionColor = ResolveColorRef("PrimaryBrush");
        int? textColor = ResolveColorRef("OnPrimaryBrush");
        if (!captionColor.HasValue || !textColor.HasValue)
        {
            return;
        }

        if (!SetWindowAttribute(api, handle, DwmwaCaptionColor, captionColor.Value))
        {
            return;
        }

        SetWindowAttribute(api, handle, DwmwaTextColor, textColor.Value);
    }

    private static bool SetWindowAttribute(NativeApi api, IntPtr handle, int attribute, int value)
    {
        int result = api.SetWindowAttribute(handle, attribute, ref value, sizeof(int));
        return result >= 0;
    }

    private static int? ResolveColorRef(string resourceKey)
    {
        if (Application.Current?.TryFindResource(resourceKey) is not SolidColorBrush brush)
        {
            return null;
        }

        Color color = brush.Color;
        return color.R | (color.G << 8) | (color.B << 16);
    }

    private static NativeApi? ResolveNativeApi()
    {
        if (!NativeLibrary.TryLoad(
                "dwmapi.dll",
                typeof(NativeWindowAppearance).Assembly,
                DllImportSearchPath.System32,
                out IntPtr libraryHandle))
        {
            return null;
        }

        if (!NativeLibrary.TryGetExport(libraryHandle, "DwmSetWindowAttribute", out IntPtr functionPointer))
        {
            NativeLibrary.Free(libraryHandle);
            return null;
        }

        DwmSetWindowAttributeDelegate setWindowAttribute =
            Marshal.GetDelegateForFunctionPointer<DwmSetWindowAttributeDelegate>(functionPointer);

        return new NativeApi(libraryHandle, setWindowAttribute);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DwmSetWindowAttributeDelegate(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    private sealed class NativeApi
    {
        private readonly IntPtr _libraryHandle;
        private readonly DwmSetWindowAttributeDelegate _setWindowAttribute;

        public NativeApi(IntPtr libraryHandle, DwmSetWindowAttributeDelegate setWindowAttribute)
        {
            _libraryHandle = libraryHandle;
            _setWindowAttribute = setWindowAttribute;
        }

        public int SetWindowAttribute(
            IntPtr windowHandle,
            int attribute,
            ref int attributeValue,
            int attributeSize)
        {
            // The loaded system module is intentionally held for the application lifetime.
            // Referencing the handle here also makes that ownership explicit to analyzers.
            if (_libraryHandle == IntPtr.Zero)
            {
                return -1;
            }

            return _setWindowAttribute(windowHandle, attribute, ref attributeValue, attributeSize);
        }
    }
}
