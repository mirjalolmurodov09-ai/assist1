using System.Globalization;

namespace ClassroomControl.TeacherApp.ViewModels;

/// <summary>One computer card in the grid.</summary>
public sealed class ComputerViewModel : ObservableObject
{
    private DeviceSnapshot _snapshot;
    private object? _preview;
    private bool _isSelected;
    private string _groupName = string.Empty;

    public ComputerViewModel(DeviceSnapshot snapshot) => _snapshot = snapshot;

    public DeviceSnapshot Snapshot => _snapshot;
    public string DeviceId => _snapshot.DeviceId;
    public string Title => _snapshot.Computer.Title;
    public string ComputerName => _snapshot.Computer.ComputerName;
    public string StudentName => string.IsNullOrWhiteSpace(_snapshot.Computer.StudentName) ? "—" : _snapshot.Computer.StudentName;
    public string IpAddress => string.IsNullOrWhiteSpace(_snapshot.Computer.IpAddress) ? "—" : _snapshot.Computer.IpAddress;
    public string MacAddress => _snapshot.Computer.MacAddress;
    public bool IsOnline => _snapshot.Online;
    public bool IsPending => _snapshot.Computer.Status == RegistrationState.Pending;
    public bool IsApproved => _snapshot.IsApproved;
    public bool IsLocked => _snapshot.Locked;
    public bool IsStreaming => _snapshot.Streaming;
    public bool IsRemoteControlled => _snapshot.RemoteControlActive;
    public bool CanBeCommanded => IsOnline && IsApproved;
    public long? GroupId => _snapshot.Computer.GroupId;

    public string GroupName
    {
        get => _groupName;
        set => Set(ref _groupName, value);
    }

    public string StatusText => !IsApproved ? Loc.Instance[IsPending ? "Status.Pending" : "Status.Rejected"]
        : IsOnline ? Loc.Instance["Status.Online"] : Loc.Instance["Status.Offline"];

    public string CpuText => IsOnline && _snapshot.Status is not null ? $"{_snapshot.CpuPercent} %" : "—";

    public string CpuModel => string.IsNullOrWhiteSpace(_snapshot.Computer.Cpu) ? "—" : _snapshot.Computer.Cpu;

    public string RamText
    {
        get
        {
            var total = _snapshot.Status?.RamTotalBytes is > 0 ? _snapshot.Status.RamTotalBytes : _snapshot.Computer.RamBytes;
            if (total <= 0) return "—";
            var gb = 1024.0 * 1024 * 1024;
            return _snapshot.Status is { RamUsedBytes: > 0 } s && IsOnline
                ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} / {1:0.0} GB", s.RamUsedBytes / gb, total / gb)
                : string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", total / gb);
        }
    }

    public string PingText => IsOnline && _snapshot.Status is not null ? string.Format(CultureInfo.InvariantCulture, "{0} ms", _snapshot.PingMilliseconds) : "—";

    public string QualityText
    {
        get
        {
            if (!IsOnline || _snapshot.Status is null) return "—";
            var ping = _snapshot.PingMilliseconds;
            return Loc.Instance[ping <= 30 ? "Quality.Excellent" : ping <= 100 ? "Quality.Good" : "Quality.Poor"];
        }
    }

    /// <summary>The latest screen image (a WPF bitmap in the app); set by the preview service.</summary>
    public object? Preview
    {
        get => _preview;
        set => Set(ref _preview, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public void Update(DeviceSnapshot snapshot)
    {
        _snapshot = snapshot;
        foreach (var name in new[]
        {
            nameof(Snapshot), nameof(Title), nameof(ComputerName), nameof(StudentName), nameof(IpAddress), nameof(MacAddress), nameof(IsOnline),
            nameof(IsPending), nameof(IsApproved), nameof(IsLocked), nameof(IsStreaming), nameof(IsRemoteControlled), nameof(CanBeCommanded),
            nameof(StatusText), nameof(CpuText), nameof(CpuModel), nameof(RamText), nameof(PingText), nameof(QualityText), nameof(GroupId),
        }) Raise(name);
        if (!snapshot.Online) Preview = null; // an offline computer shows no stale image
    }

    public void RefreshLanguage()
    {
        Raise(nameof(StatusText));
        Raise(nameof(QualityText));
    }
}

/// <summary>An entry of the left-hand filter list: everything, online, offline, waiting for approval, or one group.</summary>
public sealed class GroupFilter : ObservableObject
{
    private int _count;

    public GroupFilter(string key, string? titleKey, string? literalTitle, long? groupId)
    {
        Key = key;
        TitleKey = titleKey;
        LiteralTitle = literalTitle;
        GroupId = groupId;
    }

    public string Key { get; }
    public string? TitleKey { get; }
    public string? LiteralTitle { get; }
    public long? GroupId { get; }
    public string Title => LiteralTitle ?? Loc.Instance[TitleKey!];
    public string Display => $"{Title} ({_count})";

    public int Count
    {
        get => _count;
        set
        {
            if (Set(ref _count, value)) Raise(nameof(Display));
        }
    }

    public void RefreshLanguage()
    {
        Raise(nameof(Title));
        Raise(nameof(Display));
    }

    public const string AllKey = "all", OnlineKey = "online", OfflineKey = "offline", PendingKey = "pending";
}
