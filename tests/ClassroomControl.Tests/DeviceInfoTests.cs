using System.Net;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using ClassroomControl.StudentAgent.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClassroomControl.StudentAgent.Tests;

public sealed class DeviceInfoTests : IDisposable
{
    private readonly TestSupport.TempSettings _env = new();
    private readonly DeviceInfoService _service;

    public DeviceInfoTests()
    {
        var settings = _env.Create();
        settings.Load();
        _service = new DeviceInfoService(settings, new LocalNetworkInfo(Options.Create(new AgentOptions())));
    }

    public void Dispose() => _env.Dispose();

    [Fact]
    public void Device_id_exists_and_is_a_guid()
    {
        Assert.True(Guid.TryParse(_service.DeviceId, out var id));
        Assert.NotEqual(Guid.Empty, id);
    }

    [Fact]
    public void Computer_name_is_detected()
    {
        Assert.False(string.IsNullOrWhiteSpace(_service.ComputerName));
        Assert.Equal(Environment.MachineName, _service.ComputerName);
    }

    [Fact]
    public void Local_ip_is_a_valid_ipv4_address()
    {
        Assert.True(IPAddress.TryParse(_service.LocalIp, out var ip));
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, ip.AddressFamily);
    }

    [Fact]
    public void Full_device_info_is_populated()
    {
        var info = _service.GetDeviceInfo();
        Assert.Equal(_service.DeviceId, info.DeviceId);
        Assert.False(string.IsNullOrWhiteSpace(info.OperatingSystem));
        Assert.False(string.IsNullOrWhiteSpace(info.Cpu));
        Assert.True(info.RamBytes > 0);
        Assert.Equal("1.0.0", info.AgentVersion);
    }

    [Fact]
    public async Task Custom_computer_name_overrides_machine_name()
    {
        var settings = _env.Create();
        settings.Load();
        var s = settings.Current;
        s.ComputerName = "PC-07";
        await settings.SaveAsync(s);
        var service = new DeviceInfoService(settings, new LocalNetworkInfo(Options.Create(new AgentOptions())));
        Assert.Equal("PC-07", service.ComputerName);
    }

    [Theory]
    [InlineData("192.168.1.101", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("169.254.3.4", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("127.0.0.1", false)]
    public void Address_policy_only_permits_local_ranges(string address, bool permitted) =>
        Assert.Equal(permitted, AddressPolicy.IsPermitted(IPAddress.Parse(address), allowLoopback: false));
}
