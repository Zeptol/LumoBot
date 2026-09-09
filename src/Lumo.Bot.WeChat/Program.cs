using System.Security.Cryptography;
using System.Text;
using Lumo.Application;
using Lumo.Games;
using Lumo.Infrastructure;

const string Platform = "wechat";

var builder = WebApplication.CreateBuilder(args);

var bindUrl = Environment.GetEnvironmentVariable("LUMO_WECHAT_BIND_URL");
if (string.IsNullOrWhiteSpace(bindUrl))
{
    bindUrl = "http://127.0.0.1:5080";
}

builder.WebHost.UseUrls(bindUrl);

var databasePath = Environment.GetEnvironmentVariable("LUMO_DATABASE_PATH");
if (string.IsNullOrWhiteSpace(databasePath))
{
    databasePath = Path.Combine(Directory.GetCurrentDirectory(), "data", "lumo.db");
}

var gatewayToken = Environment.GetEnvironmentVariable("LUMO_WECHAT_GATEWAY_TOKEN");

ILumoStore store = new SqliteLumoStore(databasePath);
await store.InitializeAsync();
var selectionOptions = QuestionSelectionOptions.FromEnvironment();
var questionSource = new SqliteQuestionCandidateSource(databasePath);
var gameService = new ChatGameService(
    new GameEngine(store, questionSource, selectionOptions),
    store);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    service = "Lumo.Bot.WeChat",
    status = "ok"
}));

app.MapPost("/api/wechat/messages", async (
    HttpRequest request,
    WeChatInboundMessage message,
    CancellationToken cancellationToken) =>
{
    if (!IsAuthorized(request, gatewayToken))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(message.ConversationId) ||
        string.IsNullOrWhiteSpace(message.SenderId) ||
        string.IsNullOrWhiteSpace(message.SenderName) ||
        string.IsNullOrWhiteSpace(message.Text))
    {
        return Results.BadRequest(new
        {
            error = "conversationId, senderId, senderName and text are required"
        });
    }

    var actions = await gameService.HandleAsync(
        new ChatMessageContext(
            Platform,
            message.ConversationId,
            message.SenderId,
            message.SenderName,
            message.Text),
        cancellationToken);

    return Results.Ok(new WeChatGatewayResponse(
        actions.Select(action => new WeChatOutboundAction(
            action.Kind switch
            {
                ChatActionKind.Image => "image",
                ChatActionKind.Audio => "audio",
                _ => "text"
            },
            action.Text,
            action.MediaUrl)).ToArray()));
});

Console.WriteLine($"Lumo WeChat backend listening on {bindUrl}");
Console.WriteLine($"Database: {Path.GetFullPath(databasePath)}");
Console.WriteLine($"Question selection: candidates={selectionOptions.CandidatePoolSize}, recentQuestions={selectionOptions.RecentQuestionLimit}, recentEntities={selectionOptions.RecentEntityLimit}");
await app.RunAsync();

static bool IsAuthorized(HttpRequest request, string? expectedToken)
{
    if (string.IsNullOrEmpty(expectedToken))
    {
        return true;
    }

    if (!request.Headers.TryGetValue("X-Lumo-Gateway-Token", out var providedValues))
    {
        return false;
    }

    var providedToken = providedValues.ToString();
    var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
    var providedBytes = Encoding.UTF8.GetBytes(providedToken);

    return expectedBytes.Length == providedBytes.Length &&
           CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
}

public sealed record WeChatInboundMessage(
    string ConversationId,
    string SenderId,
    string SenderName,
    string Text,
    string? MessageId = null,
    bool IsGroup = true);

public sealed record WeChatOutboundAction(
    string Kind,
    string Text,
    string? MediaUrl = null);

public sealed record WeChatGatewayResponse(
    IReadOnlyList<WeChatOutboundAction> Actions);
