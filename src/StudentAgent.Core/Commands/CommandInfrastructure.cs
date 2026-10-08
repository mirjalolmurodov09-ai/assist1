using System.Text.Json;
using System.Text.RegularExpressions;
using ClassroomControl.Shared.Communication.Messages;
using ClassroomControl.Shared.Communication.Protocol;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using ClassroomControl.StudentAgent.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassroomControl.StudentAgent.Commands;

public sealed record CommandContext(string CommandId, string DeviceId, string SessionId, JsonElement? Parameters);

public sealed record SessionContext(string SessionId, string DeviceId, RegistrationState Registration);

public interface ICommandHandler
{
    string Name { get; }
    /// <summary>False for commands that are reserved for later stages: they are known but never executed.</summary>
    bool IsImplemented { get; }
    Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken);
}

public static class CommandResults
{
    public static CommandResult Success(CommandContext c, string message, object? payload = null) => new(
        c.CommandId, c.DeviceId, CommandStatus.Success, DateTimeOffset.UtcNow, null, message,
        payload is null ? null : JsonSerializer.SerializeToElement(payload, MessageSerializer.Options));

    public static CommandResult Error(string commandId, string deviceId, CommandStatus status, string code, string message) =>
        new(commandId, deviceId, status, DateTimeOffset.UtcNow, code, message, null);
}

/// <summary>Reserved command (Lock, Screenshot, ...). Safe placeholder: it only reports that it is not available yet.</summary>
public sealed class ReservedCommandHandler : ICommandHandler
{
    public ReservedCommandHandler(string name) => Name = name;
    public string Name { get; }
    public bool IsImplemented => false;

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken) =>
        Task.FromResult(CommandResults.Error(context.CommandId, context.DeviceId, CommandStatus.NotImplemented,
            ErrorCodes.CommandNotImplemented, $"'{Name}' buyrug'i bu versiyada qo'llab-quvvatlanmaydi."));
}

public interface ICommandDispatcher
{
    Task<CommandResult> DispatchAsync(CommandRequest request, SessionContext session, CancellationToken cancellationToken);
}

/// <summary>Final authorization + execution. Checks 1-4 (authenticated session, session id, signature, timestamp) are enforced
/// by <c>SecureChannel</c> before a message reaches this point; this class performs check 5 (registered device, known and enabled command).</summary>
public sealed partial class CommandDispatcher : ICommandDispatcher
{
    private const int MaxIdLength = 64;
    private readonly Dictionary<string, ICommandHandler> _handlers;
    private readonly HashSet<string> _enabled;
    private readonly ILogger<CommandDispatcher> _logger;

    public CommandDispatcher(IEnumerable<ICommandHandler> handlers, IOptions<AgentOptions> options, ILogger<CommandDispatcher> logger)
    {
        _handlers = handlers.ToDictionary(h => h.Name, StringComparer.Ordinal);
        _enabled = new HashSet<string>(options.Value.EnabledCommands, StringComparer.Ordinal);
        _logger = logger;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public async Task<CommandResult> DispatchAsync(CommandRequest request, SessionContext session, CancellationToken cancellationToken)
    {
        var commandId = request.CommandId is { Length: > 0 and <= MaxIdLength } id && IdPattern().IsMatch(id) ? id : "invalid";
        if (commandId == "invalid" || string.IsNullOrEmpty(request.Name) || !IdPattern().IsMatch(request.Name))
        {
            _logger.LogWarning("Invalid Command received (malformed identifiers).");
            return CommandResults.Error(commandId, session.DeviceId, CommandStatus.Rejected, ErrorCodes.InvalidMessage, "Buyruq formati noto‘g‘ri.");
        }

        if (session.Registration != RegistrationState.Approved)
            return CommandResults.Error(commandId, session.DeviceId, CommandStatus.Rejected, ErrorCodes.NotRegistered,
                "Qurilma hali Teacher tomonidan tasdiqlanmagan.");

        if (!_handlers.TryGetValue(request.Name, out var handler))
        {
            _logger.LogWarning("Invalid Command: unknown command {Command}.", request.Name);
            return CommandResults.Error(commandId, session.DeviceId, CommandStatus.Rejected, ErrorCodes.UnknownCommand, "Noma'lum buyruq.");
        }
        if (!handler.IsImplemented)
            return await handler.ExecuteAsync(new CommandContext(commandId, session.DeviceId, session.SessionId, request.Parameters), cancellationToken).ConfigureAwait(false);
        if (!_enabled.Contains(handler.Name))
            return CommandResults.Error(commandId, session.DeviceId, CommandStatus.Rejected, ErrorCodes.CommandDisabled, "Bu buyruq o'chirib qo'yilgan.");

        try
        {
            return await handler.ExecuteAsync(new CommandContext(commandId, session.DeviceId, session.SessionId, request.Parameters), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected Error while executing command {Command}.", handler.Name);
            return CommandResults.Error(commandId, session.DeviceId, CommandStatus.Failed, ErrorCodes.CommandFailed, "Buyruqni bajarishda xatolik yuz berdi.");
        }
    }
}
