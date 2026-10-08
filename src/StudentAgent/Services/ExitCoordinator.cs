using System.Security.Cryptography;
using System.Text;
using System.Windows;
using ClassroomControl.Shared.Communication.Security;
using ClassroomControl.StudentAgent.Views;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassroomControl.StudentAgent.Services
{
    /// <summary>Handles Exit requests. When the Teacher has set the "agent must remain active" policy, a normal user cannot close the
    /// agent by accident: the classroom code must be entered first. This is an accident guard only - it does not hide the process,
    /// does not restart it, and Windows (Task Manager, uninstall, shutdown) can always end it.</summary>
    internal sealed class ExitCoordinator
    {
        private readonly IHostApplicationLifetime _lifetime;
        private readonly IAgentStatusStore _status;
        private readonly ISettingsService _settings;
        private readonly ILogger<ExitCoordinator> _logger;

        public ExitCoordinator(IHostApplicationLifetime lifetime, IAgentStatusStore status, ISettingsService settings, ILogger<ExitCoordinator> logger)
        {
            _lifetime = lifetime;
            _status = status;
            _settings = settings;
            _logger = logger;
        }

        public void RequestExit()
        {
            if (_status.Current.RequireAgentActive && !ConfirmWithClassroomCode())
            {
                _logger.LogWarning("Exit was refused: the Teacher requires the agent to remain active.");
                return;
            }
            _lifetime.StopApplication();
        }

        /// <summary>Windows is shutting down or the user is logging off: always allowed.</summary>
        public void ExitForSessionEnd() => _lifetime.StopApplication();

        private bool ConfirmWithClassroomCode()
        {
            var expected = ClassroomCode.Normalize(_settings.Current.ClassroomCode);
            if (expected.Length == 0) return true;

            var prompt = new CodePromptWindow(
                (string)Application.Current.FindResource("Str.ExitPromptTitle"),
                (string)Application.Current.FindResource("Str.ExitPromptText"),
                (string)Application.Current.FindResource("Str.Ok"),
                (string)Application.Current.FindResource("Str.Cancel"));
            if (prompt.ShowDialog() != true) return false;

            var entered = Encoding.UTF8.GetBytes(ClassroomCode.Normalize(prompt.EnteredText));
            return CryptographicOperations.FixedTimeEquals(entered, Encoding.UTF8.GetBytes(expected));
        }
    }
}
