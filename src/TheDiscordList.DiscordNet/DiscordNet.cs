using System.Globalization;
using System.Reflection;
using Discord;
using Discord.WebSocket;

namespace TheDiscordList.DiscordNet;

public sealed partial class TdlAutoposter
{
    internal const string LibraryName = "DiscordNet";
    internal const string RepositoryUrl = "https://github.com/TheDiscordList/integration-discordnet";

    /// <summary>Starts publishing for a <see cref="DiscordSocketClient"/>. Safe to call before or after login.</summary>
    /// <param name="client">The client.</param>
    /// <param name="token">The listing's stats token. Missing or blank logs a warning and returns an inert poster.</param>
    /// <param name="options">Optional settings.</param>
    public TdlAutoposter(DiscordSocketClient client, string? token, TdlOptions? options = null)
        : this(new DiscordNetHost(client, () => [client], processPerShard: true), token, options)
    {
        ((DiscordNetHost)Host).Poster = this;
        if (IsInert)
        {
            return;
        }

        Func<Task> ready = () => OnReady(client);
        Func<Task> connected = () => Transition();
        Func<Exception, Task> disconnected = _ => Transition();
        Func<SocketGuild, Task> guildsChanged = _ => GuildsChanged();

        client.Ready += ready;
        client.Connected += connected;
        client.Disconnected += disconnected;
        client.JoinedGuild += guildsChanged;
        client.LeftGuild += guildsChanged;
        _detach.Add(() =>
        {
            client.Ready -= ready;
            client.Connected -= connected;
            client.Disconnected -= disconnected;
            client.JoinedGuild -= guildsChanged;
            client.LeftGuild -= guildsChanged;
        });

        if (client.ConnectionState == ConnectionState.Connected)
        {
            _ = OnReady(client);
        }
    }

    /// <summary>Starts publishing for a <see cref="DiscordShardedClient"/>. Safe to call before or after login.</summary>
    /// <param name="client">The client.</param>
    /// <param name="token">The listing's stats token. Missing or blank logs a warning and returns an inert poster.</param>
    /// <param name="options">Optional settings.</param>
    public TdlAutoposter(DiscordShardedClient client, string? token, TdlOptions? options = null)
        : this(new DiscordNetHost(client, () => client.Shards ?? [], processPerShard: false), token, options)
    {
        ((DiscordNetHost)Host).Poster = this;
        if (IsInert)
        {
            return;
        }

        Func<DiscordSocketClient, Task> ready = _ => OnReady(client);
        Func<DiscordSocketClient, Task> connected = _ => Transition();
        Func<Exception, DiscordSocketClient, Task> disconnected = (_, _) => Transition();
        Func<SocketGuild, Task> guildsChanged = _ => GuildsChanged();

        client.ShardReady += ready;
        client.ShardConnected += connected;
        client.ShardDisconnected += disconnected;
        client.JoinedGuild += guildsChanged;
        client.LeftGuild += guildsChanged;
        _detach.Add(() =>
        {
            client.ShardReady -= ready;
            client.ShardConnected -= connected;
            client.ShardDisconnected -= disconnected;
            client.JoinedGuild -= guildsChanged;
            client.LeftGuild -= guildsChanged;
        });

        if (client.Shards?.Any(shard => shard.ConnectionState == ConnectionState.Connected) == true)
        {
            _ = OnReady(client);
        }
    }

    /// <summary>
    /// Log output, in the same shape as <c>DiscordSocketClient.Log</c>, so the client's
    /// handler can be reused. Without a handler, warnings go to standard error.
    /// </summary>
    public event Func<LogMessage, Task>? Log;

    private Task Transition()
    {
        OnShardTransition();
        return Task.CompletedTask;
    }

    private Task GuildsChanged()
    {
        OnGuildsChanged();
        return Task.CompletedTask;
    }

    /// <summary>Returns at once: a gateway handler that blocks stalls the client.</summary>
    private Task OnReady(BaseSocketClient client)
    {
        _ = StartFromAsync(client);
        return Task.CompletedTask;
    }

    private async Task StartFromAsync(BaseSocketClient client)
    {
        try
        {
            var botId = BotId ?? (await client.GetApplicationInfoAsync().ConfigureAwait(false)).Id;
            Start(botId);
        }
        catch (Exception exception)
        {
            WarnOnce("bot-id", $"Could not read the application id ({exception.Message}). Set TdlOptions.BotId.");
        }
    }

    /// <summary>False when nobody handles <see cref="Log"/>.</summary>
    internal bool EmitLog(LogMessage message)
    {
        var handler = Log;
        if (handler is null)
        {
            return false;
        }

        try
        {
            _ = handler(message);
        }
        catch (Exception)
        {
        }

        return true;
    }
}

/// <summary>Creates a <see cref="TdlAutoposter"/> from a client.</summary>
public static class TdlDiscordNetExtensions
{
    /// <summary>Publishes this client's stats, heartbeats and commands to TheDiscordList.</summary>
    /// <param name="client">The client.</param>
    /// <param name="token">The listing's stats token.</param>
    /// <param name="options">Optional settings.</param>
    /// <returns>The running poster. Dispose it to stop.</returns>
    public static TdlAutoposter AddTheDiscordList(this DiscordSocketClient client, string? token, TdlOptions? options = null) => new(client, token, options);

    /// <inheritdoc cref="AddTheDiscordList(DiscordSocketClient, string?, TdlOptions?)"/>
    public static TdlAutoposter AddTheDiscordList(this DiscordShardedClient client, string? token, TdlOptions? options = null) => new(client, token, options);
}

internal sealed class DiscordNetHost(BaseSocketClient client, Func<IReadOnlyCollection<DiscordSocketClient>> shards, bool processPerShard) : ITdlHost
{
    /// <summary>Discord.Net keeps the total shard count internal. Unreadable means unknown, never a guess.</summary>
    private static readonly PropertyInfo? TotalShards =
        typeof(DiscordSocketClient).GetProperty("TotalShards", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    public TdlAutoposter? Poster { get; set; }

    public TdlTopology Topology
    {
        get
        {
            var local = shards();
            var total = local.Count > 0 && TotalShards?.GetValue(local.First()) is int value && value > 0 ? value : (int?)null;
            return TdlTopology.Detect(local.Select(shard => shard.ShardId).ToList(), total, processPerShard);
        }
    }

    public IReadOnlyList<TdlShard> Shards => shards().Select(Report).ToList();

    public int? LocalServerCount
    {
        get
        {
            var local = shards();
            return local.Count > 0 && local.All(shard => shard.ConnectionState == ConnectionState.Connected)
                ? local.Sum(shard => shard.Guilds.Count)
                : null;
        }
    }

    public async Task<IReadOnlyList<TdlCommand>> GetCommandsAsync()
    {
        var commands = await client.Rest.GetGlobalApplicationCommands().ConfigureAwait(false);
        return commands.Select(Map).ToList();
    }

    public void Log(TdlLogLevel level, string message)
    {
        var severity = level == TdlLogLevel.Warning ? LogSeverity.Warning : LogSeverity.Info;
        if (Poster?.EmitLog(new LogMessage(severity, "TheDiscordList", message)) != true && level == TdlLogLevel.Warning)
        {
            Console.Error.WriteLine("[TheDiscordList] " + message);
        }
    }

    /// <summary>
    /// Discord.Net reads an absent <c>default_member_permissions</c> as 0, so 0 is sent as
    /// absent: "everyone" is far more common than "administrators only".
    /// </summary>
    internal static TdlCommand Map(IApplicationCommand command) => new(
        (int)command.Type,
        command.Name,
        string.IsNullOrEmpty(command.Description) ? null : command.Description,
        command.IsNsfw,
        command.DefaultMemberPermissions.RawValue == 0 ? null : command.DefaultMemberPermissions.RawValue.ToString(CultureInfo.InvariantCulture),
        Map(command.Options));

    private static List<TdlCommandOption>? Map(IEnumerable<IApplicationCommandOption>? options)
    {
        var list = options?.Select(option => new TdlCommandOption(
            (int)option.Type,
            option.Name,
            option.Description,
            option.IsRequired,
            option.Choices is { Count: > 0 } choices ? choices.Select(choice => new TdlCommandChoice(choice.Name, choice.Value)).ToList() : null,
            Map(option.Options))).ToList();
        return list is { Count: > 0 } ? list : null;
    }

    private static TdlShard Report(DiscordSocketClient shard)
    {
        var status = shard.ConnectionState switch
        {
            ConnectionState.Connected => TdlShard.Ready,
            ConnectionState.Connecting => TdlShard.Connecting,
            _ => TdlShard.Disconnected,
        };
        var latency = status == TdlShard.Ready ? TdlShard.Latency(shard.Latency) : null;
        return new TdlShard(shard.ShardId, status, latency, shard.Guilds.Count);
    }
}
