using ClassroomControl.StudentAgent.Services;
using Xunit;

namespace ClassroomControl.StudentAgent.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly TestSupport.TempSettings _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task Save_then_load_roundtrips_values()
    {
        var code = TestSupport.NewCode();
        var first = _env.Create();
        first.Load();
        var s = first.Current;
        s.StudentName = "Ali";
        s.ClassroomCode = code;
        s.TeacherAddress = "192.168.1.10";
        s.TeacherPort = 40000;
        s.StartWithWindows = false;
        await first.SaveAsync(s);

        var second = _env.Create();
        second.Load();
        Assert.Equal("Ali", second.Current.StudentName);
        Assert.Equal(code, second.Current.ClassroomCode);
        Assert.Equal("192.168.1.10", second.Current.TeacherAddress);
        Assert.Equal(40000, second.Current.TeacherPort);
        Assert.False(second.Current.StartWithWindows);
    }

    [Fact]
    public async Task Classroom_code_is_never_stored_in_plain_text()
    {
        var code = TestSupport.NewCode();
        var service = _env.Create();
        service.Load();
        var s = service.Current;
        s.ClassroomCode = code;
        await service.SaveAsync(s);

        var json = await File.ReadAllTextAsync(_env.Paths.SettingsFile);
        Assert.DoesNotContain(code, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ClassroomCodeProtected", json);
    }

    [Fact]
    public void Device_id_is_created_once_and_stays_stable()
    {
        var first = _env.Create();
        first.Load();
        var id = first.Current.DeviceId;
        Assert.True(Guid.TryParse(id, out _));

        var second = _env.Create();
        second.Load();
        Assert.Equal(id, second.Current.DeviceId);
    }

    [Fact]
    public async Task Device_id_cannot_be_changed_through_save()
    {
        var service = _env.Create();
        service.Load();
        var id = service.Current.DeviceId;
        var s = service.Current;
        s.DeviceId = Guid.NewGuid().ToString();
        await service.SaveAsync(s);
        Assert.Equal(id, service.Current.DeviceId);
    }

    [Fact]
    public void Corrupted_file_falls_back_to_defaults_and_keeps_a_backup()
    {
        Directory.CreateDirectory(_env.Paths.DataDirectory);
        File.WriteAllText(_env.Paths.SettingsFile, "{ this is not json");

        var service = _env.Create();
        service.Load();

        Assert.True(Guid.TryParse(service.Current.DeviceId, out _));
        Assert.True(File.Exists(_env.Paths.SettingsFile + ".corrupt"));
        Assert.Equal(39500, service.Current.DiscoveryPort);
    }

    [Fact]
    public void Out_of_range_values_in_file_are_replaced_with_defaults()
    {
        Directory.CreateDirectory(_env.Paths.DataDirectory);
        File.WriteAllText(_env.Paths.SettingsFile,
            $$"""{"DeviceId":"{{Guid.NewGuid()}}","TeacherPort":99999,"DiscoveryPort":0,"ConnectionTimeoutSeconds":-5,"TeacherAddress":"8.8.8.8"}""");

        var service = _env.Create();
        service.Load();

        Assert.Equal(0, service.Current.TeacherPort);
        Assert.Equal(39500, service.Current.DiscoveryPort);
        Assert.Equal(15, service.Current.ConnectionTimeoutSeconds);
        Assert.Equal(string.Empty, service.Current.TeacherAddress); // public address is refused
    }

    [Theory]
    [InlineData("WRONG")]
    [InlineData("CLASS-12-2026")]
    public async Task Invalid_classroom_code_is_rejected(string code)
    {
        var service = _env.Create();
        service.Load();
        var s = service.Current;
        s.ClassroomCode = code;
        var ex = await Assert.ThrowsAsync<SettingsValidationException>(() => service.SaveAsync(s));
        Assert.Contains(ex.Errors, e => e.Contains("Classroom code", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(70000, 39500, 15)]
    [InlineData(39501, 0, 15)]
    [InlineData(39501, 39500, 1)]
    public async Task Invalid_numbers_are_rejected(int teacherPort, int discoveryPort, int timeout)
    {
        var service = _env.Create();
        service.Load();
        var s = service.Current;
        s.TeacherPort = teacherPort;
        s.DiscoveryPort = discoveryPort;
        s.ConnectionTimeoutSeconds = timeout;
        await Assert.ThrowsAsync<SettingsValidationException>(() => service.SaveAsync(s));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("127.0.0.1")]
    public async Task Public_teacher_address_is_rejected(string address)
    {
        var service = _env.Create();
        service.Load();
        var s = service.Current;
        s.TeacherAddress = address;
        await Assert.ThrowsAsync<SettingsValidationException>(() => service.SaveAsync(s));
    }

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.4.2")]
    [InlineData("teacher-pc")]
    public async Task Local_teacher_address_is_accepted(string address)
    {
        var service = _env.Create();
        service.Load();
        var s = service.Current;
        s.TeacherAddress = address;
        await service.SaveAsync(s);
        Assert.Equal(address, service.Current.TeacherAddress);
    }
}
