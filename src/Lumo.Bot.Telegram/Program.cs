using System.Globalization;
using Lumo.Application;
using Lumo.Bot.Telegram;
using Lumo.Games;
using Lumo.Infrastructure;

const string Platform = "telegram";

var token = Environment.GetEnvironmentVariable("LUMO_TELEGRAM_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Missing LUMO_TELEGRAM_TOKEN environment variable.");
    return 2;
}

var databasePath = Environment.GetEnvironmentVariable("LUMO_DATABASE_PATH");
if (string.IsNullOrWhiteSpace(databasePath))
{
    databasePath = Path.Combine(Directory.GetCurrentDirectory(), "data", "lumo.db");
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

ILumoStore store = new SqliteLumoStore(databasePath);
await store.InitializeAsync(cancellation.Token);
var gameService = new ChatGameService(new GameEngine(store), store);

using var httpClient = new HttpClient();
var telegram = new TelegramApiClient(token, httpClient);

Console.WriteLine($"Lumo Telegram bot started. Database: {Path.GetFullPath(databasePath)}");

long offset = 0;
while (!cancellation.IsCancellationRequested)
{
    try
    {
        var updates = await telegram.GetUpdatesAsync(offset, cancellation.Token);
        foreach (var update in updates)
        {
            offset = Math.Max(offset, update.UpdateId + 1);
            if (update.Message is { } message)
            {
                await HandleMessageAsync(telegram, gameService, message, cancellation.Token);
            }
        }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        break;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] {exception}");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            break;
        }
    }
}

Console.WriteLine("Lumo Telegram bot stopped.");
return 0;

static async Task HandleMessageAsync(
    TelegramApiClient telegram,
    ChatGameService gameService,
    TelegramMessage message,
    CancellationToken cancellationToken)
{
    if (message.From is null || message.From.IsBot || string.IsNullOrWhiteSpace(message.Text))
    {
        return;
    }

    var context = new ChatMessageContext(
        Platform,
        message.Chat.Id.ToString(CultureInfo.InvariantCulture),
        message.From.Id.ToString(CultureInfo.InvariantCulture),
        message.From.DisplayName,
        message.Text);

    var actions = await gameService.HandleAsync(context, cancellationToken);
    foreach (var action in actions)
    {
        switch (action.Kind)
        {
            case ChatActionKind.Image when !string.IsNullOrWhiteSpace(action.MediaUrl):
                await telegram.SendPhotoAsync(message.Chat.Id, action.MediaUrl, action.Text, cancellationToken);
                break;

            case ChatActionKind.Audio when !string.IsNullOrWhiteSpace(action.MediaUrl):
                await telegram.SendAudioAsync(message.Chat.Id, action.MediaUrl, action.Text, cancellationToken);
                break;

            default:
                await telegram.SendTextAsync(message.Chat.Id, action.Text, cancellationToken);
                break;
        }
    }
}
