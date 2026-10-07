using Microsoft.Win32;

namespace ClassroomControl.StudentAgent.Services;

public interface IRunKeyStore
{
    string? Get(string name);
    void Set(string name, string command);
    void Remove(string name);
}

/// <summary>Per-user "Run" key (HKCU). Needs no administrator rights and is visible/removable by the user.</summary>
public sealed class WindowsRunKeyStore : IRunKeyStore
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Get(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Registry.CurrentUser.OpenSubKey(RunPath);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string command)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Start with Windows requires Windows.");
        using var key = Registry.CurrentUser.CreateSubKey(RunPath);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public void Remove(string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

public interface IExecutablePathProvider
{
    string Path { get; }
}

public sealed class ProcessExecutablePathProvider : IExecutablePathProvider
{
    public string Path => Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unknown.");
}

public interface IStartupService
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

public sealed class StartupService : IStartupService
{
    public const string RunValueName = "ClassroomControl.StudentAgent";
    public const string MinimizedArgument = "--minimized";

    private readonly IRunKeyStore _store;
    private readonly IExecutablePathProvider _path;

    public StartupService(IRunKeyStore store, IExecutablePathProvider path)
    {
        _store = store;
        _path = path;
    }

    public bool IsEnabled => !string.IsNullOrEmpty(_store.Get(RunValueName));

    public void SetEnabled(bool enabled)
    {
        if (enabled) _store.Set(RunValueName, $"\"{_path.Path}\" {MinimizedArgument}");
        else _store.Remove(RunValueName);
    }
}
