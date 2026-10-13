using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using LibreHardwareMonitor.PawnIo;

namespace CoolerDisplay;

internal sealed record Options(
    string Command,
    bool Fahrenheit,
    bool Load,
    int IntervalMs,
    string? LogPath,
    int Seconds,
    int TestTop,
    int TestBottom);

internal static class Program
{
    private const string Usage = """
        CoolerDisplay - drives the 1A2C:4984 CPU cooler display (replaces "CPU TEMP Monitor").

        Usage: CoolerDisplay [command] [options]

        Commands:
          run                 (default) push CPU temperature + power/load to the display every interval
          once                read sensors, print them, send one frame
          sensors             list every CPU sensor LibreHardwareMonitor sees, plus PawnIO status
          devices             list the cooler's HID collections
          test TOP BOTTOM     show fixed numbers (no sensors, no admin needed) for --seconds

        Options:
          -f, --fahrenheit    top row in °F (default °C)
          -l, --load          bottom row is CPU load % (default package power W)
          --interval MS       refresh interval, default 1000, minimum 250
          --seconds N         duration for `test`, default 15
          --log PATH          also append log lines to PATH

        Temperature and power need the PawnIO driver (https://pawnio.eu, `winget install namazso.PawnIO`)
        and an elevated process. The display itself needs neither.
        """;

    private static string? s_logPath;

    public static int Main(string[] args)
    {
        Options? options = Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        s_logPath = options.LogPath;
        try
        {
            return options.Command switch
            {
                "run" => Run(options),
                "once" => Once(options),
                "sensors" => ListSensors(),
                "devices" => ListDevices(),
                "test" => Test(options),
                _ => throw new UnreachableException(),
            };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Run(Options options)
    {
        using var single = new Mutex(initiallyOwned: true, @"Global\CoolerDisplay.Run", out bool isOnlyInstance);
        if (!isOnlyInstance)
        {
            Log("another CoolerDisplay instance is already driving the display; exiting");
            return 1;
        }

        RotateLog();
        Log($"starting: top={(options.Fahrenheit ? "°F" : "°C")} bottom={(options.Load ? "load %" : "power W")} interval={options.IntervalMs}ms");
        WarnIfSensorsUnavailable();

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        using var sensors = new CpuSensors();
        Log($"CPU: {sensors.CpuName}");

        CoolerDevice? device = null;
        DateTime nextOpenAttempt = DateTime.MinValue;
        bool reportedMissingDevice = false;
        bool reportedMissingTemperature = false;

        try
        {
            while (!stop.IsCancellationRequested)
            {
                CpuReading reading = sensors.Read();
                if ((reading.TemperatureC is null) != reportedMissingTemperature)
                {
                    reportedMissingTemperature = !reportedMissingTemperature;
                    Log(reportedMissingTemperature
                        ? "CPU temperature unreadable (PawnIO missing or not elevated?); showing 0"
                        : "CPU temperature readable again");
                }

                if (device is null && DateTime.UtcNow >= nextOpenAttempt)
                {
                    device = TryOpenDevice();
                    if (device is null)
                    {
                        if (!reportedMissingDevice) Log("cooler display (1A2C:4984) not found; retrying every 5 s");
                        reportedMissingDevice = true;
                        nextOpenAttempt = DateTime.UtcNow.AddSeconds(5);
                    }
                    else
                    {
                        Log($"display connected: {device.Path}");
                        reportedMissingDevice = false;
                    }
                }

                if (device is not null)
                {
                    try
                    {
                        device.Send(BuildFrame(reading, options));
                    }
                    catch (Win32Exception ex)
                    {
                        // Unplugged, or the USB stack reset across sleep/resume: reopen on a later tick.
                        Log($"send failed ({ex.Message}); reconnecting");
                        device.Dispose();
                        device = null;
                        nextOpenAttempt = DateTime.UtcNow.AddSeconds(2);
                    }
                }

                stop.Token.WaitHandle.WaitOne(options.IntervalMs);
            }
        }
        finally
        {
            device?.Dispose();
        }

        Log("stopped");
        return 0;
    }

    private static int Once(Options options)
    {
        WarnIfSensorsUnavailable();
        using var sensors = new CpuSensors();
        sensors.Read(); // prime the power/load deltas
        Thread.Sleep(1000);
        CpuReading reading = sensors.Read();
        byte[] frame = BuildFrame(reading, options);

        Console.WriteLine($"CPU:   {sensors.CpuName}");
        Console.WriteLine($"Temp:  {Format(reading.TemperatureC, "°C")}");
        Console.WriteLine($"Power: {Format(reading.PackageWatts, "W")}");
        Console.WriteLine($"Load:  {Format(reading.LoadPercent, "%")}");
        Console.WriteLine($"Frame: {Convert.ToHexString(frame, 0, 8)}");

        using CoolerDevice? device = TryOpenDevice();
        if (device is null)
        {
            Console.WriteLine("Display not found; frame not sent.");
            return 1;
        }

        device.Send(frame);
        Console.WriteLine($"Sent to {device.Path}");
        return 0;
    }

    private static int ListSensors()
    {
        Console.WriteLine($"Elevated: {IsElevated()}");
        Console.WriteLine($"PawnIO:   installed={PawnIo.IsInstalled} version={PawnIo.Version}");
        using var sensors = new CpuSensors();
        sensors.Read();
        Thread.Sleep(1000);
        sensors.Read();
        Console.WriteLine($"CPU:      {sensors.CpuName}");
        foreach (var sensor in sensors.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Name, StringComparer.Ordinal))
            Console.WriteLine($"  {sensor.SensorType,-12} {sensor.Name,-28} {sensor.Value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "-"}");
        return 0;
    }

    private static int ListDevices()
    {
        List<HidCollection> collections = Hid.Enumerate(CoolerDevice.PathFilter);
        if (collections.Count == 0)
        {
            Console.WriteLine("No 1A2C:4984 device present.");
            return 1;
        }

        foreach (HidCollection c in collections)
        {
            bool isDisplay = c.UsagePage == 0xFF01 && c.FeatureReportLength == CoolerDevice.ReportLength;
            Console.WriteLine($"{(isDisplay ? "*" : " ")} page=0x{c.UsagePage:X4} usage=0x{c.Usage:X4} in={c.InputReportLength} out={c.OutputReportLength} feature={c.FeatureReportLength}  {c.Path}");
        }

        Console.WriteLine("* = display collection used by CoolerDisplay");
        return 0;
    }

    private static int Test(Options options)
    {
        using CoolerDevice device = TryOpenDevice() ?? throw new InvalidOperationException("Display not found.");
        byte[] frame = CoolerDevice.BuildReport(options.TestTop, options.TestBottom, options.Fahrenheit, options.Load);
        Console.WriteLine($"Showing {options.TestTop} / {options.TestBottom} for {options.Seconds}s on {device.Path}");
        Console.WriteLine($"Frame: {Convert.ToHexString(frame, 0, 8)}");

        // Resend quickly so another program writing ~1/s (the vendor app) cannot hold the display.
        DateTime until = DateTime.UtcNow.AddSeconds(options.Seconds);
        while (DateTime.UtcNow < until)
        {
            device.Send(frame);
            Thread.Sleep(100);
        }

        return 0;
    }

    private static byte[] BuildFrame(CpuReading reading, Options options)
    {
        int top = reading.TemperatureC is float c ? Round(options.Fahrenheit ? c * 9 / 5 + 32 : c) : 0;
        float? bottomValue = options.Load ? reading.LoadPercent : reading.PackageWatts;
        int bottom = bottomValue is float b ? Round(b) : 0;
        return CoolerDevice.BuildReport(top, bottom, options.Fahrenheit, options.Load);
    }

    private static CoolerDevice? TryOpenDevice()
    {
        try
        {
            return CoolerDevice.TryOpen();
        }
        catch (Win32Exception ex)
        {
            Log($"cannot open display: {ex.Message}");
            return null;
        }
    }

    private static void WarnIfSensorsUnavailable()
    {
        if (!PawnIo.IsInstalled) Log("warning: PawnIO driver is not installed; temperature/power will read 0 (winget install namazso.PawnIO)");
        if (!IsElevated()) Log("warning: not elevated; temperature/power need administrator rights");
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int Round(float value) => (int)MathF.Round(value, MidpointRounding.AwayFromZero);

    private static string Format(float? value, string unit) =>
        value is float v ? $"{v.ToString("0.0", CultureInfo.InvariantCulture)} {unit}" : "unavailable";

    private static void Log(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        Console.WriteLine(line);
        if (s_logPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(s_logPath))!);
            File.AppendAllText(s_logPath, line + Environment.NewLine);
        }
        catch (IOException)
        {
            // Logging must never take the display down.
        }
    }

    // Keep one previous log; the service logs only state changes, so 1 MB covers a long time.
    private static void RotateLog()
    {
        if (s_logPath is null || !File.Exists(s_logPath) || new FileInfo(s_logPath).Length < 1_000_000) return;
        File.Move(s_logPath, s_logPath + ".1", overwrite: true);
    }

    private static Options? Parse(string[] args)
    {
        string command = "run";
        bool fahrenheit = false, load = false, commandSeen = false;
        int intervalMs = 1000, seconds = 15, testTop = 0, testBottom = 0;
        string? logPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-f" or "--fahrenheit": fahrenheit = true; break;
                case "-l" or "--load": load = true; break;
                case "--interval" when TryInt(args, ++i, out intervalMs) && intervalMs >= 250: break;
                case "--seconds" when TryInt(args, ++i, out seconds) && seconds > 0: break;
                case "--log" when i + 1 < args.Length: logPath = args[++i]; break;
                case "run" or "once" or "sensors" or "devices" when !commandSeen:
                    command = arg;
                    commandSeen = true;
                    break;
                case "test" when !commandSeen && TryInt(args, i + 1, out testTop) && TryInt(args, i + 2, out testBottom):
                    command = arg;
                    commandSeen = true;
                    i += 2;
                    break;
                default: return null; // includes -h / --help
            }
        }

        return new Options(command, fahrenheit, load, intervalMs, logPath, seconds, testTop, testBottom);
    }

    private static bool TryInt(string[] args, int index, out int value)
    {
        value = 0;
        return index < args.Length && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
