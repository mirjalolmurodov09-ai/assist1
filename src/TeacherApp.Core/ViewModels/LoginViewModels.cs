namespace ClassroomControl.TeacherApp.ViewModels;

public sealed class LoginViewModel : ObservableObject
{
    private readonly UserService _users;
    private string _username = string.Empty;
    private string _error = string.Empty;

    public LoginViewModel(UserService users) => _users = users;

    public string Username
    {
        get => _username;
        set => Set(ref _username, value);
    }

    public string Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    /// <summary>The password is passed in (not bound) so it never sits in a property or a binding.</summary>
    public UserRecord? TryLogin(string password)
    {
        if (_users.IsLockedOut(Username))
        {
            Error = Loc.Instance["Login.Locked"];
            return null;
        }
        var user = _users.Authenticate(Username, password);
        Error = user is null ? Loc.Instance[_users.IsLockedOut(Username) ? "Login.Locked" : "Login.Wrong"] : string.Empty;
        return user;
    }
}

public sealed class SetupAdminViewModel : ObservableObject
{
    private readonly UserService _users;
    private string _username = "admin";
    private string _error = string.Empty;

    public SetupAdminViewModel(UserService users) => _users = users;

    public string Username
    {
        get => _username;
        set => Set(ref _username, value);
    }

    public string Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public UserRecord? Create(string password, string confirm)
    {
        if (password != confirm)
        {
            Error = Loc.Instance["Setup.Mismatch"];
            return null;
        }
        try
        {
            var user = _users.Create(Username, password, Roles.Admin);
            Error = string.Empty;
            return user;
        }
        catch (UserException ex)
        {
            Error = ex.Message;
            return null;
        }
    }
}
