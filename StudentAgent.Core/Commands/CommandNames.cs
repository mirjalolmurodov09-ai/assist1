namespace ClassroomControl.StudentAgent.Commands;

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

    /// <summary>Commands reserved for later stages. They are registered but not implemented.</summary>
    public static readonly string[] Reserved =
    [
        Lock, Unlock, Screenshot, StartApplication, StopApplication, Restart, Shutdown,
        StartScreenStream, StopScreenStream, StartRemoteControl, StopRemoteControl,
    ];
}
