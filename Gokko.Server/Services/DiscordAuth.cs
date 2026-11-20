using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gokko.Server.Services;

/// <summary>
/// 乗務員の Discord ログイン（OAuth2 の認可コード。https://docs.discord.com/developers/topics/oauth2）。
/// ・スコープは identify だけ（メールアドレスは取らない）。Discord のパスワードは Discord のページで入れるので、このサーバーには来ない
/// ・受け取るのは Discord のユーザー ID とユーザー名だけ。ID は鍵（IdKey）つきの HMAC にしてから名簿に書く。生の ID は保存しない
/// ・Discord のアクセストークンは、ID を読んだらすぐ Discord に返す（revoke）。保存しない
/// ・ユーザー名は、申請のときだけ持つ（担当者が本人か確かめる用。承認・却下で消える）
/// ・始めるとき（Begin）に、使い捨ての state を作る。戻ってきたとき（Callback）に state を確かめ、何のためのログインかを思い出す
/// ・PC のクライアントはブラウザで Discord に入る。結果はサーバーが預かり、クライアントが poll の番号で取りに来る（ループバックで受けない）
/// </summary>
public sealed class DiscordAuth
{
    private const string AuthorizeUrl = "https://discord.com/oauth2/authorize";
    private const string TokenUrl = "https://discord.com/api/oauth2/token";
    private const string RevokeUrl = "https://discord.com/api/oauth2/token/revoke";
    private const string MeUrl = "https://discord.com/api/v10/users/@me";

    private static readonly TimeSpan StateLife = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PollLife = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TicketLife = TimeSpan.FromMinutes(30);
    private const int MaxPending = 500;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly DiscordOptions _o;
    private readonly ILogger<DiscordAuth>? _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, Pending> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PollResult> _polls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);

    public DiscordAuth(ServerOptions options, ILogger<DiscordAuth>? log = null)
    {
        _o = options.Discord;
        _log = log;
    }

    public bool Enabled => _o.Enabled;

    /// <summary>何のためのログインか。Login: Discord でログイン。Link: ログインしている人（LinkUser）に紐づける。</summary>
    public enum Mode { Login, Link }

    /// <param name="ReturnTo">終わったら戻る画面（/ で始まる、このサーバーの中のパス）。</param>
    /// <param name="Poll">PC のクライアントが結果を取りに来る番号（ブラウザの画面でなら null）。</param>
    public sealed record Pending(Mode Mode, string? LinkUser, string ReturnTo, string? Poll, DateTime Until);

    /// <param name="Status">wait（まだ）・ok（ログインした）・linked（紐づけた）・apply（申請がいる）・error。</param>
    public sealed record PollResult(string Status, string? Id, string? Token, string? Message, DateTime Until);

    /// <param name="Poll">PC のクライアントから始めたときの poll の番号（その場で紐づけたら、クライアントに合い札を渡す）。</param>
    /// <param name="ReturnTo">ブラウザの画面から始めたときに戻る画面。</param>
    public sealed record Ticket(string Hash, string DiscordName, string? Poll, string ReturnTo, DateTime Until);

    /// <summary>Discord のユーザー ID を、名簿に書く形（HMAC-SHA256・16進）にする。</summary>
    public string HashId(string discordId)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(_o.IdKey));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(discordId)));
    }

    private static string NewCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(20));

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var k in _states.Where(x => x.Value.Until <= now).Select(x => x.Key).ToList()) _states.Remove(k);
        foreach (var k in _polls.Where(x => x.Value.Until <= now).Select(x => x.Key).ToList()) _polls.Remove(k);
        foreach (var k in _tickets.Where(x => x.Value.Until <= now).Select(x => x.Key).ToList()) _tickets.Remove(k);
    }

    /// <summary>Discord のページへの URL を作る。poll なら、クライアントが結果を取りに来る番号も返す。多すぎたら null。</summary>
    public (string? Url, string? Poll) Begin(Mode mode, string? linkUser, string returnTo, bool poll)
    {
        lock (_lock)
        {
            Prune();
            if (_states.Count >= MaxPending) return (null, null);
            var state = NewCode();
            var pollCode = poll ? NewCode() : null;
            _states[state] = new Pending(mode, linkUser, returnTo, pollCode, DateTime.UtcNow + StateLife);
            if (pollCode != null) _polls[pollCode] = new PollResult("wait", null, null, null, DateTime.UtcNow + PollLife);
            var url = AuthorizeUrl
                      + "?response_type=code"
                      + "&client_id=" + Uri.EscapeDataString(_o.ClientId)
                      + "&scope=identify"
                      + "&state=" + state
                      + "&redirect_uri=" + Uri.EscapeDataString(_o.RedirectUri);
            return (url, pollCode);
        }
    }

    /// <summary>戻ってきた state を確かめる（1回だけ使える）。</summary>
    public Pending? TakeState(string? state)
    {
        if (string.IsNullOrEmpty(state)) return null;
        lock (_lock)
            return _states.Remove(state, out var p) && p.Until > DateTime.UtcNow ? p : null;
    }

    /// <summary>認可コードを、Discord のユーザー ID とユーザー名に替える。アクセストークンは使ったらすぐ返す。うまくいかなければ null。</summary>
    public async Task<(string Id, string Name)?> Identify(string code)
    {
        string? access = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = _o.RedirectUri
                })
            };
            req.Headers.Authorization = Basic();
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                _log?.LogWarning("Discord: 認可コードを替えられませんでした（{Status}）", (int)res.StatusCode);
                return null;
            }

            using (var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync()))
                access = doc.RootElement.GetProperty("access_token").GetString();
            if (string.IsNullOrEmpty(access)) return null;

            using var me = new HttpRequestMessage(HttpMethod.Get, MeUrl);
            me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var mres = await Http.SendAsync(me);
            if (!mres.IsSuccessStatusCode)
            {
                _log?.LogWarning("Discord: ユーザーを読めませんでした（{Status}）", (int)mres.StatusCode);
                return null;
            }

            using var u = JsonDocument.Parse(await mres.Content.ReadAsStringAsync());
            var id = u.RootElement.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(id)) return null;
            var name = u.RootElement.TryGetProperty("global_name", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
            var user = u.RootElement.TryGetProperty("username", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            return (id, string.IsNullOrEmpty(name) ? user ?? "" : $"{name}（@{user}）");
        }
        catch (Exception ex)
        {
            _log?.LogWarning("Discord: つながりませんでした（{Error}）", ex.GetType().Name); // 例外の文面には URL が入ることがあるので出さない
            return null;
        }
        finally
        {
            if (access != null) await Revoke(access);
        }
    }

    private AuthenticationHeaderValue Basic() =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(_o.ClientId) + ":" + Uri.EscapeDataString(_o.ClientSecret))));

    private async Task Revoke(string access)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, RevokeUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = access, ["token_type_hint"] = "access_token" })
            };
            req.Headers.Authorization = Basic();
            using var _ = await Http.SendAsync(req);
        }
        catch
        {
            // 返せなくても、トークンは保存していないので、期限で切れる
        }
    }

    /// <summary>クライアントに渡す結果を預ける。</summary>
    public void SetPoll(string poll, string status, string? id = null, string? token = null, string? message = null)
    {
        lock (_lock) _polls[poll] = new PollResult(status, id, token, message, DateTime.UtcNow + PollLife);
    }

    /// <summary>クライアントが結果を取りに来る（wait 以外は1回で消える）。番号がなければ null。</summary>
    public PollResult? TakePoll(string? poll)
    {
        if (string.IsNullOrEmpty(poll)) return null;
        lock (_lock)
        {
            Prune();
            if (!_polls.TryGetValue(poll, out var r)) return null;
            if (r.Status != "wait") _polls.Remove(poll);
            return r;
        }
    }

    /// <summary>名簿にいない人が申請するための札（30分）。</summary>
    public string IssueTicket(string hash, string discordName, string? poll, string returnTo)
    {
        lock (_lock)
        {
            Prune();
            var t = NewCode();
            _tickets[t] = new Ticket(hash, discordName, poll, returnTo, DateTime.UtcNow + TicketLife);
            if (poll != null) _polls[poll] = new PollResult("wait", null, null, null, DateTime.UtcNow + TicketLife); // 申請の画面にいるあいだ、クライアントは待つ
            return t;
        }
    }

    public Ticket? PeekTicket(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return null;
        lock (_lock) return _tickets.TryGetValue(ticket, out var t) && t.Until > DateTime.UtcNow ? t : null;
    }

    public void DropTicket(string ticket)
    {
        lock (_lock) _tickets.Remove(ticket);
    }
}
