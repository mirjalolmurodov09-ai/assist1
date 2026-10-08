using System.Security.Cryptography;
using System.Text;
using ClassroomControl.ClassroomServer.Data;

namespace ClassroomControl.ClassroomServer.Security;

/// <summary>PBKDF2-SHA256 password hashing. Format: <c>pbkdf2-sha256$iterations$salt$hash</c> (base64). Passwords are never stored in plain text.</summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Prefix = "pbkdf2-sha256";

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], out var iterations) || iterations < 1) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class UserException : Exception
{
    public UserException(string message) : base(message) { }
}

/// <summary>Teacher / administrator accounts of the Teacher application.</summary>
public sealed class UserService
{
    public const int MinPasswordLength = 8;
    public const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(1);

    private readonly ClassroomStore _store;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Failures, DateTimeOffset LockedUntil)> _failures = new(StringComparer.OrdinalIgnoreCase);

    public UserService(ClassroomStore store, TimeProvider time)
    {
        _store = store;
        _time = time;
    }

    public bool HasUsers => _store.CountUsers() > 0;

    public IReadOnlyList<UserRecord> List() => _store.ListUsers();

    public UserRecord Create(string username, string password, string role)
    {
        username = username.Trim();
        if (username.Length is < 3 or > 32 || username.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-')))
            throw new UserException("Foydalanuvchi nomi 3-32 belgi: harf, raqam, '.', '_' yoki '-'.");
        if (role is not (Roles.Admin or Roles.Teacher)) throw new UserException("Noma'lum rol.");
        EnsurePasswordStrength(password);
        if (_store.GetUser(username) is not null) throw new UserException("Bunday foydalanuvchi allaqachon mavjud.");
        var id = _store.AddUser(username, PasswordHasher.Hash(password), role, _time.GetUtcNow());
        return _store.ListUsers().First(u => u.Id == id);
    }

    /// <summary>Returns the user when the credentials are valid; null otherwise. Repeated failures lock the account for a minute.</summary>
    public UserRecord? Authenticate(string username, string password)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_failures.TryGetValue(username, out var f) && f.LockedUntil > now) return null;
        }
        var user = _store.GetUser(username.Trim());
        var ok = user is not null && !user.Disabled && PasswordHasher.Verify(password, user.PasswordHash);
        lock (_gate)
        {
            if (ok)
            {
                _failures.Remove(username);
            }
            else
            {
                var failures = _failures.TryGetValue(username, out var f) ? f.Failures + 1 : 1;
                _failures[username] = failures >= MaxFailedAttempts ? (0, now + LockoutDuration) : (failures, default);
            }
        }
        if (!ok) return null;
        _store.TouchLogin(user!.Id, now);
        return user;
    }

    public bool IsLockedOut(string username)
    {
        lock (_gate) return _failures.TryGetValue(username, out var f) && f.LockedUntil > _time.GetUtcNow();
    }

    public void ChangePassword(long userId, string newPassword)
    {
        EnsurePasswordStrength(newPassword);
        _store.UpdatePassword(userId, PasswordHasher.Hash(newPassword));
    }

    public void SetDisabled(long userId, bool disabled)
    {
        EnsureNotLastAdmin(userId);
        _store.SetUserDisabled(userId, disabled);
    }

    public void Delete(long userId)
    {
        EnsureNotLastAdmin(userId);
        _store.DeleteUser(userId);
    }

    private void EnsureNotLastAdmin(long userId)
    {
        var users = _store.ListUsers();
        var target = users.FirstOrDefault(u => u.Id == userId);
        if (target is { Role: Roles.Admin } && users.Count(u => u.Role == Roles.Admin && !u.Disabled) <= 1)
            throw new UserException("Oxirgi administratorni o'chirib yoki bloklab bo'lmaydi.");
    }

    private static void EnsurePasswordStrength(string password)
    {
        if (password.Length < MinPasswordLength) throw new UserException($"Parol kamida {MinPasswordLength} belgidan iborat bo'lishi kerak.");
    }
}
