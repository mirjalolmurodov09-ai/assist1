namespace ClassroomControl.StudentAgent.Infrastructure.Helpers;

public interface IAppPaths
{
    string DataDirectory { get; }
    string SettingsFile { get; }
    string LogDirectory { get; }
    string KeyFile { get; }
}

public sealed class AppPaths : IAppPaths
{
    public AppPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
    }

    /// <summary>Per-user data directory (%LOCALAPPDATA%\ClassroomControl\StudentAgent on Windows).</summary>
    public static AppPaths ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "ClassroomControl", "StudentAgent"));

    public string DataDirectory { get; }
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string KeyFile => Path.Combine(DataDirectory, "secret.key");
}
