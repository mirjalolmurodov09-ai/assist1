using System.Text.Json;
using ClassroomControl.StudentAgent.Commands;

namespace ClassroomControl.StudentAgent.Features;

internal static class Parameters
{
    /// <summary>Reads command parameters; a missing or malformed object becomes null so the handler can answer with INVALID_PARAMETERS.</summary>
    public static T? Read<T>(CommandContext context) where T : class
    {
        if (context.Parameters is not { ValueKind: JsonValueKind.Object } json) return null;
        try
        {
            return json.Deserialize<T>(MessageSerializer.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static CommandResult Invalid(CommandContext c, string what) =>
        CommandResults.Error(c.CommandId, c.DeviceId, CommandStatus.Rejected, ErrorCodes.InvalidParameters, $"Buyruq parametrlari noto‘g‘ri: {what}.");
}

public sealed class LockCommandHandler : ICommandHandler
{
    public const string DefaultMessage = "Diqqat! O‘qituvchi kompyuterni vaqtincha blokladi.";
    private const int MaxMessage = 500;
    private readonly ILockScreen _lock;
    private readonly FeatureState _state;

    public LockCommandHandler(ILockScreen lockScreen, FeatureState state)
    {
        _lock = lockScreen;
        _state = state;
    }

    public string Name => CommandNames.Lock;
    public bool IsImplemented => true;

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var p = Parameters.Read<LockParameters>(context);
        var message = string.IsNullOrWhiteSpace(p?.Message) ? DefaultMessage : p.Message.Trim();
        if (message.Length > MaxMessage) message = message[..MaxMessage];
        _lock.Show(message);
        _state.SetLocked(true);
        return Task.FromResult(CommandResults.Success(context, "Locked"));
    }
}

public sealed class UnlockCommandHandler : ICommandHandler
{
    private readonly ILockScreen _lock;
    private readonly FeatureState _state;

    public UnlockCommandHandler(ILockScreen lockScreen, FeatureState state)
    {
        _lock = lockScreen;
        _state = state;
    }

    public string Name => CommandNames.Unlock;
    public bool IsImplemented => true;

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        _lock.Hide();
        _state.SetLocked(false);
        return Task.FromResult(CommandResults.Success(context, "Unlocked"));
    }
}

public sealed class SendMessageCommandHandler : ICommandHandler
{
    private const int MaxText = 2000;
    private const int MaxTitle = 100;
    private readonly IUserNotifier _notifier;

    public SendMessageCommandHandler(IUserNotifier notifier) => _notifier = notifier;

    public string Name => CommandNames.SendMessage;
    public bool IsImplemented => true;

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var p = Parameters.Read<SendMessageParameters>(context);
        if (p is null || string.IsNullOrWhiteSpace(p.Text)) return Task.FromResult(Parameters.Invalid(context, "xabar matni kerak"));
        var title = string.IsNullOrWhiteSpace(p.Title) ? "O‘qituvchi xabari" : p.Title.Trim();
        _notifier.Show(title.Length > MaxTitle ? title[..MaxTitle] : title, p.Text.Length > MaxText ? p.Text[..MaxText] : p.Text);
        return Task.FromResult(CommandResults.Success(context, "Message shown"));
    }
}

public sealed class ScreenshotCommandHandler : ICommandHandler
{
    private const int Quality = 85;
    private readonly IScreenSource _source;
    private readonly IFrameEncoder _encoder;

    public ScreenshotCommandHandler(IScreenSource source, IFrameEncoder encoder)
    {
        _source = source;
        _encoder = encoder;
    }

    public string Name => CommandNames.Screenshot;
    public bool IsImplemented => true;

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var frame = await Task.Run(() => _source.Capture(0), cancellationToken).ConfigureAwait(false);
        if (frame is null)
            return CommandResults.Error(context.CommandId, context.DeviceId, CommandStatus.Failed, ErrorCodes.PlatformUnavailable, "Ekranni olib bo‘lmadi (kompyuter bloklangan bo‘lishi mumkin).");
        var bytes = await Task.Run(() => _encoder.Encode(frame, new PixelRegion(0, 0, frame.Width, frame.Height), Quality), cancellationToken).ConfigureAwait(false);
        return CommandResults.Success(context, "Screenshot taken", new ScreenshotPayload(frame.Width, frame.Height, _encoder.Format, Convert.ToBase64String(bytes)));
    }
}

public sealed class StartScreenStreamCommandHandler : ICommandHandler
{
    private readonly IScreenStreamService _stream;

    public StartScreenStreamCommandHandler(IScreenStreamService stream) => _stream = stream;
    public string Name => CommandNames.StartScreenStream;
    public bool IsImplemented => true;

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var p = Parameters.Read<StartStreamParameters>(context) ?? new StartStreamParameters(3, 50, 480);
        _stream.Start(p);
        return Task.FromResult(CommandResults.Success(context, "Streaming started"));
    }
}

public sealed class StopScreenStreamCommandHandler : ICommandHandler
{
    private readonly IScreenStreamService _stream;

    public StopScreenStreamCommandHandler(IScreenStreamService stream) => _stream = stream;
    public string Name => CommandNames.StopScreenStream;
    public bool IsImplemented => true;

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        await _stream.StopAsync().ConfigureAwait(false);
        return CommandResults.Success(context, "Streaming stopped");
    }
}

public sealed class StartRemoteControlCommandHandler : ICommandHandler
{
    private readonly IRemoteInputService _remote;

    public StartRemoteControlCommandHandler(IRemoteInputService remote) => _remote = remote;
    public string Name => CommandNames.StartRemoteControl;
    public bool IsImplemented => true;

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        _remote.Start();
        return Task.FromResult(CommandResults.Success(context, "Remote control active"));
    }
}

public sealed class StopRemoteControlCommandHandler : ICommandHandler
{
    private readonly IRemoteInputService _remote;

    public StopRemoteControlCommandHandler(IRemoteInputService remote) => _remote = remote;
    public string Name => CommandNames.StopRemoteControl;
    public bool IsImplemented => true;

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        _remote.Stop();
        return Task.FromResult(CommandResults.Success(context, "Remote control stopped"));
    }
}

public sealed class StartTeacherScreenCommandHandler : ICommandHandler
{
    private readonly ITeacherScreenService _screen;

    public StartTeacherScreenCommandHandler(ITeacherScreenService screen) => _screen = screen;
    public string Name => CommandNames.StartTeacherScreen;
    public bool IsImplemented => true;

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        _screen.Start(Parameters.Read<StartTeacherScreenParameters>(context)?.Title);
        return Task.FromResult(CommandResults.Success(context, "Teacher screen shown"));
    }
}

public sealed class StopTeacherScreenCommandHandler : ICommandHandler
{
    private readonly ITeacherScreenService _screen;

    public StopTeacherScreenCommandHandler(ITeacherScreenService screen) => _screen = screen;
    public string Name => CommandNames.StopTeacherScreen;
    public bool IsImplemented => true;

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        _screen.Stop();
        return Task.FromResult(CommandResults.Success(context, "Teacher screen closed"));
    }
}

public sealed class RestartCommandHandler : ICommandHandler
{
    private readonly ISystemControl _system;

    public RestartCommandHandler(ISystemControl system) => _system = system;
    public string Name => CommandNames.Restart;
    public bool IsImplemented => true;

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var p = Parameters.Read<PowerParameters>(context) ?? new PowerParameters(PowerLimits.DefaultDelay, null);
        _system.Restart(PowerLimits.Clamp(p.DelaySeconds), p.Reason);
        return Task.FromResult(CommandResults.Success(context, "Restart scheduled"));
    }
}

public sealed class ShutdownCommandHandler : ICommandHandler
{
    private readonly ISystemControl _system;

    public ShutdownCommandHandler(ISystemControl system) => _system = system;
    public string Name => CommandNames.Shutdown;
    public bool IsImplemented => true;

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var p = Parameters.Read<PowerParameters>(context) ?? new PowerParameters(PowerLimits.DefaultDelay, null);
        _system.Shutdown(PowerLimits.Clamp(p.DelaySeconds), p.Reason);
        return Task.FromResult(CommandResults.Success(context, "Shutdown scheduled"));
    }
}

internal static class PowerLimits
{
    public const int DefaultDelay = 10;
    private const int Min = 5;
    private const int Max = 300;

    /// <summary>The student always gets at least a few seconds' warning from Windows to save their work.</summary>
    public static int Clamp(int seconds) => Math.Clamp(seconds, Min, Max);
}

public sealed class StartApplicationCommandHandler : ICommandHandler
{
    private const int MaxTarget = 260;
    private const int MaxArguments = 1024;
    private readonly ISystemControl _system;

    public StartApplicationCommandHandler(ISystemControl system) => _system = system;
    public string Name => CommandNames.StartApplication;
    public bool IsImplemented => true;

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var p = Parameters.Read<StartApplicationParameters>(context);
        if (p is null || string.IsNullOrWhiteSpace(p.Target) || p.Target.Length > MaxTarget || p.Target.Any(char.IsControl))
            return Task.FromResult(Parameters.Invalid(context, "dastur yoki fayl nomi kerak"));
        if (p.Arguments is { } a && (a.Length > MaxArguments || a.Any(ch => char.IsControl(ch) && ch is not '\t')))
            return Task.FromResult(Parameters.Invalid(context, "argumentlar noto‘g‘ri"));

        var result = _system.StartApplication(p.Target.Trim(), p.Arguments);
        return Task.FromResult(result.Started
            ? CommandResults.Success(context, result.Message)
            : CommandResults.Error(context.CommandId, context.DeviceId, CommandStatus.Failed, ErrorCodes.CommandFailed, result.Message));
    }
}

public sealed class StopApplicationCommandHandler : ICommandHandler
{
    private readonly ISystemControl _system;

    public StopApplicationCommandHandler(ISystemControl system) => _system = system;
    public string Name => CommandNames.StopApplication;
    public bool IsImplemented => true;

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var p = Parameters.Read<StopApplicationParameters>(context);
        var name = p?.ProcessName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 64 || name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            return Task.FromResult(Parameters.Invalid(context, "jarayon nomi kerak"));
        var count = _system.StopApplication(name);
        return Task.FromResult(count > 0
            ? CommandResults.Success(context, $"{count} process stopped", new { stopped = count })
            : CommandResults.Error(context.CommandId, context.DeviceId, CommandStatus.Failed, ErrorCodes.CommandFailed, "Bunday dastur ishlamayapti yoki to‘xtatib bo‘lmaydi."));
    }
}
