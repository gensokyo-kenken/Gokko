namespace Gokko.Common;

/// <summary>区間防護で選べる駅。</summary>
public sealed class ProtectionStation
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public override string ToString() => Name;
}

/// <summary>司令員が発報している区間防護1件。</summary>
public sealed class DispatcherProtectionRange
{
    public string FirstStationId { get; set; } = "";
    public string LastStationId { get; set; } = "";

    /// <summary>画面に出す区間（「水越～大道寺」）。</summary>
    public string Label { get; set; } = "";

    /// <summary>発報した司令員（「司令員 ID:12345678」）。</summary>
    public string By { get; set; } = "";

    public override string ToString() => $"{Label}（{By}）";
}

/// <summary>司令員へ: 区間防護の状態。</summary>
public sealed class DispatcherProtectionState
{
    public List<ProtectionStation> Stations { get; set; } = new();
    public List<DispatcherProtectionRange> Active { get; set; } = new();
}
