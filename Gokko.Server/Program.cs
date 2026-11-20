using Gokko.Common;
using Gokko.Server;
using Gokko.Server.Hubs;
using Gokko.Server.Services;

// 設定・データ・管理画面は、exe と同じ場所から読む（systemd などで作業フォルダが違っても動くように）
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

var options = builder.Configuration.GetSection("Gokko").Get<ServerOptions>() ?? new ServerOptions();
var dataDir = Path.IsPathRooted(options.DataDirectory)
    ? options.DataDirectory
    : Path.Combine(AppContext.BaseDirectory, options.DataDirectory);

// データファイルの誤りは、起動時に止めて知らせる
var data = GameData.Load(dataDir);
var rail = RailData.Load(dataDir, data.Line);
// 踏切の特発を戻せる軌道回路（上下別の閉塞で）
var crossingNotes = new List<string>();
data.SetCrossingCircuits(CrossingBlocks.Load(Path.Combine(dataDir, "CrossingBlocks.csv"), data.Line, rail, data, crossingNotes));

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(options.Random); // 管理画面から書き換える（運転会中だけ有効。再起動で appsettings の値に戻る）
builder.Services.AddSingleton(data);
builder.Services.AddSingleton(rail);
builder.Services.AddSingleton(sp => new DutyRosters(dataDir, rail, sp.GetService<ILogger<DutyRosters>>())); // 行路表（Data/Duties*.csv から勤怠管理で選ぶ。既定は Duties.csv。なければ空）
builder.Services.AddSingleton(sp => new CrewRoster(dataDir, sp.GetService<ILogger<CrewRoster>>()) { PasswordUntil = options.Discord.PasswordUntil }); // 乗務員と行路の割り当て（Data/Crew.json）
builder.Services.AddSingleton<DiscordAuth>(); // 乗務員の Discord ログイン
builder.Services.AddSingleton<PlayerRegistry>();
builder.Services.AddSingleton<GameClock>();
builder.Services.AddSingleton<IncidentManager>();
builder.Services.AddSingleton<RunTimeModel>();
builder.Services.AddSingleton<SignalCalculator>();
builder.Services.AddSingleton<CentralControl>();
builder.Services.AddSingleton<ConnectionTable>(); // 接続待ちの組（ダイヤの「(接)」から）
builder.Services.AddSingleton<RouteEngine>();
builder.Services.AddSingleton<ProtectionRadio>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RadioPhones>(); // スマホの列車無線（受話器）
builder.Services.AddSingleton<RadioMonitors>(); // 列車無線のモニター（受信だけ。radio-monitor.html）
builder.Services.AddSingleton<TrainRadio>();
builder.Services.AddSingleton<DispatcherProtection>();
builder.Services.AddSingleton<RadioPublisher>();
builder.Services.AddSingleton<CrewRelay>();
builder.Services.AddSingleton<OnboardAudio>(); // 列車ごとの放送装置（車内放送・車外放送・インターホン）
builder.Services.AddSingleton<BlockSignals>(); // 閉塞信号機の定期解除と強制進行
builder.Services.AddSingleton<OccupancyLog>(); // 在線の記録（logs/occupancy-*.log）
builder.Services.AddSingleton<StateBroadcaster>();
builder.Services.AddSingleton<RelayPanels>(); // 信号制御盤（relaypanel.html）。盤の状態はサーバーだけが持ち、開いている全員に配る
builder.Services.AddSingleton(sp => new ArrivalBoards(dataDir, sp.GetRequiredService<IWebHostEnvironment>(), rail, sp.GetService<ILogger<ArrivalBoards>>())); // 駅の接近案内（tablet-arrivals.html）で見る回路
builder.Services.AddHostedService(sp => sp.GetRequiredService<StateBroadcaster>());
builder.Services.AddHostedService<RandomTokuhatsuService>();
builder.Services.AddHostedService<PhoneRadioTicker>();
builder.Services.AddSingleton(sp => new ExtensionBook(dataDir, sp.GetService<ILogger<ExtensionBook>>())); // 内線番号（Data/Extensions.json。管理画面で決める）
builder.Services.AddSingleton<ExtensionExchange>(); // 内線電話（駅・司令・乗務員。列車無線とは別の回線）
builder.Services.AddSingleton<ExtensionPublisher>();
builder.Services.AddHostedService<ExtensionTicker>();

builder.Services.AddSignalR(o =>
{
    o.KeepAliveInterval = TimeSpan.FromSeconds(10);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    o.MaximumReceiveMessageSize = 64 * 1024;
});

var app = builder.Build();

app.Logger.LogInformation("データ: 踏切 {Crossings}件、区間設定 {Sections}件、理由 {Reasons}件（{Dir}）",
    data.Crossings.Count, data.CrossingZones.Count, data.Reasons.Count, dataDir);
foreach (var n in crossingNotes) app.Logger.LogWarning("{Note}", n);
app.Logger.LogInformation("踏切の閉塞: {Count}件（CrossingBlocks.csv）", data.CrossingCircuits.Count);
app.Logger.LogInformation("進路: 駅 {Stations}件（うち扱う駅 {Control}件）、進路 {Routes}件、信号機 {Signals}件、標準運転時分 {RunTimes}区間",
    rail.Stations.Count, rail.ControlStations.Count(), rail.Routes.Count, rail.Signals.Count, rail.RunTimes.Count);

// 集中扱いの操作は、記録に残す
var central = app.Services.GetRequiredService<CentralControl>();
var incidentLog = app.Services.GetRequiredService<IncidentManager>();
central.Changed += message => incidentLog.Note(message);
rail.Turns.Changed += message => incidentLog.Note(message);
app.Services.GetRequiredService<PlayerRegistry>().Changed += message => incidentLog.Note(message);
if (string.IsNullOrEmpty(options.AdminKey))
    app.Logger.LogWarning("Gokko:AdminKey が未設定です。管理画面は使えません");
if (string.IsNullOrEmpty(options.DiaPassword))
    app.Logger.LogInformation("Gokko:DiaPassword が未設定です。司令用管理画面（diasettings.html）は使えません");
if (!options.Discord.Enabled)
    app.Logger.LogInformation("Gokko:Discord が未設定です。乗務員は社員番号とパスワードでだけログインできます");
else if (options.Discord.PasswordUntil is { } until)
    app.Logger.LogInformation("パスワードでのログインは {Until} まで", until);
if (string.IsNullOrEmpty(options.Password))
    app.Logger.LogWarning("Gokko:Password が未設定です。URL を知っていれば誰でも参加できます");

// 管理画面・ダイヤグラムの画面は、差し替えたらすぐ反映されるように、毎回確かめさせる（古い diapanel.js が残らないように）
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});
app.MapGet("/", () => Results.Redirect("/admin.html"));
app.MapGet("/healthz", (PlayerRegistry players, IncidentManager incidents) =>
    Results.Ok(new { ok = true, players = players.Count, activeIncidents = incidents.ActiveCount, protocol = Protocol.Version }));
app.MapHub<GameHub>(Protocol.HubPath);
app.MapHub<CrewHub>(CrewProtocol.HubPath);
app.MapHub<PhoneHub>(Protocol.PhoneHubPath);
app.MapHub<RadioMonitorHub>(RadioMonitorHub.Path);
app.MapHub<ExtensionHub>(ExtensionProtocol.HubPath);
app.MapHub<OnboardPhoneHub>(OnboardPhoneHub.Path); // 乗務タブレット（スマホ）のマイクで放送装置に話す
app.MapAdminEndpoints(options);
app.MapDiscordEndpoints(options);
app.MapExtensionEndpoints(options);
app.MapOnboardEndpoints(); // 乗務タブレット（スマホ）から放送装置を扱う
app.MapRelayPanelEndpoints();
app.Services.GetRequiredService<RelayPanels>(); // 起動時に駅のデータを読む（誤りをすぐ記録に出す）

app.Run();
