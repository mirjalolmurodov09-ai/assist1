namespace ClassroomControl.TeacherApp.Services;

public enum SettingsTab { General, Network, Security, Monitoring, ScreenQuality, Groups, Agents, Logs }

/// <summary>Everything the view-models need from the windowing layer, so they stay testable.</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message);
    /// <summary>Asks for one line (or several) of text; null when cancelled.</summary>
    Task<string?> PromptAsync(string title, string label, string initial, bool multiline);
    void Info(string title, string message);
    void ShowFullScreen(ViewModels.ComputerViewModel computer, bool withRemoteControl);
    void ShowSettings(SettingsTab tab);
    void ShowScreenshots();
    void ShowAbout();
    void ShowAddComputer();
    void OpenFile(string path);
}

/// <summary>Who is signed in. The name goes to the audit log for every action.</summary>
public sealed class TeacherSession
{
    private readonly Server _server;

    public TeacherSession(Server server) => _server = server;

    public UserRecord? User { get; private set; }
    public bool IsAdmin => User?.Role == Roles.Admin;

    public void SignIn(UserRecord user)
    {
        User = user;
        _server.Actor = user.Username;
    }

    public void SignOut()
    {
        User = null;
        _server.Actor = "system";
    }
}
