# TheDiscordList.DiscordNet

Publish your Discord.Net bot's server count, cluster heartbeats and application
commands to [TheDiscordList](https://thediscordlist.com) automatically, and
verify its inbound webhooks.

No dependencies beyond Discord.Net: `HttpClient`, `System.Text.Json` and the
BCL's HMAC do the rest.

```sh
dotnet add package TheDiscordList.DiscordNet
```

## Quickstart

```csharp
using Discord;
using Discord.WebSocket;
using TheDiscordList.DiscordNet;

var client = new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds });

await using var poster = client.AddTheDiscordList(Environment.GetEnvironmentVariable("TDL_TOKEN"));

await client.LoginAsync(TokenType.Bot, Environment.GetEnvironmentVariable("DISCORD_TOKEN"));
await client.StartAsync();
await Task.Delay(Timeout.Infinite);
```

That is the whole integration, and it is also the recommended production setup.
`new TdlAutoposter(client, token, options)` is the same thing without the
extension method. Either starts three publishers:

| Publisher | What it sends | Cadence |
| --- | --- | --- |
| Stats | `server_count`, `shard_count` | 5s after ready, then every 5 min, debounced on guild join/leave |
| Heartbeat | per-shard status, latency and guild counts | every ~30s, plus within a second of any shard connecting or disconnecting |
| Commands | your global application commands | once, ~10s after ready |

Your **stats token** is on the listing's manage page, under the API tab. It is
scoped to one listing, so it is the only credential this package needs.

`AddTheDiscordList` is safe to call before or after `LoginAsync()`. With a
missing token it warns once and returns an inert poster — a forgotten
environment variable never takes down a deploy.

## Sharding

Detection reads the client, so the common layouts need no configuration:

| Topology | `server_count` comes from | Stats posted by | Heartbeats |
| --- | --- | --- | --- |
| `DiscordSocketClient` | `client.Guilds.Count` | this process | this process |
| `DiscordShardedClient` running every shard | the sum over `client.Shards` | this process | this process |
| one `DiscordSocketClient` per process (`ShardId` / `TotalShards`) | your `ServerCount` | shard 0's process | every process, for itself, as cluster `ShardId` of `TotalShards` |
| `DiscordShardedClient` running some shards | your `ServerCount` | the process holding shard 0 | only with `Cluster` set |

Every process heartbeats for **itself** on purpose. A manager reporting "all
fine" for a cluster whose host has died is exactly the failure the status page
exists to catch.

Discord.Net has no cross-process broadcast, so a bot split across processes
must supply the bot-wide total itself, and a sharded client that runs only some
of the shards must say which cluster it is:

```csharp
var poster = client.AddTheDiscordList(token, new TdlOptions
{
    Cluster = new TdlCluster(Id: 0, Count: 4),
    ServerCount = () => myBus.SumGuildCountsAsync(),
});
```

> **If the cluster count cannot be determined, heartbeats do not start.** The
> API hard-deletes every cluster row whose id is `>= cluster_count` in the
> request, so one process guessing `1` would erase all of its siblings and
> their monitoring history. The heartbeat publisher raises `Disabled` with
> reason `cluster-count-unknown` and logs `TdlOptions.Cluster` as the fix.
> Losing a status page beats losing data.

Discord.Net keeps the total shard count internal. The package reads it by
reflection and treats an unreadable value as unknown, so a future Discord.Net
that renames it degrades to "heartbeats off, set `Cluster`" rather than a guess.

## Options

```csharp
var poster = client.AddTheDiscordList(token, new TdlOptions
{
    BotId = 161660517914509312, // default: the client's application id
    BaseUrl = "https://thediscordlist.com/api",

    Stats = { Enabled = true, Interval = TimeSpan.FromMinutes(5), Debounce = TimeSpan.FromSeconds(20), MinGap = TimeSpan.FromSeconds(60) },
    Heartbeat = { Enabled = true, Interval = TimeSpan.FromSeconds(30) },
    Commands = { Enabled = true, PostEmpty = false },

    Cluster = new TdlCluster(0, 1),
    ServerCount = () => Task.FromResult(1234),
    HttpClient = myHttpClient, // proxies; never disposed by the poster
});
```

`MinGap` is a hard floor between two stats requests, and it cannot go below 60
seconds. A join storm, a mass reconnect and a manual `PostStatsAsync()` all route
through it, so the ceiling is structurally one request per minute. An unchanged
count is not re-sent until 30 minutes have passed.

`Commands.PostEmpty` is `false` because posting an empty array **clears** the
command list on your listing. Turn it on only if that is what you mean.

## Events and manual posting

```csharp
poster.Posted += posted => { };     // posted.Publisher, posted.Status
poster.Error += error => { };       // error.Publisher, error.Status, error.Retryable, error.Message
poster.Disabled += disabled => { }; // disabled.Publisher, disabled.Status, disabled.Reason
poster.Log += LogAsync;             // same shape as DiscordSocketClient.Log, so reuse your handler

TdlPostResult result = await poster.PostStatsAsync(); // never throws: { Ok, Status, Skipped }
await poster.PostCommandsAsync();                      // after re-registering commands at runtime
await poster.PostHeartbeatAsync();

await poster.DisposeAsync(); // stops every timer and detaches from the client
```

Nothing in this package throws at your process. Timer callbacks are wrapped,
the `PostXAsync()` methods always return a result, and a throwing event handler
is logged and contained. Without a `Log` handler, warnings go to standard error;
without an `Error` handler, errors are logged instead.

`result.Skipped` names why nothing was sent (`min-gap`, `unchanged`, `suspended`,
`plan`, `not-ready`, …), which is usually correct behaviour rather than a
failure. An explicit `PostXAsync()` also re-arms a publisher that disabled
itself, so you can retry after fixing a token or a bot id without restarting.

## Webhooks

`TdlWebhook.Verify` needs no Discord client, so it also works in a web app that
never connects to Discord.

### ASP.NET Core minimal API

The package carries no ASP.NET Core dependency, so the endpoint is yours to
write — and it is ten lines:

```csharp
app.MapPost("/webhooks/tdl", async (HttpRequest request) =>
{
    using var body = new MemoryStream();
    await request.Body.CopyToAsync(body);
    try
    {
        var webhook = TdlWebhook.Verify(body.ToArray(), request.Headers[TdlWebhook.SignatureHeader], secret);
        await HandleAsync(webhook, request.Headers[TdlWebhook.DeliveryHeader]!); // switch on webhook.Event
        return Results.NoContent();
    }
    catch (TdlWebhookException) { return Results.BadRequest(); }
});
```

`examples/BasicBot` runs exactly this next to a bot, and maps the route only
when `TDL_WEBHOOK_SECRET` is set.

### Verifying by hand

```csharp
try
{
    TdlWebhookEvent webhook = TdlWebhook.Verify(rawBody, signatureHeader, secret);
}
catch (TdlWebhookException exception)
{
    // exception.Code: MissingSignature, MalformedSignature, StaleTimestamp, InvalidSignature, MalformedBody
}
```

`rawBody` must be the exact bytes that arrived, as a `byte[]`/span or a string.
Re-serialising a parsed body can never verify: the sender signs the string it
sends, and a serializer escapes slashes and non-ASCII differently. Read the body
yourself rather than binding it to a model.

`Verify` checks a two-sided 300-second window around the signature's `t`
(tune with `tolerance:`), compares in constant time, and accepts upper-case hex.
An empty or blank `secret` throws `ArgumentException` rather than checking
against an empty key, so a missing environment variable fails closed.

### Events

| Event | Fires for | `DataAs<T>()` |
| --- | --- | --- |
| `listing.bumped` | servers, bots | `TdlListingBumped` |
| `listing.bump_ready` | servers, bots | `TdlListingBumpReady` |
| `listing.upvoted` | servers, bots | `TdlListingUpvoted` |
| `server.joined` | servers only | `TdlServerJoined` |
| `bot.added` | bots | `TdlBotAdded` |
| `bot.stats_reported` | bots | `TdlBotStatsReported` |
| `webhook.ping` | both | `TdlWebhookPing` |

The names are constants on `TdlWebhookEvents`. Every envelope carries `Id`,
`Event`, `CreatedAt`, `Listing` and a raw `Data` element, so an event name this
version does not know still parses.

Two of them are not what their names suggest. `bot.added` is a **click** event,
dispatched on the install hand-off — Discord never tells anyone whether the
install completed. And `bot.stats_reported` is not an echo of your own POST: it
also fires when the owner edits the listing in the web UI, and when the server
forwards your heartbeats' guild counts into stats. `CreatedAt` is
enqueue time, not send time; never use it for freshness.

### Rules for a healthy endpoint

- **Always answer 2xx**, including for `webhook.ping` and for event names you
  do not recognise. The sender disables an endpoint after 10 consecutive failed
  deliveries — about 50 requests over 11.5 hours.
- A disabled endpoint is probed with a `webhook.ping` every 6 hours for 7 days,
  and **a 2xx to that probe re-enables it**.
- **Register an exact `https://` URL with no redirect.** The sender treats 3xx
  as a failure.
- **Deduplicate on `X-TDL-Delivery`** (`TdlWebhook.DeliveryHeader`, equal to
  `webhook.Id`). It is stable across all five retries of one delivery. A manual
  "redeliver" from the UI mints a new id on purpose.
- **Rotating the secret has no grace window.** Deliveries already queued under
  the old secret become unverifiable.
- Answer within 8 seconds; queue slow work and return `204` first.

## Plans, limits and known behaviour

- **Heartbeats need Gold or higher.** On a `403` the heartbeat publisher
  suspends and re-probes every 30 minutes rather than disabling: the plan is
  checked per request, so a bot can gain it mid-run.
- **Rate limits are shared.** Stats and commands share 60 requests a minute per
  client IP, heartbeats get 120. A `429` suspends stats and commands together,
  honouring `Retry-After` and `X-RateLimit-Reset`; heartbeats bypass the
  suspension, because a stale number beats a false "down" on your status page.
  On shared hosting, other bots behind the same egress IP count against you.
- **`404` never means "bad token".** It means the id is unknown or belongs to a
  server listing; that publisher disables itself. A `401` is the bad-token
  signal, and it disables every publisher.
- `5xx`, timeouts (10 seconds) and network errors retry up to three times with
  jittered exponential backoff, then wait for the next tick.
- Commands are read with `client.Rest.GetGlobalApplicationCommands()`.
  Discord.Net reads an absent `default_member_permissions` as `0`, so `0` is
  sent as absent: a command restricted to administrators by `0` shows as
  available to everyone on the listing.
- Without `GatewayIntents.Guilds`, the guild cache stops moving after READY and
  the count silently drifts.

## Requirements

- .NET 8 or later (the package targets `net8.0`; CI also runs it on .NET 10)
- Discord.Net `>= 3.15.0, < 4.0.0` (`Discord.Net.WebSocket`; the `Discord.Net`
  meta-package includes it)

## Publishing (maintainers)

Releases publish from `.github/workflows/release.yml` on a `v*` tag, using
NuGet trusted publishing (OIDC through `NuGet/login`): no long-lived API key
lives in the repository. The package version comes from the tag.

One-time setup:

1. Sign in to nuget.org with the account that will own `TheDiscordList.DiscordNet`.
2. Under **Trusted Publishing**, add a GitHub Actions policy: owner
   `TheDiscordList`, repository `integration-discordnet`, workflow file
   `release.yml` (no environment).
3. In the repository, add the secret `NUGET_USER` holding that nuget.org
   **profile name** (not the email address).

Each release:

1. Move the `Unreleased` notes in `CHANGELOG.md` under the new version.
2. `git tag v0.1.0 && git push origin v0.1.0`.
3. The workflow tests, packs with `-p:Version=0.1.0`, exchanges its OIDC token
   for a short-lived key, and pushes the `.nupkg` and `.snupkg`.

## License

MIT
