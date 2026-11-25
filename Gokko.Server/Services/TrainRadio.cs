using Gokko.Common;

namespace Gokko.Server.Services;

/// <summary>
/// 列車無線。運転士・車掌が司令を呼び出し（F6）、司令員が応答すると、その2人だけがマイクを開ける。
/// ・共通通話は参加者全員に聞こえ、個別通話は当事者の2人に聞こえる。列車の声は、個別通話でも司令員みんなに聞こえる
/// ・司令員の声は、ほかの司令員には流さない（司令員どうしは Discord で話している。列車無線は列車との交信用）
/// ・司令員は何人いてもよい。呼出は司令員みんなに届き、誰かが応答したら、ほかの司令員の一覧から消える
/// ・通話は「司令員1人と運転士・車掌1人」の組ごと。組はいくつ同時にあってもよい
/// ・一斉通告は、司令員1人が全員に話す（同時に1件まで）。運転士・車掌は応答して話せる（何人でも。声は全員に聞こえる）
/// ・司令員から列車を指定して個別に呼び出せる。相手が応答したら個別通話になる（30秒で取り消し）
/// ・音声は今の SignalR の接続で送る（40ms ずつの Opus）。サーバーは中継するだけで、記録しない
/// ・運転士・車掌は、スマホを受話器として結べる（<see cref="RadioPhones"/>。スマホの声は PC の人が話したものとして中継する）
/// （共同制作者の「列車無線」のパーツを、今のサーバーに移したもの）
/// </summary>
public sealed class TrainRadio(PlayerRegistry players, CrewRelay relay, CrewRoster crew, RadioPhones phones, TimeProvider clock)
{
    private static readonly TimeSpan RequestLife = TimeSpan.FromMinutes(2);

    private sealed record Member(string Id, string Key, string Label, bool IsDispatcher)
    {
        public string Name { get; init; } = "";

        /// <summary>聞くだけ（乗客でない添乗）。呼び出す・応答する・司令員から呼ばれることはない。</summary>
        public bool ListenOnly { get; init; }
    }

    private sealed record Request(long Id, DateTimeOffset At);

    /// <summary>司令員から列車を指定した個別呼出（応答を待っている間）。</summary>
    private sealed record Offer(long Id, string Dispatcher, string Target, DateTimeOffset At);

    private static readonly TimeSpan OfferLife = TimeSpan.FromSeconds(30);

    /// <summary>送ってよい音声のパケットの数（話す人ごと）。決まった速さより多く送られた分は捨てる。</summary>
    private sealed class Budget(DateTimeOffset now)
    {
        public DateTimeOffset CreditAt = now;
        public double Credit = BudgetBurst;
    }

    private const double BudgetBurst = 4;

    private sealed class Call
    {
        public long Id;
        public required string Dispatcher;
        public string? Caller; // null なら一斉通告
        public bool Private;
        public bool BroadcastSession; // 個別に切り替えた間も一斉通告の枠を確保する

        /// <summary>一斉通告に応答して話している運転士・車掌（何人でも）。</summary>
        public readonly HashSet<string> Responders = new();

        public readonly Dictionary<string, Budget> Budgets = new();
        public bool Has(string id) => Dispatcher == id || Caller == id || Responders.Contains(id);
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _dispatchers = new(); // 接続ID → 社員番号
    private readonly Dictionary<string, Request> _pending = new(); // 呼び出した人の接続ID → 呼出
    private readonly Dictionary<long, Call> _calls = new();
    private readonly Dictionary<long, Offer> _offers = new();
    private long _revision = 1;
    private long _serial;

    private static TrainRadioResult Result(bool ok, string message) => new() { Ok = ok, Message = message };

    // ---- 司令員 ----

    /// <summary>司令員として参加した（役職は呼ぶ側で確かめる）。</summary>
    public void AddDispatcher(string connectionId, string crewId)
    {
        lock (_gate)
        {
            _dispatchers[connectionId] = crewId;
            _revision++;
        }
    }

    public bool IsDispatcher(string connectionId)
    {
        lock (_gate) return _dispatchers.ContainsKey(connectionId);
    }

    /// <summary>参加している司令員（接続ID → 社員番号）。</summary>
    public Dictionary<string, string> Dispatchers()
    {
        lock (_gate) return new Dictionary<string, string>(_dispatchers);
    }

    public static string DispatcherLabel(string crewId) => crewId.Length > 0 ? $"司令員 ID:{crewId}" : "司令員";

    // ---- 参加者 ----

    /// <summary>無線を聞ける人（運転士・車掌・司令員）。継続乗務で残している在線は入れない。</summary>
    private List<Member> Members()
    {
        var list = new List<Member>();
        foreach (var p in players.Snapshot())
        {
            if (PlayerRegistry.IsVirtual(p.ConnectionId)) continue;
            list.Add(new Member(p.ConnectionId, p.CrewId, TrainLabel(p.TrainNumber, "運転士"), false));
        }

        foreach (var c in relay.Conductors())
            list.Add(new Member(c.ConnectionId, c.CrewId, TrainLabel(c.TrainNumber, "車掌"), false));
        // 乗客でない添乗は、聞くだけ（乗客として乗っている添乗には、列車無線は届かない）
        foreach (var r in relay.Riders())
            if (!r.Passenger)
                list.Add(new Member(r.ConnectionId, r.CrewId, TrainLabel(r.TrainNumber, "添乗"), false) { ListenOnly = true });
        foreach (var (id, crewId) in _dispatchers)
            list.Add(new Member(id, crewId, DispatcherLabel(crewId), true));
        return list.Select(m => m with { Name = m.Key.Length > 0 ? crew.InfoOf(m.Key)?.Name ?? "" : "" }).ToList();
    }

    /// <summary>参加者の名前（「1040列車（運転士）」など）。いなければ空。</summary>
    public string LabelOf(string id)
    {
        lock (_gate) return Members().FirstOrDefault(m => m.Id == id)?.Label ?? "";
    }

    private static string TrainLabel(string train, string role) => (train.Length > 0 ? $"{train}列車" : "列番未設定") + $"（{role}）";

    /// <summary>抜けた人の呼出・通話を片づける。期限の切れた呼出を消す。</summary>
    private void Clean(Dictionary<string, Member> members)
    {
        var now = clock.GetUtcNow();
        foreach (var (id, request) in _pending.ToArray())
            if (!members.ContainsKey(id) || now - request.At > RequestLife)
            {
                _pending.Remove(id);
                _revision++;
            }

        foreach (var call in _calls.Values.ToArray())
            if (!members.ContainsKey(call.Dispatcher) || (call.Caller != null && !members.ContainsKey(call.Caller)))
                EndCall(call);
            else
                foreach (var r in call.Responders.Where(r => !members.ContainsKey(r)).ToList())
                    RemoveResponder(call, r);

        // 個別呼出: 抜けた・30秒たった・相手がほかの通話に入ったら取り消す
        foreach (var offer in _offers.Values.ToArray())
            if (!members.ContainsKey(offer.Dispatcher) || !members.ContainsKey(offer.Target) || now - offer.At > OfferLife || CallOf(offer.Target) != null)
            {
                _offers.Remove(offer.Id);
                _revision++;
            }
    }

    private Dictionary<string, Member> Snapshot()
    {
        var members = Members().GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        Clean(members);
        return members;
    }

    private Call? CallOf(string id) => _calls.Values.FirstOrDefault(c => c.Has(id));
    private Offer? OfferFrom(string dispatcher) => _offers.Values.FirstOrDefault(o => o.Dispatcher == dispatcher);
    private Offer? OfferTo(string target) => _offers.Values.FirstOrDefault(o => o.Target == target);

    private void EndCall(Call call)
    {
        _calls.Remove(call.Id);
        _revision++;
    }

    private void RemoveResponder(Call call, string id)
    {
        call.Responders.Remove(id);
        call.Budgets.Remove(id);
        _revision++;
    }

    /// <summary>通話に新しい番号を振る（共通・個別を切り替えたとき、前の宛先の音声が混ざらないように）。</summary>
    private void Renumber(Call call)
    {
        _calls.Remove(call.Id);
        call.Id = ++_serial;
        call.Budgets.Clear();
        var now = clock.GetUtcNow();
        call.Budgets[call.Dispatcher] = new Budget(now);
        if (call.Caller != null) call.Budgets[call.Caller] = new Budget(now);
        foreach (var r in call.Responders) call.Budgets[r] = new Budget(now);
        _calls[call.Id] = call;
        _revision++;
    }

    // ---- 操作 ----

    public TrainRadioResult RequestCall(string id)
    {
        lock (_gate)
        {
            var members = Snapshot();
            if (!members.TryGetValue(id, out var me)) return Result(false, "サーバーに参加してから操作してください");
            if (me.IsDispatcher) return Result(false, "司令員は呼び出せません");
            if (me.ListenOnly) return Result(false, "添乗は列車無線を聞くだけです");
            if (_dispatchers.Count == 0) return Result(false, "司令員が参加していません");
            if (CallOf(id) != null) return Result(false, "通話中です");
            if (!_pending.ContainsKey(id))
            {
                _pending[id] = new Request(++_serial, clock.GetUtcNow());
                _revision++;
            }

            return Result(true, "司令を呼び出しています");
        }
    }

    public TrainRadioResult CancelRequest(string id)
    {
        lock (_gate)
        {
            if (_pending.Remove(id)) _revision++;
            // 応答と取消が行き違ったときも、取り消した人のマイクは開かない
            if (CallOf(id) is { Caller: not null } call && call.Caller == id) EndCall(call);
            return Result(true, "呼出を取り消しました");
        }
    }

    public TrainRadioResult Answer(string id, long requestId, bool privateCall)
    {
        lock (_gate)
        {
            Snapshot();
            if (!_dispatchers.ContainsKey(id)) return Result(false, "応答できるのは司令員だけです");
            if (CallOf(id) != null) return Result(false, "今の通話を終えてから応答してください");
            if (OfferFrom(id) != null) return Result(false, "個別呼出を取り消してから応答してください");
            var caller = _pending.FirstOrDefault(p => p.Value.Id == requestId).Key;
            if (caller == null) return Result(false, "この呼出は、ほかの司令員が応答したか、取り消されました");
            _pending.Remove(caller);
            var call = new Call { Dispatcher = id, Caller = caller, Private = privateCall };
            Renumber(call);
            return Result(true, privateCall ? "個別通話を始めました" : "共通通話を始めました");
        }
    }

    public TrainRadioResult StartBroadcast(string id)
    {
        lock (_gate)
        {
            Snapshot();
            if (!_dispatchers.ContainsKey(id)) return Result(false, "一斉通告は司令員だけが流せます");
            if (CallOf(id) != null) return Result(false, "今の通話を終えてから一斉通告してください");
            if (OfferFrom(id) != null) return Result(false, "個別呼出を取り消してから一斉通告してください");
            if (_calls.Values.FirstOrDefault(c => c.BroadcastSession) is { } other)
                return Result(false, $"{DispatcherLabel(_dispatchers.GetValueOrDefault(other.Dispatcher, ""))} が一斉通告中です");
            // 全員で開始チャイムを鳴らす。司令員のマイクはすぐ開く（チャイムと声は重なって流れる）
            Renumber(new Call { Dispatcher = id, BroadcastSession = true });
            return Result(true, "一斉通告を始めました（全員に聞こえます）");
        }
    }

    /// <summary>
    /// 運転士・車掌: 一斉通告に応答して話す（何人でも。声は全員に聞こえる）。
    /// 取消（<see cref="End"/>）で自分の送話だけ止め、通告はそのまま聞こえる。もう一度応答してもよい。
    /// </summary>
    public TrainRadioResult RespondBroadcast(string id, long callId)
    {
        lock (_gate)
        {
            var members = Snapshot();
            if (!members.TryGetValue(id, out var me)) return Result(false, "サーバーに参加してから操作してください");
            if (me.IsDispatcher || me.ListenOnly) return Result(false, "一斉通告に応答できるのは運転士・車掌です");
            if (!_calls.TryGetValue(callId, out var call) || call.Caller != null) return Result(false, "この一斉通告は終わっています");
            if (CallOf(id) is { } mine && mine != call) return Result(false, "通話中です");
            if (call.Responders.Add(id))
            {
                call.Budgets[id] = new Budget(clock.GetUtcNow());
                _revision++;
            }

            if (_pending.Remove(id)) _revision++; // 自分の呼出待ちは、この応答で済ませる
            return Result(true, "一斉通告に応答しています（全員に聞こえます。取消で送話を終えます）");
        }
    }

    /// <summary>司令員: 列車（運転士・車掌）を指定して個別に呼び出す。相手が応答したら個別通話になる（30秒で取り消し）。</summary>
    public TrainRadioResult CallTrain(string id, string? target)
    {
        lock (_gate)
        {
            var members = Snapshot();
            if (!_dispatchers.ContainsKey(id)) return Result(false, "個別呼出は司令員だけができます");
            if (CallOf(id) != null) return Result(false, "今の通話を終えてから呼び出してください");
            if (OfferFrom(id) != null) return Result(false, "いまの個別呼出を取り消してから呼び出してください");
            if (target == null || !members.TryGetValue(target, out var who) || who.IsDispatcher || who.ListenOnly) return Result(false, "その列車は参加していません");
            if (CallOf(target) != null) return Result(false, $"{who.Label} は通話中です");
            if (OfferTo(target) != null) return Result(false, $"{who.Label} は、ほかの司令員が呼び出しています");
            var offer = new Offer(++_serial, id, target, clock.GetUtcNow());
            _offers[offer.Id] = offer;
            _revision++;
            return Result(true, $"{who.Label} を個別に呼び出しています");
        }
    }

    /// <summary>運転士・車掌: 司令員からの個別呼出に応答する（個別通話が始まる）。</summary>
    public TrainRadioResult AcceptOffer(string id, long offerId)
    {
        lock (_gate)
        {
            Snapshot();
            if (!_offers.TryGetValue(offerId, out var offer) || offer.Target != id) return Result(false, "この呼出は終わっています");
            _offers.Remove(offerId);
            _revision++;
            if (CallOf(offer.Dispatcher) != null || CallOf(id) != null) return Result(false, "司令員が通話中になったので、つながりませんでした");
            if (_pending.Remove(id)) _revision++; // 自分の呼出待ちは、この通話で済ませる
            Renumber(new Call { Dispatcher = offer.Dispatcher, Caller = id, Private = true });
            return Result(true, "司令員と個別通話を始めました");
        }
    }

    /// <summary>個別呼出を取り消す（呼び出した司令員・呼ばれた人のどちらからでも）。</summary>
    public TrainRadioResult CancelOffer(string id, long offerId)
    {
        lock (_gate)
        {
            if (!_offers.TryGetValue(offerId, out var offer) || (offer.Dispatcher != id && offer.Target != id)) return Result(false, "この呼出は終わっています");
            _offers.Remove(offerId);
            _revision++;
            return Result(true, "個別呼出を取り消しました");
        }
    }

    public TrainRadioResult ChangeMode(string id, long callId, bool privateCall, string? responder = null)
    {
        lock (_gate)
        {
            Snapshot();
            if (!_calls.TryGetValue(callId, out var call) || call.Dispatcher != id)
                return Result(false, "通話中の司令員だけが切り替えられます");
            if (responder != null && (!call.BroadcastSession || call.Private))
                return Result(false, "共通の一斉通告中に操作してください");
            if (call.Private != privateCall)
            {
                if (call.BroadcastSession)
                {
                    if (privateCall)
                    {
                        if (responder == null || !call.Responders.Contains(responder))
                            return Result(false, "一斉通告に応答中の乗務員を選んでください");
                        call.Caller = responder;
                        call.Responders.Clear(); // 他の乗務員のマイクは閉じる。共通に戻れば再応答できる
                    }
                    else
                    {
                        call.Responders.Add(call.Caller!);
                        call.Caller = null;
                    }
                }
                call.Private = privateCall;
                Renumber(call);
            }

            return Result(true, privateCall ? "個別通話に切り替えました" : "共通通話に戻しました");
        }
    }

    public TrainRadioResult End(string id, long callId)
    {
        lock (_gate)
        {
            if (!_calls.TryGetValue(callId, out var call) || !call.Has(id)) return Result(false, "通話の当事者だけが終えられます");
            if (call.Responders.Contains(id))
            {
                RemoveResponder(call, id); // 一斉通告への応答だけ止める（通告は続く）
                return Result(true, "応答を終えました（一斉通告は引き続き聞こえます）");
            }

            EndCall(call);
            return Result(true, call.Caller == null ? "一斉通告を終えました" : "通話を終えました");
        }
    }

    /// <summary>接続が切れた（呼出・通話を片づける）。</summary>
    public void Remove(string id)
    {
        lock (_gate)
        {
            if (_pending.Remove(id)) _revision++;
            if (_dispatchers.Remove(id)) _revision++;
            foreach (var offer in _offers.Values.Where(o => o.Dispatcher == id || o.Target == id).ToList())
            {
                _offers.Remove(offer.Id);
                _revision++;
            }
            foreach (var call in _calls.Values.Where(c => c.Has(id)).ToList())
                if (call.Responders.Contains(id)) RemoveResponder(call, id);
                else EndCall(call);
        }
    }

    /// <summary>音声の宛先（接続ID）。送ってよくない音声なら空。</summary>
    public IReadOnlyList<string> Route(string id, TrainRadioPacket packet) => Route(id, packet, out _);

    /// <summary>音声の宛先（接続ID）。toMonitors は、モニター（受信だけ）にも聞かせてよい音声か（共通通話・一斉通告）。</summary>
    public IReadOnlyList<string> Route(string id, TrainRadioPacket packet, out bool toMonitors)
    {
        toMonitors = false;
        if (packet.Opus is not { Length: > 0 and <= TrainRadioFormat.MaxPacketBytes }) return [];
        lock (_gate)
        {
            if (!_calls.TryGetValue(packet.SessionId, out var call) || !call.Has(id) || !call.Budgets.TryGetValue(id, out var budget)) return [];
            var now = clock.GetUtcNow();
            budget.Credit = Math.Min(BudgetBurst,
                budget.Credit + Math.Max(0, (now - budget.CreditAt).TotalSeconds) * TrainRadioFormat.PacketsPerSecond);
            budget.CreditAt = now;
            if (budget.Credit < 1) return []; // 送りすぎ
            budget.Credit -= 1;
            packet.SenderId = id; // なりすましを許さない
            toMonitors = !call.Private;

            // 司令員の声は、ほかの司令員には送らない（司令員どうしは別の通話（Discord）で話している。列車無線は列車との交信用）
            // 運転士・車掌の声は、個別通話でも司令員みんなに届く（司令員は列車の声をすべて聞ける）
            if (_dispatchers.ContainsKey(id))
                return call.Private ? [call.Caller!] : Members().Where(m => !m.IsDispatcher).Select(m => m.Id).Distinct().ToList();
            if (call.Private)
                return _dispatchers.Keys.Where(m => m != id).Distinct().ToList();
            return Members().Select(m => m.Id).Where(m => m != id).Distinct().ToList();
        }
    }

    // ---- 配信 ----

    /// <summary>モニター（受信だけ）に見せる状態: 共通通話・一斉通告だけ（個別通話は出さない）。</summary>
    public (int Dispatchers, int Members, List<TrainRadioCall> Calls) PublicView()
    {
        lock (_gate)
        {
            var members = Snapshot();
            TrainRadioSpeaker Speaker(string id) => members.TryGetValue(id, out var m)
                ? new TrainRadioSpeaker { Id = id, Label = m.Label }
                : new TrainRadioSpeaker { Id = id };
            var calls = _calls.Values.Where(c => !c.Private).OrderBy(c => c.Id).Select(c => new TrainRadioCall
            {
                Id = c.Id, Dispatcher = Speaker(c.Dispatcher), Caller = c.Caller == null ? null : Speaker(c.Caller),
                Responders = c.Responders.Select(Speaker).OrderBy(s => s.Label, StringComparer.CurrentCulture).ToList()
            }).ToList();
            return (_dispatchers.Count, members.Values.Count(m => !m.IsDispatcher && !m.ListenOnly), calls);
        }
    }

    /// <summary>参加者ごとの列車無線の状態（接続ID → 状態）。</summary>
    public Dictionary<string, TrainRadioState> Views()
    {
        var links = phones.Links();
        lock (_gate)
        {
            var members = Snapshot();
            TrainRadioSpeaker Speaker(string id) => members.TryGetValue(id, out var m)
                ? new TrainRadioSpeaker { Id = id, Key = m.Key, Label = m.Label, Name = m.Name }
                : new TrainRadioSpeaker { Id = id };
            var calls = _calls.Values.OrderBy(c => c.Id).Select(c => (Call: c, View: new TrainRadioCall
            {
                Id = c.Id, IsPrivate = c.Private, BroadcastSession = c.BroadcastSession, Dispatcher = Speaker(c.Dispatcher), Caller = c.Caller == null ? null : Speaker(c.Caller),
                Responders = c.Responders.Select(Speaker).OrderBy(s => s.Label, StringComparer.CurrentCulture).ToList()
            })).ToList();
            TrainRadioOffer OfferView(long offerId, string who) => new() { Id = offerId, Label = members.TryGetValue(who, out var m) ? m.Label : "" };
            var targets = members.Values.Where(m => !m.IsDispatcher && !m.ListenOnly)
                .Select(m => new TrainRadioTarget { Id = m.Id, Label = m.Name.Length > 0 ? $"{m.Label}　{m.Name}" : m.Label })
                .OrderBy(t => t.Label, StringComparer.CurrentCulture).ToList();
            var requests = _pending.OrderBy(x => x.Value.Id)
                .Select(x => new TrainRadioRequest { Id = x.Value.Id, Label = members.TryGetValue(x.Key, out var m) ? m.Label : "" }).ToList();

            return members.Values.ToDictionary(m => m.Id, m => new TrainRadioState
            {
                Revision = _revision,
                SelfId = m.Id,
                IsDispatcher = m.IsDispatcher,
                DispatcherCount = _dispatchers.Count,
                MyCall = calls.FirstOrDefault(c => c.Call.Has(m.Id)).View,
                Audible = calls.Where(c => !c.Call.Private || m.IsDispatcher || c.Call.Has(m.Id)).Select(c => c.View).ToList(),
                OwnRequestPending = _pending.ContainsKey(m.Id),
                Incoming = OfferTo(m.Id) is { } inc ? OfferView(inc.Id, inc.Dispatcher) : null,
                Outgoing = OfferFrom(m.Id) is { } outg ? OfferView(outg.Id, outg.Target) : null,
                Targets = m.IsDispatcher ? targets : new(),
                PhoneLinked = links.ContainsKey(m.Id),
                Requests = m.IsDispatcher ? requests : new()
            });
        }
    }
}
