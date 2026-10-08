using System.Reflection;
using System.Runtime.InteropServices;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using ClassroomControl.StudentAgent.Models;
using Microsoft.Win32;

namespace ClassroomControl.StudentAgent.Services;

public interface IDeviceInfoService
{
    /// <summary>Full device details (AgentStatus left as "Unknown"; callers fill the live status).</summary>
    DeviceInfo GetDeviceInfo();
    string DeviceId { get; }
    string ComputerName { get; }
    string LocalIp { get; }
    string AgentVersion { get; }
}

public sealed class DeviceInfoService : IDeviceInfoService
{
    private static readonly string Version = typeof(DeviceInfoService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    private readonly ISettingsService _settings;
    private readonly ILocalNetworkInfo _network;
    private readonly Lazy<string> _cpu = new(ReadCpuName);

    public DeviceInfoService(ISettingsService settings, ILocalNetworkInfo network)
    {
        _settings = settings;
        _network = network;
    }

    public string DeviceId => _settings.Current.DeviceId;

    public string ComputerName
    {
        get
        {
            var custom = _settings.Current.ComputerName;
            return string.IsNullOrWhiteSpace(custom) ? Environment.MachineName : custom.Trim();
        }
    }

    public string LocalIp => _network.GetPrimaryAdapter()?.Address.ToString() ?? "0.0.0.0";

    public string AgentVersion => Version;

    public DeviceInfo GetDeviceInfo()
    {
        var adapter = _network.GetPrimaryAdapter();
        var settings = _settings.Current;
        return new DeviceInfo(
            DeviceId: settings.DeviceId,
            ComputerName: ComputerName,
            WindowsUser: Environment.UserName,
            LocalIp: adapter?.Address.ToString() ?? "0.0.0.0",
            MacAddress: adapter?.MacAddress ?? string.Empty,
            OperatingSystem: RuntimeInformation.OSDescription,
            OsVersion: Environment.OSVersion.Version.ToString(),
            Cpu: _cpu.Value,
            RamBytes: GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            AgentVersion: Version,
            AgentStatus: "Unknown",
            LastConnectionTime: settings.LastConnectionUtc);
    }

    private static string ReadCpuName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
            else if (File.Exists("/proc/cpuinfo"))
            {
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.Ordinal))
                        return line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Falls through to the generic description below.
        }
        return $"{RuntimeInformation.ProcessArchitecture} ({Environment.ProcessorCount} logical processors)";
    }
}
