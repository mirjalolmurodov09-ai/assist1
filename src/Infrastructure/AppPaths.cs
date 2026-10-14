namespace ClassroomControl.Infrastructure;

public interface IAppPaths
{
    string DataDirectory { get; }
    string SettingsFile { get; }
    string LogDirectory { get; }
    string KeyFile { get; }
    /// <summary>Folder for files received over the network. Visible to the user (Documents), never chosen by the sender.</summary>
    string ReceivedFilesDirectory { get; }
}

public sealed class AppPaths : IAppPaths
{
    public AppPaths(string dataDirectory, string? receivedFilesDirectory = null)
    {
        DataDirectory = dataDirectory;
        ReceivedFilesDirectory = receivedFilesDirectory ?? Path.Combine(dataDirectory, "Received");
    }

    /// <summary>Per-user data directory (%LOCALAPPDATA%\ClassroomControl\StudentAgent on Windows).</summary>
    public static AppPaths ForCurrentUser(string appName = "StudentAgent")
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "ClassroomControl", appName);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.Create);
        var received = string.IsNullOrEmpty(documents)
            ? null
            : Path.Combine(documents, "ClassroomControl", appName == "Teacher" ? "Received from students" : "Received from teacher");
        return new AppPaths(data, received);
    }

    public string DataDirectory { get; }
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string KeyFile => Path.Combine(DataDirectory, "secret.key");
    public string ReceivedFilesDirectory { get; }
}
