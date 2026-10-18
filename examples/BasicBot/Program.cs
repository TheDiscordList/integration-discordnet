// The whole integration for most bots, plus an ASP.NET Core webhook endpoint.
//
//   dotnet run --project examples/BasicBot
//
// Environment: DISCORD_TOKEN, TDL_TOKEN, and TDL_WEBHOOK_SECRET; the webhook route is only mapped when it is set.
using Discord;
using Discord.WebSocket;
using TheDiscordList.DiscordNet;

var client = new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds });
client.Log += Log;

await using var poster = client.AddTheDiscordList(Environment.GetEnvironmentVariable("TDL_TOKEN"));

// Entirely optional. Without these the package still never throws at your bot.
poster.Log += Log;
poster.Posted += posted => Console.WriteLine($"[tdl] {posted.Publisher} -> {posted.Status}");
poster.Disabled += disabled => Console.WriteLine($"[tdl] {disabled.Publisher} disabled: {disabled.Reason}");

await client.LoginAsync(TokenType.Bot, Environment.GetEnvironmentVariable("DISCORD_TOKEN"));
await client.StartAsync();

var secret = Environment.GetEnvironmentVariable("TDL_WEBHOOK_SECRET");
var app = WebApplication.Create(args);

if (string.IsNullOrWhiteSpace(secret))
{
    Console.WriteLine("[tdl] TDL_WEBHOOK_SECRET is not set; the webhook route is off.");
}
else
{
    app.MapPost("/webhooks/tdl", async (HttpRequest request) =>
    {
        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body);
        try
        {
            var webhook = TdlWebhook.Verify(body.ToArray(), request.Headers[TdlWebhook.SignatureHeader], secret);
            if (webhook.Event == TdlWebhookEvents.ListingUpvoted)
            {
                Console.WriteLine($"{webhook.DataAs<TdlListingUpvoted>()?.User?.Username ?? "Someone"} upvoted the bot.");
            }

            return Results.NoContent();
        }
        catch (TdlWebhookException exception)
        {
            Console.WriteLine($"[tdl] rejected a webhook: {exception.Code}");
            return Results.BadRequest();
        }
    });
}

await app.RunAsync();

static Task Log(LogMessage message)
{
    Console.WriteLine(message);
    return Task.CompletedTask;
}
