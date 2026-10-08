namespace ClassroomControl.Shared.Communication.Protocol;

public static class CommandNames
{
    public const string Ping = "Ping";
    public const string GetStatus = "GetStatus";
    public const string GetDeviceInfo = "GetDeviceInfo";
    public const string Lock = "Lock";
    public const string Unlock = "Unlock";
    public const string Screenshot = "Screenshot";
    public const string StartApplication = "StartApplication";
    public const string StopApplication = "StopApplication";
    public const string Restart = "Restart";
    public const string Shutdown = "Shutdown";
    public const string StartScreenStream = "StartScreenStream";
    public const string StopScreenStream = "StopScreenStream";
    public const string StartRemoteControl = "StartRemoteControl";
    public const string StopRemoteControl = "StopRemoteControl";
    public const string SendMessage = "SendMessage";
    public const string StartTeacherScreen = "StartTeacherScreen";
    public const string StopTeacherScreen = "StopTeacherScreen";

    /// <summary>Every command a Teacher may send to a Student Agent in protocol 1.0.</summary>
    public static readonly string[] All =
    [
        Ping, GetStatus, GetDeviceInfo, Lock, Unlock, Screenshot, StartApplication, StopApplication, Restart, Shutdown,
        StartScreenStream, StopScreenStream, StartRemoteControl, StopRemoteControl, SendMessage, StartTeacherScreen, StopTeacherScreen,
    ];

    /// <summary>Commands without a dedicated handler yet.</summary>
    public static readonly string[] Reserved = [.. All.Where(n => n is not (Ping or GetStatus or GetDeviceInfo))];
}
