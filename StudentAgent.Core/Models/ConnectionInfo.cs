using System.Net;

namespace ClassroomControl.StudentAgent.Models;

public enum TeacherSource { Discovery, Manual }

/// <summary>A Teacher/Local Server endpoint that the agent may connect to.</summary>
public sealed record ConnectionInfo(
    IPAddress Address,
    int Port,
    string TeacherId,
    string TeacherName,
    string ClassroomName,
    TeacherSource Source);
