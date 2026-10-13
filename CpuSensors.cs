using LibreHardwareMonitor.Hardware;

namespace CoolerDisplay;

internal readonly record struct CpuReading(float? TemperatureC, float? PackageWatts, float? LoadPercent);

/// <summary>
/// CPU temperature, package power and load via LibreHardwareMonitor 0.9.6, which accesses hardware through the
/// signed PawnIO driver (no WinRing0). Temperature and power need PawnIO installed and an elevated process.
/// Power and load are deltas between two updates, so the first <see cref="Read"/> after opening may be empty.
/// </summary>
internal sealed class CpuSensors : IDisposable
{
    // First match wins. AMD Zen reports "Core (Tctl/Tdie)"; Intel reports "CPU Package".
    private static readonly string[] TemperatureNames = ["Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Core (Tdie)"];

    private readonly Computer _computer;
    private readonly IHardware _cpu;

    public CpuSensors()
    {
        _computer = new Computer { IsCpuEnabled = true };
        _computer.Open();
        _cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu)
               ?? throw new InvalidOperationException("LibreHardwareMonitor found no CPU.");
    }

    public string CpuName => _cpu.Name;

    public IReadOnlyList<ISensor> Sensors => _cpu.Sensors;

    public CpuReading Read()
    {
        _cpu.Update();
        return new CpuReading(PickTemperature(), Value(SensorType.Power, "Package"), Value(SensorType.Load, "CPU Total"));
    }

    private float? PickTemperature()
    {
        foreach (string name in TemperatureNames)
        {
            float? value = Value(SensorType.Temperature, name);
            if (value is not null) return value;
        }

        // Unknown CPU naming: fall back to the hottest valid temperature sensor.
        return _cpu.Sensors
            .Where(s => s.SensorType == SensorType.Temperature)
            .Select(s => Valid(s.SensorType, s.Value))
            .Where(v => v is not null)
            .Max();
    }

    private float? Value(SensorType type, string name) =>
        Valid(type, _cpu.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name == name)?.Value);

    // Unreadable sensors come back as null or NaN. A temperature of exactly 0 also means "no driver access",
    // while 0 W / 0 % are legitimate readings.
    private static float? Valid(SensorType type, float? value) =>
        value is float v && float.IsFinite(v) && (type != SensorType.Temperature || v > 0) ? v : null;

    public void Dispose() => _computer.Close();
}
