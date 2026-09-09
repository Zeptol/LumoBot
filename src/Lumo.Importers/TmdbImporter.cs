using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Lumo.Domain;
using Lumo.Infrastructure;

internal static class TmdbImporter
{
    private const string ApiBase = "https://api.themoviedb.org/3/";
    private const string ImageBase = "https://image.tmdb.org/t/p/w1280";
    private const string TmdbUsageNotice = "TMDB API/data/images; use subject to TMDB terms and attribution requirements";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static async Task<int> RunAsync(string databasePath, CliOptions options)
    {
        var kind = (options.Get("type") ?? options.Get("preset") ?? "movie").Trim().ToLowerInvariant();
        if (kind is not ("movie" or "tv" or "variety"))
        {
            throw new ArgumentException("TMDB --type must be movie, tv or variety.");
        }

        var token = options.Get("token") ?? Environment.GetEnvironmentVariable("TMDB_BEARER_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Missing TMDB token. Set TMDB_BEARER_TOKEN or pass --token.");
        }

        var language = options.Get("language") ?? "zh-CN";
        var region = options.Get("region");
        var pages = Math.Clamp(options.GetInt("pages", 3), 1, 50);
        var pageStart = Math.Clamp(options.GetInt("page-start", 1), 1, 500);
        var imagesPerTitle = Math.Clamp(options.GetInt("images-per-title", 2), 1, 8);
        var maxTitles = Math.Clamp(options.GetInt("max-titles", pages * 20), 1, 1000);
        var pool = (options.Get("pool") ?? "all").Trim().ToLowerInvariant();
        var alsoMixed = options.GetBool("also-mixed", true);
        var originalLanguage = options.Get("original-language");
        var yearFrom = options.GetInt("year-from", 0);
        var yearTo = options.GetInt("year-to", 0);
        var minVotes = options.GetInt("min-votes", DefaultMinVotes(pool));
        var maxVotes = options.GetInt("max-votes", 0);
        var difficultyOption = options.Get("difficulty") ?? "auto";

        using var http = new HttpClient
        {
            BaseAddress = new Uri(ApiBase, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(45)
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LumoBot/0.1 (+https://github.com/Zeptol/LumoBot)");

        var store = new SqliteLumoStore(databasePath);
        await store.InitializeAsync();

        var varietyGenres = kind == "variety"
            ? await ResolveVarietyGenresAsync(http)
            : null;

        var importedTitles = 0;
        var importedQuestions = 0;
        var seenIds = new HashSet<long>();

        for (var page = pageStart; page < pageStart + pages && importedTitles < maxTitles; page++)
        {
            var endpoint = kind == "movie" ? "discover/movie" : "discover/tv";
            var query = BuildDiscoverQuery(
                kind,
                page,
                language,
                region,
                pool,
                originalLanguage,
                yearFrom,
                yearTo,
                minVotes,
                maxVotes,
                varietyGenres,
                options.Get("sort"));

            using var document = await GetJsonAsync(http, $"{endpoint}?{query}");
            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in results.EnumerateArray())
            {
                if (importedTitles >= maxTitles)
                {
                    break;
                }

                if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var tmdbId) || !seenIds.Add(tmdbId))
                {
                    continue;
                }

                var title = GetString(item, kind == "movie" ? "title" : "name");
                var originalTitle = GetString(item, kind == "movie" ? "original_title" : "original_name");
                var date = GetString(item, kind == "movie" ? "release_date" : "first_air_date");
                var year = ParseYear(date);
                var originalLanguageValue = GetString(item, "original_language");
                var popularity = GetDouble(item, "popularity");
                var voteCount = GetInt(item, "vote_count");
                var overview = GetString(item, "overview");
                var discoverBackdrop = GetString(item, "backdrop_path");

                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var imagePaths = await GetBackdropsAsync(http, kind == "movie" ? "movie" : "tv", tmdbId, imagesPerTitle);
                if (imagePaths.Count == 0 && !string.IsNullOrWhiteSpace(discoverBackdrop))
                {
                    imagePaths.Add(discoverBackdrop!);
                }

                if (imagePaths.Count == 0)
                {
                    continue;
                }

                var aliases = new[] { title, originalTitle }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var entityType = kind switch
                {
                    "movie" => "Movie",
                    "tv" => "TV",
                    _ => "VarietyShow"
                };
                var categoryTag = kind switch
                {
                    "movie" => "电影",
                    "tv" => "电视剧",
                    _ => "综艺"
                };
                var difficulty = ResolveDifficulty(difficultyOption, popularity, voteCount);
                var sourceUrl = kind == "movie"
                    ? $"https://www.themoviedb.org/movie/{tmdbId}"
                    : $"https://www.themoviedb.org/tv/{tmdbId}";
                var metadata = JsonSerializer.Serialize(new
                {
                    provider = "TMDB",
                    tmdbId,
                    kind,
                    title,
                    originalTitle,
                    overview,
                    date,
                    year,
                    originalLanguage = originalLanguageValue,
                    popularity,
                    voteCount,
                    pool
                }, JsonOptions);

                var commonTags = new[]
                {
                    categoryTag,
                    "影视",
                    "TMDB",
                    originalLanguageValue,
                    year > 0 ? $"{year / 10 * 10}s" : null,
                    PoolTag(pool)
                }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

                foreach (var imagePath in imagePaths)
                {
                    var imageUrl = $"{ImageBase}{imagePath}";
                    var imageKey = StablePathKey(imagePath);

                    if (kind == "movie")
                    {
                        await store.UpsertQuestionAsync(new CatalogQuestionInput(
                            $"tmdb:movie:{tmdbId}:backdrop:{imageKey}:movie",
                            GameMode.GuessMovie,
                            entityType,
                            title.Trim(),
                            "🎬 猜电影：这是哪部电影？",
                            title.Trim(),
                            difficulty,
                            MediaKind.Image,
                            imageUrl,
                            sourceUrl,
                            TmdbUsageNotice,
                            aliases,
                            commonTags,
                            metadata));
                        importedQuestions++;

                        if (alsoMixed)
                        {
                            await store.UpsertQuestionAsync(new CatalogQuestionInput(
                                $"tmdb:movie:{tmdbId}:backdrop:{imageKey}:mixed",
                                GameMode.GuessImage,
                                entityType,
                                title.Trim(),
                                "🖼️ 猜图：这张画面来自哪部电影？",
                                title.Trim(),
                                difficulty,
                                MediaKind.Image,
                                imageUrl,
                                sourceUrl,
                                TmdbUsageNotice,
                                aliases,
                                commonTags,
                                metadata));
                            importedQuestions++;
                        }
                    }
                    else
                    {
                        var prompt = kind == "variety"
                            ? "🖼️ 猜图：这是哪档综艺节目？"
                            : "🖼️ 猜图：这张画面来自哪部电视剧？";

                        await store.UpsertQuestionAsync(new CatalogQuestionInput(
                            $"tmdb:{kind}:{tmdbId}:backdrop:{imageKey}:mixed",
                            GameMode.GuessImage,
                            entityType,
                            title.Trim(),
                            prompt,
                            title.Trim(),
                            difficulty,
                            MediaKind.Image,
                            imageUrl,
                            sourceUrl,
                            TmdbUsageNotice,
                            aliases,
                            commonTags,
                            metadata));
                        importedQuestions++;
                    }
                }

                importedTitles++;
                Console.WriteLine($"[{importedTitles}] {categoryTag}: {title} ({year}) -> {imagePaths.Count} images");
            }
        }

        Console.WriteLine($"TMDB import complete: {importedTitles} titles, {importedQuestions} questions -> {Path.GetFullPath(databasePath)}");
        return 0;
    }

    public static int RunSelfTest()
    {
        if (ResolveDifficulty("auto", 200, 5000) != 1 ||
            ResolveDifficulty("auto", 15, 50) < 3 ||
            PoolTag("obscure") != "冷门" ||
            StablePathKey("/abc/def.jpg").Length == 0)
        {
            throw new InvalidOperationException("TMDB importer self-test failed.");
        }

        Console.WriteLine("TMDB importer self-test passed.");
        return 0;
    }

    private static async Task<string?> ResolveVarietyGenresAsync(HttpClient http)
    {
        using var document = await GetJsonAsync(http, "genre/tv/list?language=en-US");
        if (!document.RootElement.TryGetProperty("genres", out var genres) || genres.ValueKind != JsonValueKind.Array)
        {
            return "10764|10767";
        }

        var ids = genres.EnumerateArray()
            .Where(genre =>
            {
                var name = GetString(genre, "name");
                return string.Equals(name, "Reality", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "Talk", StringComparison.OrdinalIgnoreCase);
            })
            .Select(genre => GetInt(genre, "id"))
            .Where(id => id > 0)
            .Distinct()
            .ToArray();

        return ids.Length > 0 ? string.Join('|', ids) : "10764|10767";
    }

    private static string BuildDiscoverQuery(
        string kind,
        int page,
        string language,
        string? region,
        string pool,
        string? originalLanguage,
        int yearFrom,
        int yearTo,
        int minVotes,
        int maxVotes,
        string? varietyGenres,
        string? sortOverride)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("language", language),
            new("page", page.ToString(CultureInfo.InvariantCulture)),
            new("include_adult", "false"),
            new("sort_by", sortOverride ?? DefaultSort(pool)),
            new("vote_count.gte", Math.Max(0, minVotes).ToString(CultureInfo.InvariantCulture))
        };

        if (maxVotes > 0)
        {
            parameters.Add(new("vote_count.lte", maxVotes.ToString(CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(region) && kind == "movie")
        {
            parameters.Add(new("region", region));
        }

        if (!string.IsNullOrWhiteSpace(originalLanguage))
        {
            parameters.Add(new("with_original_language", originalLanguage.Trim()));
        }

        if (yearFrom > 0)
        {
            parameters.Add(new(kind == "movie" ? "primary_release_date.gte" : "first_air_date.gte", $"{yearFrom:0000}-01-01"));
        }

        if (yearTo > 0)
        {
            parameters.Add(new(kind == "movie" ? "primary_release_date.lte" : "first_air_date.lte", $"{yearTo:0000}-12-31"));
        }

        if (kind == "variety" && !string.IsNullOrWhiteSpace(varietyGenres))
        {
            parameters.Add(new("with_genres", varietyGenres));
        }

        return string.Join('&', parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static async Task<List<string>> GetBackdropsAsync(HttpClient http, string mediaType, long id, int limit)
    {
        using var document = await GetJsonAsync(http, $"{mediaType}/{id}/images?include_image_language=zh,null,en");
        if (!document.RootElement.TryGetProperty("backdrops", out var backdrops) || backdrops.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return backdrops.EnumerateArray()
            .Select(image => new
            {
                Path = GetString(image, "file_path"),
                VoteCount = GetInt(image, "vote_count"),
                Width = GetInt(image, "width")
            })
            .Where(image => !string.IsNullOrWhiteSpace(image.Path) && image.Width >= 600)
            .OrderByDescending(image => image.VoteCount)
            .Select(image => image.Path!)
            .Distinct(StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient http, string relativeUrl)
    {
        using var response = await http.GetAsync(relativeUrl);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"TMDB returned {(int)response.StatusCode}: {body}");
        }

        return JsonDocument.Parse(body);
    }

    private static int ResolveDifficulty(string setting, double popularity, int voteCount)
    {
        if (int.TryParse(setting, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fixedDifficulty))
        {
            return Math.Clamp(fixedDifficulty, 1, 10);
        }

        if (popularity >= 100 || voteCount >= 3000) return 1;
        if (popularity >= 40 || voteCount >= 800) return 2;
        if (popularity >= 15 || voteCount >= 150) return 3;
        if (popularity >= 5 || voteCount >= 30) return 4;
        return 5;
    }

    private static int DefaultMinVotes(string pool) => pool switch
    {
        "popular" => 300,
        "normal" => 80,
        "obscure" => 10,
        _ => 20
    };

    private static string DefaultSort(string pool) => pool switch
    {
        "obscure" => "popularity.asc",
        "normal" => "vote_count.desc",
        _ => "popularity.desc"
    };

    private static string? PoolTag(string pool) => pool switch
    {
        "popular" => "热门",
        "normal" => "普通",
        "obscure" => "冷门",
        _ => null
    };

    private static int ParseYear(string? date)
        => !string.IsNullOrWhiteSpace(date) && date!.Length >= 4 && int.TryParse(date[..4], out var year) ? year : 0;

    private static string StablePathKey(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))
            .ToLowerInvariant()[..16];

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;

    private static int GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static double GetDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : 0;
}