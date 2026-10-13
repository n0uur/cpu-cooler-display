using Microsoft.Win32.SafeHandles;

namespace CoolerDisplay;

/// <summary>
/// The cooler's two-row 7-segment display. It enumerates as a USB "keyboard" (VID 1A2C, PID 4984);
/// the display itself is the vendor-defined collection (usage page 0xFF01) with a 64-byte feature report.
///
/// Feature report 0x07, 64 bytes:
///   [0]    = 0x07 (report ID)
///   [1..3] = top row digits (hundreds, tens, ones) as raw values 0-9, not ASCII
///   [4]    = flags: bit0 = top row is °F (else °C), bit4 = bottom row is load % (else W)
///   [5..7] = bottom row digits
///   [8..]  = 0
/// </summary>
internal sealed class CoolerDevice : IDisposable
{
    public const string PathFilter = "vid_1a2c&pid_4984";
    public const int ReportLength = 64;
    private const ushort DisplayUsagePage = 0xFF01;
    private const byte ReportId = 0x07;
    private const byte FlagFahrenheit = 0x01;
    private const byte FlagLoadPercent = 0x10;

    private readonly SafeFileHandle _handle;

    private CoolerDevice(string path, SafeFileHandle handle)
    {
        Path = path;
        _handle = handle;
    }

    public string Path { get; }

    /// <summary>Opens the display collection, or returns null when the cooler is not connected.</summary>
    public static CoolerDevice? TryOpen()
    {
        HidCollection? display = Hid.Enumerate(PathFilter)
            .FirstOrDefault(c => c.UsagePage == DisplayUsagePage && c.FeatureReportLength == ReportLength);
        return display is null ? null : new CoolerDevice(display.Path, Hid.OpenReadWrite(display.Path));
    }

    public void Send(byte[] report) => Hid.SetFeature(_handle, report);

    /// <summary>Builds a display frame. Values are clamped to 0-999 and shown as-is (no unit conversion).</summary>
    public static byte[] BuildReport(int top, int bottom, bool topIsFahrenheit, bool bottomIsLoad)
    {
        var report = new byte[ReportLength];
        report[0] = ReportId;
        WriteDigits(report, 1, top);
        report[4] = (byte)((topIsFahrenheit ? FlagFahrenheit : 0) | (bottomIsLoad ? FlagLoadPercent : 0));
        WriteDigits(report, 5, bottom);
        return report;
    }

    private static void WriteDigits(byte[] report, int offset, int value)
    {
        int clamped = Math.Clamp(value, 0, 999);
        report[offset] = (byte)(clamped / 100);
        report[offset + 1] = (byte)(clamped / 10 % 10);
        report[offset + 2] = (byte)(clamped % 10);
    }

    public void Dispose() => _handle.Dispose();
}
