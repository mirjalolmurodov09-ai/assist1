namespace ClassroomControl.ClassroomServer.Data;

/// <summary>Forward-only database migrations. Add new entries with a higher version; never edit an existing one.</summary>
internal static class Schema
{
    public static readonly (int Version, string Sql)[] Migrations =
    [
        (1, @"
CREATE TABLE Users (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Username TEXT NOT NULL UNIQUE COLLATE NOCASE,
    PasswordHash TEXT NOT NULL,
    Role TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    LastLoginAt TEXT NULL,
    Disabled INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE Classrooms (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    CodeProtected TEXT NOT NULL,
    TeacherId TEXT NOT NULL,
    CreatedAt TEXT NOT NULL
);
CREATE TABLE Groups (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ClassroomId INTEGER NOT NULL REFERENCES Classrooms(Id) ON DELETE CASCADE,
    Name TEXT NOT NULL,
    UNIQUE (ClassroomId, Name)
);
CREATE TABLE Students (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    FullName TEXT NOT NULL,
    ClassroomId INTEGER NOT NULL REFERENCES Classrooms(Id) ON DELETE CASCADE,
    CreatedAt TEXT NOT NULL,
    UNIQUE (ClassroomId, FullName)
);
CREATE TABLE Computers (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    DeviceId TEXT NOT NULL UNIQUE,
    ClassroomId INTEGER NOT NULL REFERENCES Classrooms(Id) ON DELETE CASCADE,
    ComputerName TEXT NOT NULL,
    DisplayName TEXT NOT NULL DEFAULT '',
    StudentName TEXT NOT NULL DEFAULT '',
    IpAddress TEXT NOT NULL DEFAULT '',
    MacAddress TEXT NOT NULL DEFAULT '',
    Cpu TEXT NOT NULL DEFAULT '',
    RamBytes INTEGER NOT NULL DEFAULT 0,
    GroupId INTEGER NULL REFERENCES Groups(Id) ON DELETE SET NULL,
    Status TEXT NOT NULL,
    LastSeen TEXT NULL,
    CreatedAt TEXT NOT NULL
);
CREATE TABLE Sessions (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SessionId TEXT NOT NULL,
    DeviceId TEXT NOT NULL,
    ConnectedAt TEXT NOT NULL,
    DisconnectedAt TEXT NULL,
    Reason TEXT NULL
);
CREATE TABLE Commands (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CommandId TEXT NOT NULL,
    DeviceId TEXT NOT NULL,
    Name TEXT NOT NULL,
    Status TEXT NOT NULL,
    ErrorCode TEXT NULL,
    RequestedAt TEXT NOT NULL,
    CompletedAt TEXT NULL,
    RequestedBy TEXT NOT NULL
);
CREATE TABLE Logs (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp TEXT NOT NULL,
    Teacher TEXT NOT NULL,
    Computer TEXT NOT NULL,
    Action TEXT NOT NULL,
    Result TEXT NOT NULL,
    Details TEXT NOT NULL DEFAULT ''
);
CREATE TABLE Screenshots (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ComputerId INTEGER NULL,
    FilePath TEXT NOT NULL,
    TakenAt TEXT NOT NULL,
    StudentName TEXT NOT NULL DEFAULT '',
    ComputerName TEXT NOT NULL DEFAULT ''
);
CREATE TABLE Settings (
    Key TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);
CREATE INDEX IX_Logs_Timestamp ON Logs (Timestamp);
CREATE INDEX IX_Computers_Classroom ON Computers (ClassroomId);
"),
    ];
}
