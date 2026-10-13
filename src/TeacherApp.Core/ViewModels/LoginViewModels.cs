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

/// <summary>Reset of a forgotten administrator/teacher password. Only reachable from an elevated (Windows administrator) process.</summary>
public sealed class RecoverPasswordViewModel : ObservableObject
{
    private readonly UserService _users;
    private readonly string _operatingSystemUser;
    private UserRecord? _selected;
    private string _error = string.Empty;

    public RecoverPasswordViewModel(UserService users, string operatingSystemUser)
    {
        _users = users;
        _operatingSystemUser = operatingSystemUser;
        Users = users.List().OrderBy(u => u.Role != Roles.Admin).ThenBy(u => u.Username, StringComparer.OrdinalIgnoreCase).ToList();
        _selected = Users.FirstOrDefault();
    }

    public IReadOnlyList<UserRecord> Users { get; }

    public UserRecord? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public bool TryReset(string password, string confirm)
    {
        if (Selected is null)
        {
            Error = Loc.Instance["Recover.NoUsers"];
            return false;
        }
        if (password != confirm)
        {
            Error = Loc.Instance["Setup.Mismatch"];
            return false;
        }
        try
        {
            _users.RecoverPassword(Selected.Id, password, _operatingSystemUser);
            Error = string.Empty;
            return true;
        }
        catch (UserException ex)
        {
            Error = ex.Message;
            return false;
        }
    }
}
