using System.Text.Json;
using ClassroomControl.Shared.Communication.Security;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using ClassroomControl.StudentAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassroomControl.StudentAgent.Services;

public interface ISettingsService
{
    /// <summary>A copy of the current settings (safe to modify).</summary>
    StudentSettings Current { get; }
    event EventHandler? Changed;
    /// <summary>Loads settings; creates the Device ID on first run; recovers from a corrupted file.</summary>
    void Load();
    Task SaveAsync(StudentSettings settings, CancellationToken cancellationToken = default);
    Task UpdateAsync(Action<StudentSettings> mutate, CancellationToken cancellationToken = default);
}

public sealed class SettingsValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }
    public SettingsValidationException(IReadOnlyList<string> errors) : base(string.Join(" ", errors)) => Errors = errors;
}

public sealed class SettingsService : ISettingsService
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly IAppPaths _paths;
    private readonly ISecretProtector _protector;
    private readonly ILogger<SettingsService> _logger;
    private readonly bool _allowLoopback;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _stateGate = new();
    private StudentSettings _current = new();

    public SettingsService(IAppPaths paths, ISecretProtector protector, IOptions<AgentOptions> options, ILogger<SettingsService> logger)
    {
        _paths = paths;
        _protector = protector;
        _logger = logger;
        _allowLoopback = options.Value.AllowLoopbackTeacher;
    }

    public event EventHandler? Changed;

    public StudentSettings Current
    {
        get { lock (_stateGate) return _current.Clone(); }
    }

    public void Load()
    {
        var loaded = TryRead() ?? new StudentSettings();
        var created = false;
        if (!Guid.TryParse(loaded.DeviceId, out _))
        {
            loaded.DeviceId = Guid.NewGuid().ToString();
            created = true;
            _logger.LogInformation("Device ID created.");
        }
        Sanitize(loaded);
        lock (_stateGate) _current = loaded;
        if (created || !File.Exists(_paths.SettingsFile)) WriteToDisk(loaded);
    }

    public async Task SaveAsync(StudentSettings settings, CancellationToken cancellationToken = default)
    {
        var candidate = settings.Clone();
        lock (_stateGate) candidate.DeviceId = _current.DeviceId; // Device ID is immutable after creation.
        candidate.ClassroomCode = ClassroomCode.Normalize(candidate.ClassroomCode);
        var errors = SettingsValidator.Validate(candidate, _allowLoopback);
        if (errors.Count > 0) throw new SettingsValidationException(errors);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => WriteToDisk(candidate), cancellationToken).ConfigureAwait(false);
            lock (_stateGate) _current = candidate;
        }
        finally
        {
            _writeGate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task UpdateAsync(Action<StudentSettings> mutate, CancellationToken cancellationToken = default)
    {
        var copy = Current;
        mutate(copy);
        return SaveAsync(copy, cancellationToken);
    }

    private StudentSettings? TryRead()
    {
        if (!File.Exists(_paths.SettingsFile)) return null;
        try
        {
            var file = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(_paths.SettingsFile), JsonOptions)
                       ?? throw new JsonException("Empty settings file.");
            var s = new StudentSettings
            {
                DeviceId = file.DeviceId ?? string.Empty,
                ComputerName = file.ComputerName ?? string.Empty,
                StudentName = file.StudentName ?? string.Empty,
                ClassroomName = file.ClassroomName ?? string.Empty,
                TeacherAddress = file.TeacherAddress ?? string.Empty,
                TeacherPort = file.TeacherPort,
                DiscoveryPort = file.DiscoveryPort,
                ConnectionTimeoutSeconds = file.ConnectionTimeoutSeconds,
                StartWithWindows = file.StartWithWindows,
                PinnedTeacherCertificate = file.PinnedTeacherCertificate ?? string.Empty,
                LastConnectionUtc = file.LastConnectionUtc,
            };
            if (!string.IsNullOrEmpty(file.ClassroomCodeProtected))
            {
                var code = _protector.Unprotect(file.ClassroomCodeProtected);
                if (code is null) _logger.LogWarning("Stored classroom code could not be decrypted; it must be entered again.");
                else s.ClassroomCode = code;
            }
            return s;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            var backup = _paths.SettingsFile + ".corrupt";
            _logger.LogError(ex, "Settings file is invalid; defaults will be used. A copy was kept at {Backup}.", backup);
            try { File.Copy(_paths.SettingsFile, backup, overwrite: true); }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(copyEx, "Could not keep a backup of the invalid settings file.");
            }
            return null;
        }
    }

    /// <summary>Replaces out-of-range values that came from a hand-edited or damaged file with defaults.</summary>
    private void Sanitize(StudentSettings s)
    {
        var defaults = new StudentSettings();
        if (s.TeacherPort is < 0 or > 65535) s.TeacherPort = 0;
        if (s.DiscoveryPort is < 1 or > 65535) s.DiscoveryPort = defaults.DiscoveryPort;
        if (s.ConnectionTimeoutSeconds is < SettingsValidator.MinTimeoutSeconds or > SettingsValidator.MaxTimeoutSeconds)
            s.ConnectionTimeoutSeconds = defaults.ConnectionTimeoutSeconds;
        if (!string.IsNullOrEmpty(s.ClassroomCode) && !ClassroomCode.IsValidFormat(s.ClassroomCode)) s.ClassroomCode = string.Empty;
        s.ClassroomCode = ClassroomCode.Normalize(s.ClassroomCode);
        if (!string.IsNullOrWhiteSpace(s.TeacherAddress) && !SettingsValidator.IsAcceptableTeacherHost(s.TeacherAddress, _allowLoopback))
        {
            _logger.LogWarning("Configured Teacher address is not a local network address and was ignored.");
            s.TeacherAddress = string.Empty;
        }
    }

    private void WriteToDisk(StudentSettings s)
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        var file = new SettingsFile
        {
            SchemaVersion = SchemaVersion,
            DeviceId = s.DeviceId,
            ComputerName = s.ComputerName,
            StudentName = s.StudentName,
            ClassroomName = s.ClassroomName,
            ClassroomCodeProtected = string.IsNullOrEmpty(s.ClassroomCode) ? null : _protector.Protect(s.ClassroomCode),
            TeacherAddress = s.TeacherAddress,
            TeacherPort = s.TeacherPort,
            DiscoveryPort = s.DiscoveryPort,
            ConnectionTimeoutSeconds = s.ConnectionTimeoutSeconds,
            StartWithWindows = s.StartWithWindows,
            PinnedTeacherCertificate = s.PinnedTeacherCertificate,
            LastConnectionUtc = s.LastConnectionUtc,
        };
        var temp = _paths.SettingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
        File.Move(temp, _paths.SettingsFile, overwrite: true);
    }

    /// <summary>On-disk shape. The classroom code only ever appears here in encrypted form.</summary>
    private sealed class SettingsFile
    {
        public int SchemaVersion { get; set; }
        public string? DeviceId { get; set; }
        public string? ComputerName { get; set; }
        public string? StudentName { get; set; }
        public string? ClassroomName { get; set; }
        public string? ClassroomCodeProtected { get; set; }
        public string? TeacherAddress { get; set; }
        public int TeacherPort { get; set; }
        public int DiscoveryPort { get; set; } = ProtocolConstants.DefaultDiscoveryPort;
        public int ConnectionTimeoutSeconds { get; set; } = StudentSettings.DefaultConnectionTimeoutSeconds;
        public bool StartWithWindows { get; set; } = true;
        public string? PinnedTeacherCertificate { get; set; }
        public DateTimeOffset? LastConnectionUtc { get; set; }
    }
}
