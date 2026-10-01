using System.Reflection;
using System.Text.Json;
using Discord;
using Discord.WebSocket;

namespace TheDiscordList.DiscordNet.Tests;

/// <summary>Real Discord.Net clients, constructed offline: nothing here logs in.</summary>
public sealed class DiscordNetTests
{
    private static TdlOptions Offline(FakeHandler? http = null) => new() { HttpClient = new HttpClient(http ?? new FakeHandler()) };

    [Fact]
    public async Task PlainClientIsASingleCluster()
    {
        await using var client = new DiscordSocketClient();
        await using var poster = client.AddTheDiscordList("tdl_token", Offline());

        Assert.Equal(new TdlTopology(0, 1, 1, true), poster.Topology);
        Assert.Equal(new TdlShard(0, TdlShard.Disconnected, null, 0), Assert.Single(poster.Host.Shards));
        Assert.Null(poster.Host.LocalServerCount);
    }

    [Fact]
    public async Task OneShardOfSeveralIsItsOwnCluster()
    {
        await using var client = new DiscordSocketClient(new DiscordSocketConfig { ShardId = 2, TotalShards = 4 });
        await using var poster = client.AddTheDiscordList("tdl_token", Offline());

        Assert.Equal(new TdlTopology(2, 4, 4, false), poster.Topology);
    }

    [Fact]
    public async Task ShardedClientRunningEveryShardIsASingleCluster()
    {
        await using var client = new DiscordShardedClient(new DiscordSocketConfig { TotalShards = 3 });
        await using var poster = client.AddTheDiscordList("tdl_token", Offline());

        Assert.Equal(new TdlTopology(0, 1, 3, true), poster.Topology);
        Assert.Equal(new[] { 0, 1, 2 }, poster.Host.Shards.Select(shard => shard.Id));
    }

    [Fact]
    public async Task ShardedClientRunningSomeShardsNeverGuessesTheClusterCount()
    {
        var http = new FakeHandler();
        await using var client = new DiscordShardedClient([2, 3], new DiscordSocketConfig { TotalShards = 4 });
        await using var poster = client.AddTheDiscordList("tdl_token", Offline(http));
        var disabled = new List<TdlDisabled>();
        poster.Disabled += disabled.Add;

        Assert.Equal(new TdlTopology(2, null, 4, false), poster.Topology);

        poster.Start(123);

        Assert.Equal(new TdlDisabled(TdlPublisherKind.Heartbeat, null, "cluster-count-unknown"), Assert.Single(disabled));
        Assert.Empty(http.Sent);
    }

    [Fact]
    public async Task ClusterOptionOverridesDetection()
    {
        await using var client = new DiscordShardedClient([2, 3], new DiscordSocketConfig { TotalShards = 4 });
        var options = Offline();
        options.Cluster = new TdlCluster(1, 2);
        await using var poster = client.AddTheDiscordList("tdl_token", options);

        Assert.Equal(new TdlTopology(1, 2, 4, false), poster.Topology);
    }

    [Fact]
    public async Task ShardedClientBeforeLoginHasNoShardsYet()
    {
        await using var client = new DiscordShardedClient();
        await using var poster = client.AddTheDiscordList("tdl_token", Offline());

        Assert.Empty(poster.Host.Shards);
        Assert.Null(poster.Host.LocalServerCount);
    }

    [Fact]
    public async Task MissingTokenIsInertOnARealClient()
    {
        await using var client = new DiscordSocketClient();
        await using var poster = new TdlAutoposter(client, null);

        Assert.Equal("no-token", (await poster.PostStatsAsync()).Skipped);
    }

    [Fact]
    public async Task LogEventReceivesWarnings()
    {
        await using var client = new DiscordSocketClient();
        await using var poster = client.AddTheDiscordList("tdl_token", Offline());
        var messages = new List<LogMessage>();
        poster.Log += message =>
        {
            messages.Add(message);
            return Task.CompletedTask;
        };

        poster.Host.Log(TdlLogLevel.Warning, "careful");

        var message = Assert.Single(messages);
        Assert.Equal(LogSeverity.Warning, message.Severity);
        Assert.Equal("TheDiscordList", message.Source);
    }

    [Fact]
    public void CommandsMapToTheWireShape()
    {
        var choice = Fake.Of<IApplicationCommandOptionChoice>(("Name", "Level"), ("Value", 3L));
        var parameter = Fake.Of<IApplicationCommandOption>(
            ("Type", ApplicationCommandOptionType.Integer), ("Name", "level"), ("Description", "How loud"),
            ("IsRequired", (bool?)true), ("Choices", new[] { choice }), ("Options", Array.Empty<IApplicationCommandOption>()));
        var subcommand = Fake.Of<IApplicationCommandOption>(
            ("Type", ApplicationCommandOptionType.SubCommand), ("Name", "set"), ("Description", "Set it"),
            ("Choices", Array.Empty<IApplicationCommandOptionChoice>()), ("Options", new[] { parameter }));
        var slash = Fake.Of<IApplicationCommand>(
            ("Type", ApplicationCommandType.Slash), ("Name", "volume"), ("Description", "Volume"), ("IsNsfw", false),
            ("DefaultMemberPermissions", new GuildPermissions(32)), ("Options", new[] { subcommand }));
        var user = Fake.Of<IApplicationCommand>(
            ("Type", ApplicationCommandType.User), ("Name", "Inspect"), ("Description", string.Empty), ("IsNsfw", true),
            ("DefaultMemberPermissions", new GuildPermissions(0)), ("Options", Array.Empty<IApplicationCommandOption>()));

        var json = JsonSerializer.Serialize(new[] { DiscordNetHost.Map(slash), DiscordNetHost.Map(user) }, TdlApiClient.Json);

        Assert.Equal(
            """[{"type":1,"name":"volume","description":"Volume","nsfw":false,"default_member_permissions":"32","options":[{"type":1,"name":"set","description":"Set it","options":"""
            + """[{"type":4,"name":"level","description":"How loud","required":true,"choices":[{"name":"Level","value":3}]}]}]},{"type":2,"name":"Inspect","nsfw":true}]""",
            json);
    }
}

/// <summary>An interface fake whose getters answer from a dictionary.</summary>
public class Fake : DispatchProxy
{
    private readonly Dictionary<string, object?> _values = [];

    public static T Of<T>(params (string Name, object? Value)[] values)
        where T : class
    {
        var proxy = Create<T, Fake>();
        foreach (var (name, value) in values)
        {
            ((Fake)(object)proxy)._values[name] = value;
        }

        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        if (method.Name.StartsWith("get_", StringComparison.Ordinal) && _values.TryGetValue(method.Name[4..], out var value))
        {
            return value;
        }

        return method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
