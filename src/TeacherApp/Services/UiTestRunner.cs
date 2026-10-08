using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClassroomControl.TeacherApp.Services
{
    /// <summary>Unattended end-to-end check used by CI on Windows (<c>--ui-test &lt;dir&gt;</c>): waits for real Student Agents, drives every
    /// feature against them through the real server, renders the window in each theme/language to PNG files and writes result.json.
    /// The process exit code is 0 only when every step passed.</summary>
    internal sealed class UiTestRunner
    {
        public const string UserName = "ci";
        public const string Password = "ci-Password-1!";
        private static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

        private readonly TeacherRuntime _runtime;
        private readonly MainViewModel _main;
        private readonly Window _window;
        private readonly string _outDir;
        private readonly int _expected;
        private readonly IThemeService _theme;
        private readonly List<object> _results = [];

        public UiTestRunner(TeacherRuntime runtime, MainViewModel main, Window window, string outDir, int expected, IThemeService theme)
        {
            _runtime = runtime;
            _main = main;
            _window = window;
            _outDir = outDir;
            _expected = expected;
            _theme = theme;
        }

        public async Task RunAsync()
        {
            Directory.CreateDirectory(_outDir);
            var failed = false;
            try
            {
                await ApproveAndWaitAsync();
                await Check("devices connected", () => Task.FromResult(_runtime.Server.Devices.Count(d => d.Online && d.IsApproved) >= _expected));

                var ids = _runtime.Server.Devices.Where(d => d.Online && d.IsApproved).Select(d => d.DeviceId).ToList();
                var first = ids[0];
                var server = _runtime.Server;

                await Check("ping", async () => (await server.ExecuteAsync(first, "Ping", null)).Success);
                await Check("device info", async () => (await server.ExecuteAsync(first, "GetDeviceInfo", null)).Success);
                await Check("screenshot (real GDI capture + JPEG)", async () =>
                {
                    var (outcome, record) = await server.TakeScreenshotAsync(first);
                    if (!outcome.Success || record is null) { Note(outcome.Message); return false; }
                    var bytes = await File.ReadAllBytesAsync(record.FilePath);
                    File.Copy(record.FilePath, Path.Combine(_outDir, "student-screenshot.jpg"), true);
                    return bytes.Length > 1000 && bytes[0] == 0xFF && bytes[1] == 0xD8;
                });
                await Check("monitoring frames arrive", async () =>
                {
                    var got = new TaskCompletionSource<bool>();
                    void OnFrame(object? s, FrameReceivedEventArgs e) => got.TrySetResult(true);
                    server.FrameReceived += OnFrame;
                    try
                    {
                        await _runtime.Monitoring.StartAllAsync();
                        return await Task.WhenAny(got.Task, Task.Delay(Step)) == got.Task;
                    }
                    finally { server.FrameReceived -= OnFrame; }
                });
                await Check("message window", async () => (await server.SendMessageAsync(first, "CI: test xabari")).Success);
                await Check("lock + unlock", async () =>
                {
                    var locked = (await server.LockAsync(first, "CI: bloklandi")).Success;
                    await Task.Delay(1500);
                    return locked && (await server.UnlockAsync(first)).Success;
                });
                await Check("remote control (real SendInput, harmless move)", async () =>
                {
                    if (!(await server.StartRemoteControlAsync(first)).Success) return false;
                    await server.SendMouseAsync(first, new MouseEventMessage(MouseAction.Move, 0.5, 0.5, MouseButtonKind.None, 0));
                    await Task.Delay(800);
                    return (await server.StopRemoteControlAsync(first)).Success;
                });
                await Check("teacher screen sharing", async () =>
                {
                    var share = new TeacherScreenShareService(server, new ClassroomControl.Platform.GdiScreenSource(), new ClassroomControl.Platform.JpegFrameEncoder());
                    await share.StartAsync(() => ids, "CI");
                    await Task.Delay(3000);
                    var ok = share.IsSharing;
                    await share.DisposeAsync();
                    return ok;
                });
                await Check("start + stop application (real process)", async () =>
                {
                    if (!(await server.StartApplicationAsync(first, "notepad.exe")).Success) return false;
                    await Task.Delay(1500);
                    return (await server.StopApplicationAsync(first, "notepad")).Success;
                });
                await Check("status update carries CPU/RAM", () => Task.FromResult(_runtime.Server.GetDevice(first)?.Status is { RamTotalBytes: > 0 }));

                foreach (var (theme, language) in new[] { ("Light", "uz"), ("Dark", "uz"), ("Light", "en"), ("Dark", "ru") })
                {
                    _theme.Apply(theme);
                    Loc.Instance.Language = language;
                    await Task.Delay(1500);
                    Render(Path.Combine(_outDir, $"teacher-{theme.ToLowerInvariant()}-{language}.png"));
                }
                _theme.Apply("Light");
                Loc.Instance.Language = "uz";
                foreach (var tab in Enum.GetValues<SettingsTab>().Where(t => t is SettingsTab.General or SettingsTab.Logs))
                {
                    var dialogs = new DialogService(_runtime.Server, _runtime.Users, _runtime.Session, _runtime.Monitoring, new WpfDispatcher(), _theme);
                    dialogs.ShowSettings(tab);
                    await Task.Delay(1200);
                    if (Application.Current.Windows.OfType<Views.SettingsWindow>().FirstOrDefault() is { } w)
                    {
                        Render(Path.Combine(_outDir, $"teacher-settings-{tab.ToString().ToLowerInvariant()}.png"), w);
                        w.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                failed = true;
                _results.Add(new { step = "unexpected exception", ok = false, note = ex.ToString() });
            }

            failed |= _results.Any(r => r.GetType().GetProperty("ok")?.GetValue(r) is false);
            await File.WriteAllTextAsync(Path.Combine(_outDir, "result.json"), JsonSerializer.Serialize(new { ok = !failed, steps = _results }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown(failed ? 1 : 0));
        }

        private string _note = string.Empty;
        private void Note(string text) => _note = text;

        private async Task ApproveAndWaitAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var pending in _runtime.Server.Devices.Where(d => d.Online && d.Computer.Status == ClassroomControl.Shared.Models.RegistrationState.Pending))
                    await _runtime.Server.ApproveAsync(pending.DeviceId);
                if (_runtime.Server.Devices.Count(d => d.Online && d.IsApproved) >= _expected) return;
                await Task.Delay(500);
            }
        }

        private async Task Check(string name, Func<Task<bool>> step)
        {
            _note = string.Empty;
            bool ok;
            try { ok = await step(); }
            catch (Exception ex) { ok = false; _note = ex.Message; }
            _results.Add(new { step = name, ok, note = _note });
        }

        private void Render(string path, Window? target = null)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var window = target ?? _window;
                window.UpdateLayout();
                var width = (int)Math.Max(1, window.ActualWidth);
                var height = (int)Math.Max(1, window.ActualHeight);
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(path);
                encoder.Save(stream);
            });
        }
    }
}
