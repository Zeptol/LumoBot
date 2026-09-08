using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumo.Bot.Telegram;

public sealed class TelegramApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;

    public TelegramApiClient(string token, HttpClient httpClient)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Telegram bot token is required.", nameof(token));
        }

        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri($"https://api.telegram.org/bot{token.Trim()}/", UriKind.Absolute);
        _httpClient.Timeout = TimeSpan.FromSeconds(40);
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        long offset,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetFromJsonAsync<TelegramApiResponse<List<TelegramUpdate>>>(
            $"getUpdates?offset={offset}&timeout=25",
            JsonOptions,
            cancellationToken);

        if (response is null)
        {
            throw new InvalidOperationException("Telegram returned an empty response.");
        }

        EnsureOk(response.Ok, response.Description);
        return response.Result ?? [];
    }

    public Task SendTextAsync(
        long chatId,
        string text,
        CancellationToken cancellationToken)
    {
        return PostFormAsync(
            "sendMessage",
            new Dictionary<string, string>
            {
                ["chat_id"] = chatId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["text"] = text
            },
            cancellationToken);
    }

    public Task SendPhotoAsync(
        long chatId,
        string photoUrl,
        string caption,
        CancellationToken cancellationToken)
    {
        return PostFormAsync(
            "sendPhoto",
            new Dictionary<string, string>
            {
                ["chat_id"] = chatId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["photo"] = photoUrl,
                ["caption"] = caption
            },
            cancellationToken);
    }

    public Task SendAudioAsync(
        long chatId,
        string audioUrl,
        string caption,
        CancellationToken cancellationToken)
    {
        return PostFormAsync(
            "sendAudio",
            new Dictionary<string, string>
            {
                ["chat_id"] = chatId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["audio"] = audioUrl,
                ["caption"] = caption
            },
            cancellationToken);
    }

    private async Task PostFormAsync(
        string method,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var httpResponse = await _httpClient.PostAsync(method, content, cancellationToken);
        var response = await httpResponse.Content.ReadFromJsonAsync<TelegramApiResponse<JsonElement>>(
            JsonOptions,
            cancellationToken);

        if (response is null)
        {
            httpResponse.EnsureSuccessStatusCode();
            throw new InvalidOperationException($"Telegram {method} returned an empty response.");
        }

        if (!httpResponse.IsSuccessStatusCode || !response.Ok)
        {
            throw new InvalidOperationException(
                $"Telegram {method} failed: {response.Description ?? httpResponse.ReasonPhrase}");
        }
    }

    private static void EnsureOk(bool ok, string? description)
    {
        if (!ok)
        {
            throw new InvalidOperationException($"Telegram API error: {description ?? "unknown error"}");
        }
    }
}

public sealed class TelegramApiResponse<T>
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("result")]
    public T? Result { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

public sealed class TelegramUpdate
{
    [JsonPropertyName("update_id")]
    public long UpdateId { get; init; }

    [JsonPropertyName("message")]
    public TelegramMessage? Message { get; init; }
}

public sealed class TelegramMessage
{
    [JsonPropertyName("message_id")]
    public long MessageId { get; init; }

    [JsonPropertyName("from")]
    public TelegramUser? From { get; init; }

    [JsonPropertyName("chat")]
    public required TelegramChat Chat { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

public sealed class TelegramChat
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
}

public sealed class TelegramUser
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("is_bot")]
    public bool IsBot { get; init; }

    [JsonPropertyName("first_name")]
    public string FirstName { get; init; } = string.Empty;

    [JsonPropertyName("last_name")]
    public string? LastName { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    public string DisplayName
    {
        get
        {
            var name = string.Join(' ', new[] { FirstName, LastName }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            return string.IsNullOrWhiteSpace(name)
                ? Username ?? Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : name;
        }
    }
}
