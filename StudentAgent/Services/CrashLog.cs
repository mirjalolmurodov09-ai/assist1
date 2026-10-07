using System;
using System.IO;
using ClassroomControl.StudentAgent.Infrastructure.Logging;

namespace ClassroomControl.StudentAgent.Services
{
    /// <summary>Synchronous last-resort log for fatal errors (the normal logger writes asynchronously and may not flush in time).</summary>
    internal static class CrashLog
    {
        public static void Write(string directory, string source, Exception? exception)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var line = DateTime.UtcNow.ToString("O") + " [CRT] " + source + ": " + SensitiveDataRedactor.Redact(exception?.ToString()) + Environment.NewLine;
                File.AppendAllText(Path.Combine(directory, "crash.log"), line);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine("Crash log could not be written: " + ex.Message);
            }
        }
    }
}
