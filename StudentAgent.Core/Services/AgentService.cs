using ClassroomControl.StudentAgent.Communication.Messages;
using ClassroomControl.StudentAgent.Communication.Protocol;
using ClassroomControl.StudentAgent.Communication.Security;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using ClassroomControl.StudentAgent.Models;
using ClassroomControl.StudentAgent.Resources.Strings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassroomControl.StudentAgent.Services;

public interface IAgentService
{
    /// <summary>Start (or restart) connecting now, clearing a previous rejection and the back-off.</summary>
    void RequestConnect();
    /// <summary>Call after settings were saved: restarts the connection only when a connection-relevant setting changed.</summary>
    void NotifySettingsChanged();
}

/// <summary>Supervisor of the whole connection life cycle: discovery → TLS → authentication → registration → session → reconnect.
/// Any failure is contained here; the loop only ends when the host stops.</summary>
public sealed class AgentService : BackgroundService, IAgentService
{
    private readonly ISettingsService _settings;
    private readonly IDiscoveryService _discovery;
    private readonly IConnectionService _connection;
    private readonly IAuthenticationService _auth;
    private readonly ISessionRunner _runner;
    private readonly IReconnectService _reconnect;
    private readonly INetworkMonitor _network;
    private readonly IAgentStatusStore _status;
    private readonly TimeProvider _time;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentService> _logger;
    private readonly AsyncSignal _wake = new();
    private readonly object _gate = new();

    private CancellationTokenSource? _attempt;
    private string _activeKey = string.Empty;
    private ConnectionInfo? _lastTeacher;
    private bool _rejected;
    private bool _hadFailure;

    public AgentService(ISettingsService settings, IDiscoveryService discovery, IConnectionService connection,
        IAuthenticationService auth, ISessionRunner runner, IReconnectService reconnect, INetworkMonitor network,
        IAgentStatusStore status, TimeProvider time, IOptions<AgentOptions> options, ILogger<AgentService> logger)
    {
        _settings = settings;
        _discovery = discovery;
        _connection = connection;
        _auth = auth;
        _runner = runner;
        _reconnect = reconnect;
        _network = network;
        _status = status;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    public void RequestConnect()
    {
        _rejected = false;
        _reconnect.Reset();
        Interrupt();
        _wake.Set();
    }

    public void NotifySettingsChanged()
    {
        var key = ConnectionKey(_settings.Current);
        bool changed;
        lock (_gate) changed = key != _activeKey;
        if (!changed) return;
        _rejected = false;
        _lastTeacher = null;
        _reconnect.Reset();
        Interrupt();
        _wake.Set();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agent Started");
        _network.NetworkChanged += OnNetworkChanged;
        _network.Start();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunCycleAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected Error in the connection loop; restarting it.");
                    _status.Update(s => s with { LastErrorCode = "UNEXPECTED_ERROR", LastErrorMessage = UiStrings.UnexpectedError });
                    EnsureOffline();
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_options.SupervisorRestartDelaySeconds), _time, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            _network.NetworkChanged -= OnNetworkChanged;
            ForceDisconnected(UiStrings.Stopped);
            _logger.LogInformation("Agent Stopped");
        }
    }

    private async Task RunCycleAsync(CancellationToken stop)
    {
        var settings = _settings.Current;
        lock (_gate) _activeKey = ConnectionKey(settings);

        if (!ClassroomCode.IsValidFormat(settings.ClassroomCode))
        {
            ForceDisconnected(UiStrings.ClassroomCodeMissing, ErrorCodes.InvalidClassroomCode);
            await _wake.WaitAsync(stop).ConfigureAwait(false);
            return;
        }
        if (_rejected)
        {
            ForceDisconnected(UiStrings.RejectedByTeacher, ErrorCodes.Rejected);
            await _wake.WaitAsync(stop).ConfigureAwait(false);
            return;
        }

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (_gate) _attempt = attempt;
        var ct = attempt.Token;
        try
        {
            await RunAttemptAsync(settings, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            // Interrupted by a network/settings change: start over immediately.
            _logger.LogInformation("Connection attempt interrupted; restarting discovery.");
            EnsureOffline();
            _status.Update(s => s with { StatusText = UiStrings.NetworkChanged });
        }
        finally
        {
            lock (_gate) _attempt = null;
        }
    }

    private async Task RunAttemptAsync(StudentSettings settings, CancellationToken ct)
    {
        _status.TransitionTo(ConnectionState.Discovering, s => s with { StatusText = UiStrings.Discovering, LastErrorCode = null, LastErrorMessage = null });

        ConnectionInfo? teacher = _lastTeacher;
        if (teacher is null)
        {
            var found = await _discovery.FindTeacherAsync(settings, ct).ConfigureAwait(false);
            teacher = found.Teacher;
            if (teacher is null)
            {
                var code = found.RejectedResponsesSeen ? ErrorCodes.InvalidClassroomCode : "TEACHER_NOT_FOUND";
                var message = found.RejectedResponsesSeen ? UiStrings.InvalidClassroomCode : UiStrings.TeacherNotFound;
                await FailAsync(code, message, ct).ConfigureAwait(false);
                return;
            }
        }

        _status.TransitionTo(ConnectionState.TeacherFound, s => s with
        {
            TeacherAddress = teacher.Address.ToString(),
            TeacherName = string.IsNullOrEmpty(teacher.TeacherName) ? s.TeacherName : teacher.TeacherName,
            ClassroomName = string.IsNullOrEmpty(teacher.ClassroomName) ? s.ClassroomName : teacher.ClassroomName,
        });
        _status.TransitionTo(ConnectionState.Connecting, s => s with { StatusText = UiStrings.Connecting });

        TeacherConnection connection;
        try
        {
            connection = await _connection.ConnectAsync(teacher, settings, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _lastTeacher = null;
            var code = ex is ProtocolException pe ? pe.ErrorCode : "CONNECTION_FAILED";
            _logger.LogWarning("Connection to Teacher failed: {Reason}", ex.Message);
            await FailAsync(code, ex is ProtocolException ? ex.Message : UiStrings.TeacherNotFound, ct).ConfigureAwait(false);
            return;
        }

        await using (connection.ConfigureAwait(false))
        {
            _status.TransitionTo(ConnectionState.Authenticating, s => s with { StatusText = UiStrings.Authenticating });
            AuthenticationResult auth;
            try
            {
                auth = await _auth.AuthenticateAsync(connection, settings, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                _lastTeacher = null;
                await FailAsync("CONNECTION_FAILED", UiStrings.ConnectionLost, ct).ConfigureAwait(false);
                return;
            }

            if (!auth.Success || auth.Session is null)
            {
                _lastTeacher = null;
                if (auth.ErrorCode == ErrorCodes.Rejected)
                {
                    _logger.LogWarning("Registration Rejected by Teacher.");
                    _rejected = true;
                    ForceDisconnected(UiStrings.RejectedByTeacher, ErrorCodes.Rejected);
                    return;
                }
                await FailAsync(auth.ErrorCode ?? ErrorCodes.AuthenticationFailed, auth.Message ?? string.Empty, ct).ConfigureAwait(false);
                return;
            }

            await using var session = auth.Session.Channel;
            await OnAuthenticatedAsync(settings, teacher, connection, auth.Session, ct).ConfigureAwait(false);
            var end = await _runner.RunAsync(auth.Session, update => OnRegistrationUpdate(update), ct).ConfigureAwait(false);
            await HandleSessionEndAsync(end, ct).ConfigureAwait(false);
        }
    }

    private async Task OnAuthenticatedAsync(StudentSettings settings, ConnectionInfo teacher, TeacherConnection connection, AuthenticatedSession session, CancellationToken ct)
    {
        if (_hadFailure) _logger.LogInformation("Reconnect Success");
        _hadFailure = false;
        _reconnect.Reset();
        _lastTeacher = teacher with { TeacherId = session.Classroom.TeacherId, TeacherName = session.Classroom.TeacherName };

        var now = _time.GetUtcNow();
        var approved = session.Registration == RegistrationState.Approved;
        if (approved) _logger.LogInformation("Registration Approved");
        else _logger.LogInformation("Waiting for Teacher approval.");

        _status.TransitionTo(approved ? ConnectionState.Connected : ConnectionState.WaitingForApproval, s => s with
        {
            Registration = session.Registration,
            StatusText = approved ? UiStrings.Connected : UiStrings.WaitingForApproval,
            TeacherName = session.Classroom.TeacherName,
            ClassroomName = session.Classroom.Name,
            LastConnectionUtc = now,
            RequireAgentActive = session.Policy.RequireAgentActive,
            LastErrorCode = null,
            LastErrorMessage = null,
        });

        try
        {
            await _settings.UpdateAsync(s =>
            {
                s.PinnedTeacherCertificate = connection.CertificateFingerprint;
                s.LastConnectionUtc = now;
                s.ClassroomName = session.Classroom.Name;
            }, ct).ConfigureAwait(false);
        }
        catch (SettingsValidationException ex)
        {
            _logger.LogWarning("Connection details could not be stored: {Reason}", ex.Message);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Connection details could not be stored.");
        }
    }

    private void OnRegistrationUpdate(RegistrationUpdateMessage update)
    {
        _status.Update(s => s with
        {
            Registration = update.Registration,
            RequireAgentActive = update.Policy?.RequireAgentActive ?? s.RequireAgentActive,
        });
        switch (update.Registration)
        {
            case RegistrationState.Approved:
                _logger.LogInformation("Registration Approved");
                if (_status.Current.State == ConnectionState.WaitingForApproval)
                    _status.TransitionTo(ConnectionState.Connected, s => s with { StatusText = UiStrings.Connected });
                break;
            case RegistrationState.Pending:
                if (_status.Current.State == ConnectionState.Connected)
                    _status.TransitionTo(ConnectionState.WaitingForApproval, s => s with { StatusText = UiStrings.WaitingForApproval });
                break;
            case RegistrationState.Rejected:
                _logger.LogWarning("Registration Rejected by Teacher.");
                break;
        }
    }

    private async Task HandleSessionEndAsync(SessionEnd end, CancellationToken ct)
    {
        switch (end.Reason)
        {
            case SessionEndReason.Cancelled:
                ct.ThrowIfCancellationRequested();
                break;
            case SessionEndReason.Rejected:
                _rejected = true;
                _lastTeacher = null;
                ForceDisconnected(UiStrings.RejectedByTeacher, ErrorCodes.Rejected);
                break;
            default:
                _logger.LogWarning("Connection Lost ({Reason}).", end.Reason);
                if (end.Reason == SessionEndReason.SecurityViolation) _lastTeacher = null;
                _hadFailure = true;
                await FailAsync("CONNECTION_LOST", UiStrings.ConnectionLost, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Goes offline (red), waits the back-off delay without using CPU, then lets the loop retry.</summary>
    private async Task FailAsync(string code, string message, CancellationToken ct)
    {
        _hadFailure = true;
        var delay = _reconnect.NextDelay();
        _logger.LogInformation("Reconnect Started (attempt {Attempt}, in {Delay}s)", _reconnect.Attempt, delay.TotalSeconds);
        _status.TransitionTo(ConnectionState.Reconnecting, s => s with
        {
            StatusText = $"{message} {UiStrings.ReconnectingIn(delay)}".Trim(),
            LastErrorCode = code,
            LastErrorMessage = message,
            Registration = s.Registration == RegistrationState.Rejected ? s.Registration : RegistrationState.NotRegistered,
        });
        await _reconnect.WaitAsync(delay, ct).ConfigureAwait(false);
    }

    private void EnsureOffline()
    {
        var state = _status.Current.State;
        if (state is ConnectionState.Disconnected or ConnectionState.Reconnecting) return;
        _status.TransitionTo(ConnectionState.Reconnecting);
    }

    private void ForceDisconnected(string message, string? errorCode = null)
    {
        if (_status.Current.State != ConnectionState.Disconnected) _status.TransitionTo(ConnectionState.Disconnected);
        _status.Update(s => s with
        {
            StatusText = message,
            LastErrorCode = errorCode,
            LastErrorMessage = errorCode is null ? null : message,
            Registration = RegistrationState.NotRegistered,
        });
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        _lastTeacher = null;
        _reconnect.Reset();
        Interrupt();
    }

    private void Interrupt()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _attempt;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The attempt already finished.
        }
    }

    private static string ConnectionKey(StudentSettings s) =>
        string.Join('|', s.ClassroomCode, s.TeacherAddress, s.TeacherPort, s.DiscoveryPort, s.StudentName, s.ComputerName);
}
