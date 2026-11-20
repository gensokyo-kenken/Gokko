using Gokko.Common;
using Gokko.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Gokko.Server.Services;

/// <summary>
/// 列車ごとの放送装置（車内放送・車外放送・インターホン）。届くのは同じ列番の運転士・車掌・添乗の中だけ（ほかの列車・司令には届かない）。
/// ・車内放送・車外放送: 運転士か車掌が、どちらか片方を流す（列車ごとに同時に1人・1種類だけ。先に流した人が止めるまで、ほかの人は流せない）。
///   聞こえるのは、流している人以外の全員（添乗は乗客でも）
/// ・インターホン（連絡）: 運転士と車掌の打ち合わせ。入にしている運転士・車掌どうしと、乗客でない添乗に聞こえる。話せるのは入にしている運転士・車掌だけ
/// ・1人が選べるのは、車内・車外・連絡のどれか1つだけ（ゲームの放送操作盤の切替と同じ）。放送を始めたら自分の連絡は切れ、連絡を入れたら自分の放送は止まる
/// ・車掌は、中継（/crew）で運転士に乗務を許可された人だけを数える（列番を入れただけの人には聞かせない）
/// ・音声は列車無線と同じ形（40ms ずつの Opus）。サーバーは中身を見ずに中継し、記録しない
/// </summary>
public sealed class OnboardAudio(PlayerRegistry players, CrewRelay relay, CrewRoster crew, IHubContext<GameHub> hub, ILogger<OnboardAudio> logger)
{
    private sealed record Person(string Id, CrewRole Role, string Train, string CrewId, bool Passenger);

    /// <summary>送ってよい音声のパケットの数（話す人ごと）。決まった速さより多く送られた分は捨てる。</summary>
    private sealed class Budget
    {
        public DateTime CreditAt = DateTime.UtcNow;
        public double Credit = BudgetBurst;
    }

    private const double BudgetBurst = 4;

    private readonly object _gate = new();
    private readonly HashSet<string> _intercom = new(); // インターホンを入にしている運転士・車掌（接続ID）
    private readonly Dictionary<string, (string Sender, OnboardChannel Channel)> _broadcasts = new(); // 列番 → 流している人と種類
    private readonly Dictionary<string, Budget> _budgets = new();
    private readonly Dictionary<string, string> _phoneMics = new(); // 社員番号 → スマホのマイクを入にしている接続（乗務タブレット）

    // 宛先を決めるための、列番ごとの顔ぶれ（音声のたびに作り直さない。0.3秒ごとと、変わったときに作り直す）
    private Dictionary<string, Person> _people = new();
    private DateTime _peopleAt = DateTime.MinValue;

    private static TrainRadioResult Result(bool ok, string message) => new() { Ok = ok, Message = message };

    /// <summary>放送装置を使える人（運転士・許可された車掌・添乗）。列番のない人は入れない。</summary>
    private Dictionary<string, Person> People(bool fresh = false)
    {
        var now = DateTime.UtcNow;
        if (!fresh && now - _peopleAt < TimeSpan.FromMilliseconds(300)) return _people;
        var list = new Dictionary<string, Person>();
        foreach (var p in players.Snapshot())
        {
            if (p.ConnectionId.StartsWith(PlayerRegistry.GhostPrefix) || p.TrainNumber.Length == 0) continue;
            list[p.ConnectionId] = new Person(p.ConnectionId, CrewRole.Driver, p.TrainNumber, p.CrewId, false);
        }

        foreach (var c in relay.Conductors())
            if (c.TrainNumber.Length > 0 && relay.IsApprovedConductor(c.CrewId, c.TrainNumber))
                list[c.ConnectionId] = new Person(c.ConnectionId, CrewRole.Conductor, c.TrainNumber, c.CrewId, false);
        foreach (var r in relay.Riders())
            if (r.TrainNumber.Length > 0)
                list[r.ConnectionId] = new Person(r.ConnectionId, CrewRole.Rider, r.TrainNumber, r.CrewId, r.Passenger);

        _people = list;
        _peopleAt = now;

        // いなくなった人・列番を変えた人の放送とインターホンを片づける
        foreach (var (train, b) in _broadcasts.ToList())
            if (!list.TryGetValue(b.Sender, out var who) || who.Train != train || who.Role == CrewRole.Rider)
                _broadcasts.Remove(train);
        _intercom.RemoveWhere(id => !list.TryGetValue(id, out var who) || who.Role == CrewRole.Rider);
        foreach (var id in _budgets.Keys.Where(id => !list.ContainsKey(id)).ToList()) _budgets.Remove(id);
        return list;
    }

    private static string Name(Person p) => p.Role == CrewRole.Driver ? "運転士" : "車掌";

    /// <summary>車内放送・車外放送を流す・止める（None で止める）。</summary>
    public TrainRadioResult SetBroadcast(string id, OnboardChannel channel)
    {
        TrainRadioResult result;
        lock (_gate)
        {
            var people = People(fresh: true);
            if (!people.TryGetValue(id, out var me) || me.Role == CrewRole.Rider)
                return Result(false, me?.Role == CrewRole.Rider ? "添乗は放送を流せません" : "列番を確定して乗務に入ってから操作してください（車掌は運転士の許可が要ります）");
            var current = _broadcasts.GetValueOrDefault(me.Train);
            if (channel is not (OnboardChannel.Inside or OnboardChannel.Outside))
            {
                if (current.Sender == id) _broadcasts.Remove(me.Train);
                result = Result(true, "放送を終えました");
            }
            else if (current.Sender != null && current.Sender != id && people.TryGetValue(current.Sender, out var other))
            {
                return Result(false, $"{Name(other)}が{(current.Channel == OnboardChannel.Outside ? "車外" : "車内")}放送を流しています");
            }
            else
            {
                _broadcasts[me.Train] = (id, channel);
                _intercom.Remove(id); // 車内・車外・連絡はどれか1つ
                result = Result(true, channel == OnboardChannel.Outside ? "車外放送を流しています" : "車内放送を流しています");
            }

            logger.LogInformation("放送 {Train} {Who} {Channel}", me.Train, Name(me), channel);
        }

        _ = PublishAsync();
        return result;
    }

    /// <summary>インターホンの入・切（運転士・車掌）。</summary>
    public TrainRadioResult SetIntercom(string id, bool on)
    {
        lock (_gate)
        {
            var people = People(fresh: true);
            if (!people.TryGetValue(id, out var me) || me.Role == CrewRole.Rider)
                return Result(false, me?.Role == CrewRole.Rider ? "添乗はインターホンを使えません" : "列番を確定して乗務に入ってから操作してください（車掌は運転士の許可が要ります）");
            if (on)
            {
                _intercom.Add(id);
                if (_broadcasts.GetValueOrDefault(me.Train).Sender == id) _broadcasts.Remove(me.Train); // 車内・車外・連絡はどれか1つ
            }
            else _intercom.Remove(id);
        }

        _ = PublishAsync();
        return Result(true, on ? "連絡（インターホン）を入にしました（声を出すと、自動で送ります）" : "連絡（インターホン）を切りました");
    }

    /// <summary>社員番号の人が、運転士・車掌として乗っている接続（乗務タブレットから扱うため）。なければ null。</summary>
    public string? TalkerOf(string crewId)
    {
        lock (_gate)
            return People(fresh: true).Values.FirstOrDefault(p => p.CrewId == crewId && p.Role != CrewRole.Rider)?.Id;
    }

    /// <summary>その人から見た状態（乗っていなければ null）。</summary>
    public OnboardState? ViewOf(string id) => Views().GetValueOrDefault(id);

    /// <summary>
    /// ボタンを押した（"Inside"・"Outside"・"Intercom"）。押すたびに入・切。車内・車外・連絡はどれか1つ（入れたら、ほかは切れる）。
    /// </summary>
    public TrainRadioResult Press(string id, string action)
    {
        if (ViewOf(id) is not { } st) return Result(false, "列番を確定して乗務に入ってから操作してください");
        return action switch
        {
            "Inside" => SetBroadcast(id, st.BroadcasterId == id && st.Broadcast == OnboardChannel.Inside ? OnboardChannel.None : OnboardChannel.Inside),
            "Outside" => SetBroadcast(id, st.BroadcasterId == id && st.Broadcast == OnboardChannel.Outside ? OnboardChannel.None : OnboardChannel.Outside),
            "Intercom" => SetIntercom(id, !st.Intercom),
            _ => Result(false, "そのボタンはありません")
        };
    }

    /// <summary>接続が切れた。</summary>
    public void Remove(string id)
    {
        lock (_gate)
        {
            _intercom.Remove(id);
            _budgets.Remove(id);
            foreach (var (train, b) in _broadcasts.ToList())
                if (b.Sender == id) _broadcasts.Remove(train);
            _peopleAt = DateTime.MinValue;
        }
    }

    // ---- 乗務タブレット（スマホ）のマイク ----

    /// <summary>スマホのマイクの入・切（社員番号ごとに1台。新しく入れたスマホに替わる）。</summary>
    public void SetPhoneMic(string crewId, string phoneId, bool on)
    {
        lock (_gate)
        {
            if (on) _phoneMics[crewId] = phoneId;
            else if (_phoneMics.GetValueOrDefault(crewId) == phoneId) _phoneMics.Remove(crewId);
        }
    }

    public bool IsPhoneMic(string crewId, string phoneId)
    {
        lock (_gate) return _phoneMics.GetValueOrDefault(crewId) == phoneId;
    }

    /// <summary>スマホが切れた。マイクを入にしていたら true。</summary>
    public bool RemovePhone(string phoneId)
    {
        lock (_gate)
        {
            var key = _phoneMics.FirstOrDefault(kv => kv.Value == phoneId).Key;
            return key != null && _phoneMics.Remove(key);
        }
    }

    /// <summary>音声の宛先（接続ID）。送ってよくない音声なら空。fromPhone はスマホのマイクから（PC の接続の声として送る）。</summary>
    public IReadOnlyList<string> Route(string id, OnboardPacket packet, bool fromPhone = false)
    {
        if (packet.Opus is not { Length: > 0 and <= TrainRadioFormat.MaxPacketBytes }) return [];
        lock (_gate)
        {
            var people = People();
            if (!people.TryGetValue(id, out var me) || me.Role == CrewRole.Rider) return [];
            if (!fromPhone && _phoneMics.ContainsKey(me.CrewId)) return []; // スマホのマイクで話している間は、PC のマイクの声は捨てる
            var mates = people.Values.Where(p => p.Train == me.Train && p.Id != id);
            switch (packet.Channel)
            {
                case OnboardChannel.Inside or OnboardChannel.Outside:
                    var b = _broadcasts.GetValueOrDefault(me.Train);
                    if (b.Sender != id || b.Channel != packet.Channel) return [];
                    break;
                case OnboardChannel.Intercom:
                    if (!_intercom.Contains(id)) return [];
                    // 受ける側は［連絡］を入にしていなくても聞こえる（運転士・車掌と、乗客でない添乗）
                    mates = mates.Where(p => p.Role != CrewRole.Rider || !p.Passenger);
                    break;
                default:
                    return [];
            }

            // 送りすぎ（1秒に 25 パケットを超える分）は捨てる
            if (!_budgets.TryGetValue(id, out var budget)) _budgets[id] = budget = new Budget();
            var now = DateTime.UtcNow;
            budget.Credit = Math.Min(BudgetBurst, budget.Credit + Math.Max(0, (now - budget.CreditAt).TotalSeconds) * TrainRadioFormat.PacketsPerSecond);
            budget.CreditAt = now;
            if (budget.Credit < 1) return [];
            budget.Credit -= 1;

            packet.SenderId = id; // なりすましを許さない
            return mates.Select(p => p.Id).ToList();
        }
    }

    /// <summary>参加者ごとの放送装置の状態（接続ID → 状態）。列番のない人には作らない。</summary>
    public Dictionary<string, OnboardState> Views()
    {
        lock (_gate)
        {
            var people = People(fresh: true);
            var names = new Dictionary<string, string>();
            string NameOf(string crewId)
            {
                if (crewId.Length == 0) return "";
                if (!names.TryGetValue(crewId, out var n)) names[crewId] = n = crew.InfoOf(crewId)?.Name ?? "";
                return n;
            }

            var result = new Dictionary<string, OnboardState>();
            foreach (var group in people.Values.GroupBy(p => p.Train))
            {
                var members = group
                    .Select(p => new OnboardMember { Id = p.Id, Role = p.Role, Key = p.CrewId, Name = NameOf(p.CrewId), Intercom = _intercom.Contains(p.Id), Passenger = p.Passenger })
                    .OrderBy(m => m.Role == CrewRole.Rider ? 2 : (int)m.Role).ThenBy(m => m.Name, StringComparer.CurrentCulture).ToList();
                var b = _broadcasts.GetValueOrDefault(group.Key);
                foreach (var p in group)
                    result[p.Id] = new OnboardState
                    {
                        TrainNumber = group.Key, SelfId = p.Id, Role = p.Role, Passenger = p.Passenger, Intercom = _intercom.Contains(p.Id),
                        PhoneMic = p.Role != CrewRole.Rider && _phoneMics.ContainsKey(p.CrewId),
                        Broadcast = b.Sender != null ? b.Channel : OnboardChannel.None, BroadcasterId = b.Sender ?? "", Members = members
                    };
            }

            return result;
        }
    }

    /// <summary>状態を、すぐその列車の人たちへ送る（配信の0.5秒を待たない）。</summary>
    public async Task PublishAsync()
    {
        try
        {
            await Task.WhenAll(Views().Select(kv => hub.Clients.Client(kv.Key).SendAsync(Protocol.ReceiveOnboardState, kv.Value)));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "放送装置の状態を送れませんでした");
        }
    }

    /// <summary>音声を届ける。</summary>
    public async Task SendAudioAsync(IReadOnlyList<string> recipients, OnboardPacket packet)
    {
        if (recipients.Count == 0) return;
        try
        {
            await hub.Clients.Clients(recipients).SendAsync(Protocol.ReceiveOnboardAudio, packet);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "放送装置の音声を送れませんでした");
        }
    }
}
