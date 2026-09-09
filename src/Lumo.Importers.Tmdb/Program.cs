using System.Globalization;

var command = args.FirstOrDefault()?.Trim().ToLowerInvariant();
if (command is null or "help" or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

if (command == "self-test")
{
    return TmdbImporter.RunSelfTest();
}

var effectiveArgs = command == "import" ? args.Skip(1).ToArray() : args;
var options = CliOptions.Parse(effectiveArgs);
var databasePath = options.Get("database")
    ?? Environment.GetEnvironmentVariable("LUMO_DATABASE_PATH")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "data", "lumo.db");

try
{
    return await TmdbImporter.RunAsync(databasePath, options);
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
        Lumo TMDB importer

        Authentication:
          $env:TMDB_BEARER_TOKEN="your API Read Access Token"

        Examples:
          dotnet run --project src/Lumo.Importers.Tmdb -- --type movie --pages 5
          dotnet run --project src/Lumo.Importers.Tmdb -- --type movie --pool obscure --pages 10
          dotnet run --project src/Lumo.Importers.Tmdb -- --type tv --language zh-CN --pages 5
          dotnet run --project src/Lumo.Importers.Tmdb -- --type variety --pages 5

        Options:
          --type movie|tv|variety
          --database data/lumo.db
          --token <TMDB bearer token>
          --language zh-CN
          --region CN
          --pages 3
          --page-start 1
          --max-titles 60
          --images-per-title 2
          --also-mixed true|false   (movie only; defaults true)
          --pool all|popular|normal|obscure
          --difficulty auto|1..10
          --original-language zh|en|ja|ko
          --year-from 1990
          --year-to 2026
          --min-votes 20
          --max-votes 0
          --sort popularity.desc

        Movie images are added to the dedicated GuessMovie pool and, by default,
        also to mixed GuessImage. TV and variety imports go to mixed GuessImage.
        Backdrops are preferred over posters so titles are not exposed in the image.
        """);
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

    public int GetInt(string key, int defaultValue)
        => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;

    public bool GetBool(string key, bool defaultValue)
    {
        var value = Get(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "y" or "on" => true,
            "0" or "false" or "no" or "n" or "off" => false,
            _ => defaultValue
        };
    }
}
