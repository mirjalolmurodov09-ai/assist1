namespace ClassroomControl.Shared.Communication.Messages;

// ---- COMMAND parameters (Teacher -> Student, carried in CommandRequest.Parameters) ----

public sealed record LockParameters(string? Message);

public sealed record SendMessageParameters(string Text, string? Title, bool RequireAcknowledge);

public sealed record StartStreamParameters(int FramesPerSecond, int JpegQuality, int MaxWidth);

public sealed record StartTeacherScreenParameters(string? Title);

public sealed record StartApplicationParameters(string Target, string? Arguments);

public sealed record StopApplicationParameters(string ProcessName);

public sealed record BlockApplicationParameters(string ProcessName);

public sealed record PowerParameters(int DelaySeconds, string? Reason);

/// <summary>Payload of a successful <c>Screenshot</c> command.</summary>
public sealed record ScreenshotPayload(int Width, int Height, string Format, string ImageBase64);

// ---- Streaming messages ----

/// <summary>One screen image (full frame or only the changed region). Used for SCREEN_FRAME (Student->Teacher) and
/// TEACHER_SCREEN_FRAME (Teacher->Student).</summary>
public sealed record ScreenFrameMessage(
    long Sequence,
    int FullWidth,
    int FullHeight,
    int X,
    int Y,
    int Width,
    int Height,
    bool KeyFrame,
    string Format,
    string ImageBase64);

public enum MouseAction { Move, Down, Up, Wheel }

public enum MouseButtonKind { None, Left, Right, Middle }

/// <summary>X and Y are normalized to 0..1 of the student's primary screen.</summary>
public sealed record MouseEventMessage(MouseAction Action, double X, double Y, MouseButtonKind Button, int WheelDelta);

public sealed record KeyboardEventMessage(int VirtualKey, bool IsDown, bool IsExtended);

/// <summary>Periodic student state for the Teacher grid.</summary>
public sealed record StatusUpdateMessage(
    bool Locked,
    bool Streaming,
    bool RemoteControlActive,
    bool ShowingTeacherScreen,
    int CpuPercent,
    long RamTotalBytes,
    long RamUsedBytes,
    int PingMilliseconds,
    int BlockedApplications = 0,
    long BootTimeUnixSeconds = 0);
