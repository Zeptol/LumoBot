using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Lumo.Domain;
using Lumo.Infrastructure;

var exitCode = await ImporterApp.RunAsync(args);
return exitCode;

internal static class ImporterApp
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var options = CliOptions.Parse(args.Skip(1).ToArray());
        var databasePath = options.Get("database")
            ?? Environment.GetEnvironmentVariable("LUMO_DATABASE_PATH")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "data", "lumo.db");

        try
        {
            return command switch
            {
                "manifest" => await ImportManifestAsync(databasePath, options),
                "idioms" => await ImportIdiomsAsync(databasePath, options),
                "music" => await ImportMusicAsync(databasePath, options),
                "wikidata" => await ImportWikidataAsync(databasePath, options),
                "self-test" => await RunSelfTestAsync(),
                _ => Fail($"Unknown command: {command}")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> ImportManifestAsync(string databasePath, CliOptions options)
    {
        var inputPath = RequireFile(options, "input");
        var questions = await ReadManifestAsync(inputPath);
        var store = await CreateStoreAsync(databasePath);

        var imported = 0;
        foreach (var question in questions)
        {
            await store.UpsertQuestionAsync(question);
            imported++;
        }

        Console.WriteLine($"Manifest import complete: {imported} questions -> {Path.GetFullPath(databasePath)}");
        return 0;
    }

    private static async Task<int> ImportIdiomsAsync(string databasePath, CliOptions options)
    {
        var inputPath = RequireFile(options, "input");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(inputPath));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Idiom JSON must contain a top-level array.");
        }

        var store = await CreateStoreAsync(databasePath);
        var imported = 0;
        var skipped = 0;

        foreach (var element in root.EnumerateArray())
        {
            var word = GetString(element, "word", "idiom", "name");
            var explanation = GetString(element, "explanation", "definition", "desc");
            var derivation = GetString(element, "derivation", "source", "origin");
            var pinyin = GetString(element, "pinyin");

            if (string.IsNullOrWhiteSpace(word) ||
                (string.IsNullOrWhiteSpace(explanation) && string.IsNullOrWhiteSpace(derivation)))
            {
                skipped++;
                continue;
            }

            var clue = !string.IsNullOrWhiteSpace(explanation) ? explanation : derivation!;
            var metadata = JsonSerializer.Serialize(new
            {
                pinyin,
                derivation,
                example = GetString(element, "example")
            }, JsonOptions);

            await store.UpsertQuestionAsync(new CatalogQuestionInput(
                $"idiom:{word.Trim()}",
                GameMode.GuessIdiom,
                "Idiom",
                word.Trim(),
                $"🀄 猜成语：{clue.Trim()}",
                word.Trim(),
                1,
                MediaKind.None,
                null,
                inputPath,
                options.Get("license") ?? "Imported dataset; verify the dataset license before public deployment",
                [word.Trim()],
                BuildTags("成语", "文学", pinyin),
                metadata));
            imported++;
        }

        Console.WriteLine($"Idiom import complete: {imported} imported, {skipped} skipped.");
        return 0;
    }

    private static async Task<int> ImportMusicAsync(string databasePath, CliOptions options)
    {
        var root = options.Require("root");
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Music root not found: {root}");
        }

        var clipRoot = Path.GetFullPath(options.Get("clips")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "data", "media", "music"));
        Directory.CreateDirectory(clipRoot);

        var ffprobe = options.Get("ffprobe") ?? "ffprobe";
        var ffmpeg = options.Get("ffmpeg") ?? "ffmpeg";
        var clipCount = Math.Clamp(options.GetInt("clip-count", 3), 1, 5);
        var clipDuration = Math.Clamp(options.GetDouble("duration", 8), 4, 20);
        var difficulty = Math.Clamp(options.GetInt("difficulty", 2), 1, 10);
        var extraTags = SplitCsv(options.Get("tags"));
        var store = await CreateStoreAsync(databasePath);

        var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".m4a", ".mp4", ".aac", ".ogg", ".opus", ".wav", ".wma"
        };

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => supportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Console.WriteLine($"Found {files.Length} audio files. Generating up to {clipCount} clips per song...");
        var songs = 0;
        var questions = 0;
        var skipped = 0;

        foreach (var file in files)
        {
            try
            {
                var probe = await ProbeAudioAsync(ffprobe, file);
                if (probe.DurationSeconds < clipDuration + 4)
                {
                    skipped++;
                    continue;
                }

                var title = FirstNonEmpty(
                    probe.GetTag("title"),
                    Path.GetFileNameWithoutExtension(file));
                var artist = FirstNonEmpty(
                    probe.GetTag("artist"),
                    probe.GetTag("album_artist"),
                    probe.GetTag("albumartist"),
                    "未知歌手");
                var album = probe.GetTag("album");
                var year = FirstNonEmptyOrNull(probe.GetTag("date"), probe.GetTag("year"));
                var genre = probe.GetTag("genre");
                var entityName = $"{title} — {artist}";
                var fingerprint = HashKey($"{artist}\n{title}\n{album}\n{probe.DurationSeconds:F3}");
                var starts = BuildClipStarts(probe.DurationSeconds, clipDuration, clipCount);
                var metadata = JsonSerializer.Serialize(new
                {
                    title,
                    artist,
                    album,
                    year,
                    genre,
                    durationSeconds = probe.DurationSeconds,
                    originalFile = Path.GetFullPath(file)
                }, JsonOptions);

                for (var index = 0; index < starts.Count; index++)
                {
                    var clipPath = Path.Combine(clipRoot, $"{fingerprint}-{index + 1:00}.mp3");
                    await GenerateClipAsync(ffmpeg, file, clipPath, starts[index], clipDuration);

                    var yearTag = !string.IsNullOrWhiteSpace(year) && year.Length >= 4 ? year[..4] : year;
                    var tags = BuildTags(
                        "音乐",
                        "歌曲",
                        artist,
                        album,
                        genre,
                        yearTag,
                        "本地音乐库")
                        .Concat(extraTags)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    await store.UpsertQuestionAsync(new CatalogQuestionInput(
                        $"music:{fingerprint}:clip:{index + 1}",
                        GameMode.GuessSong,
                        "Song",
                        entityName,
                        "🎵 猜歌曲：听这段音频，猜歌名。",
                        title,
                        difficulty,
                        MediaKind.Audio,
                        Path.GetFullPath(clipPath),
                        Path.GetFullPath(file),
                        options.Get("license") ?? "Local music library; operator must verify redistribution rights",
                        [$"{title} - {artist}", $"{title}—{artist}"],
                        tags,
                        metadata));
                    questions++;
                }

                songs++;
                Console.WriteLine($"[{songs}] {artist} - {title} ({starts.Count} clips)");
            }
            catch (Exception exception)
            {
                skipped++;
                Console.Error.WriteLine($"Skip {file}: {exception.Message}");
            }
        }

        Console.WriteLine($"Music import complete: {songs} songs, {questions} questions, {skipped} skipped.");
        Console.WriteLine($"Clips: {clipRoot}");
        return 0;
    }

    private static async Task<int> ImportWikidataAsync(string databasePath, CliOptions options)
    {
        var preset = options.Require("preset").ToLowerInvariant();
        var limit = Math.Clamp(options.GetInt("limit", 100), 1, 1000);
        var difficulty = Math.Clamp(options.GetInt("difficulty", 2), 1, 10);
        var query = BuildWikidataQuery(preset, limit);
        var store = await CreateStoreAsync(databasePath);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LumoBot/0.1 (+https://github.com/Zeptol/LumoBot)");
        var endpoint = "https://query.wikidata.org/sparql?format=json&query=" + Uri.EscapeDataString(query);
        using var response = await http.GetAsync(endpoint);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var bindings = document.RootElement
            .GetProperty("results")
            .GetProperty("bindings")
            .EnumerateArray()
            .ToArray();

        var imported = 0;
        var skipped = 0;
        foreach (var binding in bindings)
        {
            var itemUri = GetBinding(binding, "item");
            var label = GetBinding(binding, "itemLabel");
            var image = GetBinding(binding, "image");
            if (string.IsNullOrWhiteSpace(itemUri) ||
                string.IsNullOrWhiteSpace(label) ||
                string.IsNullOrWhiteSpace(image))
            {
                skipped++;
                continue;
            }

            try
            {
                var commons = await ResolveCommonsAsync(http, image);
                var qid = itemUri[(itemUri.LastIndexOf('/') + 1)..];
                var descriptor = GetWikidataPreset(preset);
                var metadata = JsonSerializer.Serialize(new
                {
                    wikidataId = qid,
                    wikidataUrl = itemUri,
                    commonsArtist = commons.Artist,
                    commonsCredit = commons.Credit
                }, JsonOptions);

                await store.UpsertQuestionAsync(new CatalogQuestionInput(
                    $"wikidata:{preset}:{qid}",
                    GameMode.GuessImage,
                    descriptor.EntityType,
                    label,
                    descriptor.Prompt,
                    label,
                    difficulty,
                    MediaKind.Image,
                    commons.MediaUrl ?? image,
                    commons.SourceUrl ?? itemUri,
                    commons.License ?? "Wikimedia Commons; license metadata unavailable, verify before use",
                    [],
                    descriptor.Tags.Concat(["Wikidata", "Wikimedia Commons"]).ToArray(),
                    metadata));
                imported++;
                Console.WriteLine($"[{imported}] {label}");
            }
            catch (Exception exception)
            {
                skipped++;
                Console.Error.WriteLine($"Skip {label}: {exception.Message}");
            }
        }

        Console.WriteLine($"Wikidata import complete: {imported} imported, {skipped} skipped.");
        return 0;
    }

    private static async Task<int> RunSelfTestAsync()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "lumo-importer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var database = Path.Combine(tempRoot, "lumo.db");
            var store = await CreateStoreAsync(database);
            var input = new CatalogQuestionInput(
                "self-test:song:1",
                GameMode.GuessSong,
                "Song",
                "Lumo Self Test",
                "🎵 test",
                "First Answer",
                2,
                MediaKind.Audio,
                Path.Combine(tempRoot, "clip.mp3"),
                null,
                "test",
                ["First"],
                ["测试"]);

            await store.UpsertQuestionAsync(input);
            await store.UpsertQuestionAsync(input with
            {
                Answer = "Second Answer",
                Aliases = ["Second"],
                Tags = ["测试", "幂等"]
            });

            var count = await store.GetQuestionCountAsync(GameMode.GuessSong);
            var question = await store.GetRandomQuestionAsync(GameMode.GuessSong);
            if (count != 1 || question?.Answer != "Second Answer" || !question.Tags.Contains("幂等"))
            {
                throw new InvalidOperationException("Importer self-test failed: idempotent upsert did not preserve one updated row.");
            }

            Console.WriteLine("Lumo importer self-test passed.");
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private static async Task<SqliteLumoStore> CreateStoreAsync(string databasePath)
    {
        var store = new SqliteLumoStore(databasePath);
        await store.InitializeAsync();
        return store;
    }

    private static async Task<IReadOnlyList<CatalogQuestionInput>> ReadManifestAsync(string inputPath)
    {
        var text = await File.ReadAllTextAsync(inputPath);
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('['))
        {
            return JsonSerializer.Deserialize<List<CatalogQuestionInput>>(text, JsonOptions)
                ?? [];
        }

        var results = new List<CatalogQuestionInput>();
        foreach (var line in File.ReadLines(inputPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            results.Add(JsonSerializer.Deserialize<CatalogQuestionInput>(line, JsonOptions)
                ?? throw new InvalidOperationException("Invalid JSONL question row."));
        }

        return results;
    }

    private static async Task<AudioProbe> ProbeAudioAsync(string ffprobe, string file)
    {
        var result = await RunProcessAsync(ffprobe,
        [
            "-v", "quiet",
            "-print_format", "json",
            "-show_format",
            file
        ]);

        using var document = JsonDocument.Parse(result.StdOut);
        var format = document.RootElement.GetProperty("format");
        var durationText = format.TryGetProperty("duration", out var durationElement)
            ? durationElement.GetString()
            : null;
        if (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
        {
            throw new InvalidOperationException("ffprobe did not return a valid duration.");
        }

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (format.TryGetProperty("tags", out var tagsElement))
        {
            foreach (var property in tagsElement.EnumerateObject())
            {
                tags[property.Name] = property.Value.ToString();
            }
        }

        return new AudioProbe(duration, tags);
    }

    private static async Task GenerateClipAsync(
        string ffmpeg,
        string input,
        string output,
        double startSeconds,
        double durationSeconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var temp = output + ".tmp.mp3";
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }

        await RunProcessAsync(ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", input,
            "-t", durationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-vn",
            "-ac", "2",
            "-ar", "44100",
            "-c:a", "libmp3lame",
            "-b:a", "128k",
            temp
        ]);

        File.Move(temp, output, true);
    }

    private static IReadOnlyList<double> BuildClipStarts(double totalDuration, double clipDuration, int count)
    {
        var usableEnd = Math.Max(0, totalDuration - clipDuration - 2);
        var usableStart = Math.Min(10, Math.Max(2, totalDuration * 0.08));
        if (usableEnd <= usableStart)
        {
            return [Math.Max(0, (totalDuration - clipDuration) / 2)];
        }

        var fractions = new[] { 0.15, 0.38, 0.61, 0.82, 0.50 };
        return fractions
            .Take(count)
            .Select(fraction => usableStart + (usableEnd - usableStart) * fraction)
            .Select(value => Math.Round(value, 3))
            .Distinct()
            .ToArray();
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {stderr.Trim()}");
        }

        return new ProcessResult(stdout, stderr);
    }

    private static string BuildWikidataQuery(string preset, int limit)
    {
        var where = preset switch
        {
            "countries" => "?item wdt:P31 wd:Q6256; wdt:P41 ?image.",
            "cities" => "?item wdt:P31 wd:Q515; wdt:P18 ?image.",
            "landmarks" => "?item wdt:P31 wd:Q570116; wdt:P18 ?image.",
            _ => throw new ArgumentException("Unknown Wikidata preset. Use countries, cities, or landmarks.")
        };

        return "SELECT DISTINCT ?item ?itemLabel ?image WHERE {\n" +
               "  " + where + "\n" +
               "  SERVICE wikibase:label { bd:serviceParam wikibase:language \"zh,en\". }\n" +
               "}\nLIMIT " + limit.ToString(CultureInfo.InvariantCulture);
    }

    private static WikidataPreset GetWikidataPreset(string preset)
    {
        return preset switch
        {
            "countries" => new("Country", "🖼️ 猜图：这是哪个国家或地区的旗帜？", ["国家", "国旗", "地理"]),
            "cities" => new("City", "🖼️ 猜图：这是哪座城市？", ["城市", "地理"]),
            "landmarks" => new("Landmark", "🖼️ 猜图：这是哪个著名景点或地标？", ["地标", "景点", "地理"]),
            _ => throw new ArgumentException("Unknown Wikidata preset.")
        };
    }

    private static async Task<CommonsMedia> ResolveCommonsAsync(HttpClient http, string imageUrl)
    {
        var fileName = TryGetCommonsFileName(imageUrl);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return new CommonsMedia(imageUrl, null, null, null, null);
        }

        var api = "https://commons.wikimedia.org/w/api.php?action=query&format=json&formatversion=2" +
                  "&prop=imageinfo&iiprop=url%7Cextmetadata&iiurlwidth=1200" +
                  "&iiextmetadatafilter=LicenseShortName%7CLicenseUrl%7CArtist%7CCredit" +
                  "&titles=" + Uri.EscapeDataString("File:" + fileName);
        using var response = await http.GetAsync(api);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var pages = document.RootElement.GetProperty("query").GetProperty("pages");
        if (pages.GetArrayLength() == 0)
        {
            return new CommonsMedia(imageUrl, null, null, null, null);
        }

        var page = pages[0];
        if (!page.TryGetProperty("imageinfo", out var infos) || infos.GetArrayLength() == 0)
        {
            return new CommonsMedia(imageUrl, null, null, null, null);
        }

        var info = infos[0];
        var mediaUrl = info.TryGetProperty("thumburl", out var thumbUrl)
            ? thumbUrl.GetString()
            : info.TryGetProperty("url", out var url) ? url.GetString() : imageUrl;
        var sourceUrl = info.TryGetProperty("descriptionurl", out var description) ? description.GetString() : null;
        string? license = null;
        string? artist = null;
        string? credit = null;
        if (info.TryGetProperty("extmetadata", out var metadata))
        {
            var shortName = GetMetadataValue(metadata, "LicenseShortName");
            var licenseUrl = GetMetadataValue(metadata, "LicenseUrl");
            license = string.Join(" ", new[] { shortName, licenseUrl }.Where(value => !string.IsNullOrWhiteSpace(value)));
            artist = CleanHtml(GetMetadataValue(metadata, "Artist"));
            credit = CleanHtml(GetMetadataValue(metadata, "Credit"));
        }

        return new CommonsMedia(mediaUrl, sourceUrl, license, artist, credit);
    }

    private static string? TryGetCommonsFileName(string imageUrl)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        const string marker = "/Special:FilePath/";
        var index = uri.AbsolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        return Uri.UnescapeDataString(uri.AbsolutePath[(index + marker.Length)..]);
    }

    private static string? GetMetadataValue(JsonElement metadata, string name)
    {
        return metadata.TryGetProperty(name, out var property) &&
               property.TryGetProperty("value", out var value)
            ? value.GetString()
            : null;
    }

    private static string? CleanHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", string.Empty)).Trim();
    }

    private static string? GetBinding(JsonElement binding, string name)
    {
        return binding.TryGetProperty(name, out var property) &&
               property.TryGetProperty("value", out var value)
            ? value.GetString()
            : null;
    }

    private static string RequireFile(CliOptions options, string key)
    {
        var path = options.Require(key);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"File not found: {path}", path);
        }

        return Path.GetFullPath(path);
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    return property.Value.ToString();
                }
            }
        }

        return null;
    }

    private static string[] BuildTags(params string?[] values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] SplitCsv(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.First(value => !string.IsNullOrWhiteSpace(value))!.Trim();

    private static string? FirstNonEmptyOrNull(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string HashKey(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Lumo bulk catalog importer

            Commands:
              manifest --input questions.json[|l] [--database data/lumo.db]
              idioms  --input idioms.json [--license MIT] [--database data/lumo.db]
              music   --root D:\Music [--clips data/media/music] [--clip-count 3] [--duration 8]
                      [--difficulty 2] [--tags 冷门,华语] [--ffmpeg ffmpeg] [--ffprobe ffprobe]
              wikidata --preset countries|cities|landmarks [--limit 100] [--difficulty 2]
              self-test

            The music importer scans MP3/FLAC/M4A/AAC/OGG/OPUS/WAV/WMA metadata with ffprobe,
            creates multiple MP3 quiz clips with ffmpeg, and stores local clip paths in Lumo.

            Manifest records use CatalogQuestionInput fields and accept string enum names,
            for example GameMode=GuessMovie and MediaKind=Image.
            """);
    }
}

internal sealed class CliOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            if (!current.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument: {current}");
            }

            var key = current[2..];
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options._values[key] = "true";
                continue;
            }

            options._values[key] = args[++index];
        }

        return options;
    }

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public string Require(string key)
        => Get(key) ?? throw new ArgumentException($"Missing required option --{key}.");

    public int GetInt(string key, int defaultValue)
        => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;

    public double GetDouble(string key, double defaultValue)
        => double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
}

internal sealed record AudioProbe(
    double DurationSeconds,
    IReadOnlyDictionary<string, string> Tags)
{
    public string? GetTag(string name)
        => Tags.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

internal sealed record ProcessResult(string StdOut, string StdErr);
internal sealed record WikidataPreset(string EntityType, string Prompt, IReadOnlyList<string> Tags);
internal sealed record CommonsMedia(string? MediaUrl, string? SourceUrl, string? License, string? Artist, string? Credit);
