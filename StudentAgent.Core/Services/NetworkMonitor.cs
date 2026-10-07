using System.Net.NetworkInformation;
using ClassroomControl.StudentAgent.Infrastructure.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClassroomControl.StudentAgent.Services;

public interface INetworkMonitor : IDisposable
{
    /// <summary>Raised (debounced) when the machine's IP addresses actually changed.</summary>
    event EventHandler? NetworkChanged;
    void Start();
}

public sealed class NetworkMonitor : INetworkMonitor
{
    private readonly ILocalNetworkInfo _network;
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly ILogger<NetworkMonitor> _logger;
    private readonly object _gate = new();
    private ITimer? _timer;
    private string _signature = string.Empty;
    private bool _started;

    public NetworkMonitor(ILocalNetworkInfo network, TimeProvider time, IOptions<AgentOptions> options, ILogger<NetworkMonitor> logger)
    {
        _network = network;
        _time = time;
        _debounce = TimeSpan.FromMilliseconds(options.Value.NetworkChangeDebounceMilliseconds);
        _logger = logger;
    }

    public event EventHandler? NetworkChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            _signature = _network.GetNetworkSignature();
            _timer = _time.CreateTimer(_ => OnSettled(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        NetworkChange.NetworkAddressChanged += OnSystemEvent;
        NetworkChange.NetworkAvailabilityChanged += OnSystemEvent;
    }

    private void OnSystemEvent(object? sender, EventArgs e)
    {
        lock (_gate) _timer?.Change(_debounce, Timeout.InfiniteTimeSpan); // Wi-Fi roaming fires bursts of events: wait until quiet.
    }

    private void OnSettled()
    {
        try
        {
            lock (_gate)
            {
                var current = _network.GetNetworkSignature();
                if (current == _signature) return;
                _signature = current;
            }
            _logger.LogInformation("Network change detected.");
            NetworkChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Error while handling a network change.");
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnSystemEvent;
        NetworkChange.NetworkAvailabilityChanged -= OnSystemEvent;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
