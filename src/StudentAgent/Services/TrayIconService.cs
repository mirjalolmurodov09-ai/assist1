using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ClassroomControl.StudentAgent.Models;
using ClassroomControl.StudentAgent.ViewModels;
using ClassroomControl.StudentAgent.Resources.Strings;

namespace ClassroomControl.StudentAgent.Services
{
    /// <summary>System tray icon: green = connected, yellow = connecting, red = offline.</summary>
    internal sealed class TrayIconService : IDisposable
    {
        private const int IconSize = 32;
        private const int MaxTooltipLength = 63;

        private readonly IAgentStatusStore _status;
        private readonly IWindowService _windows;
        private readonly ExitCoordinator _exit;
        private readonly IUiDispatcher _ui;
        private readonly Icon _green = CreateIcon(Color.FromArgb(46, 160, 67));
        private readonly Icon _yellow = CreateIcon(Color.FromArgb(240, 180, 20));
        private readonly Icon _red = CreateIcon(Color.FromArgb(215, 58, 73));
        private NotifyIcon? _icon;
        private ToolStripMenuItem? _statusItem;

        public TrayIconService(IAgentStatusStore status, IWindowService windows, ExitCoordinator exit, IUiDispatcher ui)
        {
            _status = status;
            _windows = windows;
            _exit = exit;
            _ui = ui;
        }

        public void Start()
        {
            _statusItem = new ToolStripMenuItem { Enabled = true };
            _statusItem.Click += (_, _) => _windows.ShowMain();

            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Open", null, (_, _) => _windows.ShowMain()));
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripMenuItem("Settings", null, (_, _) => _windows.ShowSettings()));
            menu.Items.Add(new ToolStripMenuItem("About", null, (_, _) => _windows.ShowSettings(aboutTab: true)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => _exit.RequestExit()));

            _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
            _icon.DoubleClick += (_, _) => _windows.ShowMain();
            Apply(_status.Current);
            _status.Changed += OnStatusChanged;
        }

        private void OnStatusChanged(object? sender, AgentStatusSnapshot snapshot) => _ui.Post(() => Apply(snapshot));

        private void Apply(AgentStatusSnapshot snapshot)
        {
            if (_icon is null || _statusItem is null) return;
            var text = MainViewModel.IndicatorFor(snapshot.Indicator);
            _icon.Icon = snapshot.Indicator switch
            {
                StatusIndicator.Connected => _green,
                StatusIndicator.Connecting => _yellow,
                _ => _red,
            };
            var tooltip = "Student Agent — " + text;
            _icon.Text = tooltip.Length > MaxTooltipLength ? tooltip.Substring(0, MaxTooltipLength) : tooltip;
            _statusItem.Text = "Connection Status: " + text;
        }

        private static Icon CreateIcon(Color color)
        {
            using var bitmap = new Bitmap(IconSize, IconSize);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var fill = new SolidBrush(color);
                using var border = new Pen(Color.White, 2);
                g.FillEllipse(fill, 3, 3, IconSize - 6, IconSize - 6);
                g.DrawEllipse(border, 3, 3, IconSize - 6, IconSize - 6);
            }
            var handle = bitmap.GetHicon();
            try
            {
                using var temporary = Icon.FromHandle(handle);
                return (Icon)temporary.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        public void Dispose()
        {
            _status.Changed -= OnStatusChanged;
            if (_icon is not null)
            {
                _icon.Visible = false;
                _icon.ContextMenuStrip?.Dispose();
                _icon.Dispose();
                _icon = null;
            }
            _green.Dispose();
            _yellow.Dispose();
            _red.Dispose();
        }
    }
}
