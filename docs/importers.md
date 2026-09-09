# Lumo catalog importers

`Lumo.Importers` builds the large shared question catalog used by Telegram and WeChat.

All importer commands are idempotent through `ExternalKey`: running the same import repeatedly updates the existing question instead of creating duplicates.

## Common database

By default importers write to `data/lumo.db`. To target the same database used by your bot:

```powershell
$env:LUMO_DATABASE_PATH="D:\LumoBot\data\lumo.db"
```

You can also pass `--database` on each command.

## Local music library

Requirements:

- FFmpeg and ffprobe available in `PATH`, or pass their full executable paths.
- Audio you have the right to use in the bot.

Supported input extensions currently include MP3, FLAC, M4A, MP4 audio, AAC, OGG, OPUS, WAV and WMA.

Example:

```powershell
dotnet run --project src\Lumo.Importers\Lumo.Importers.csproj -- music `
  --root "D:\Music" `
  --clips "D:\LumoBot\data\media\music" `
  --clip-count 3 `
  --duration 8 `
  --difficulty 2 `
  --tags "华语,冷门"
```

For every song Lumo:

1. calls `ffprobe` to read duration and embedded metadata such as title, artist, album, year and genre;
2. picks several positions across the recording;
3. creates short MP3 clips with FFmpeg;
4. stores each clip as a separate `GuessSong` question attached to the same song entity;
5. creates a stable `ExternalKey`, so rerunning the scan updates rather than duplicates the question.

The generated clip path is stored as local media. Telegram uploads local media with multipart/form-data, and the Wechaty gateway uses `FileBox.fromFile`, so a CDN is not required for a single-machine deployment.

If the bot and Wechaty gateway run on different machines, move generated media to shared/object storage and import a manifest containing reachable media URLs instead.

## Idioms

The idiom importer accepts a top-level JSON array. It recognizes several common field names:

- idiom text: `word`, `idiom`, or `name`
- definition: `explanation`, `definition`, or `desc`
- source: `derivation`, `source`, or `origin`
- optional: `pinyin`, `example`

Example:

```powershell
dotnet run --project src\Lumo.Importers\Lumo.Importers.csproj -- idioms `
  --input "D:\datasets\idioms.json" `
  --license "MIT"
```

Lumo generates `GuessIdiom` questions from the definitions and stores pinyin/source/example metadata with the entity.

## Wikidata + Wikimedia Commons

Built-in mixed-image presets:

```powershell
# Countries / flags
dotnet run --project src\Lumo.Importers\Lumo.Importers.csproj -- wikidata --preset countries --limit 200

# Cities
dotnet run --project src\Lumo.Importers\Lumo.Importers.csproj -- wikidata --preset cities --limit 200

# Landmarks / tourist attractions
dotnet run --project src\Lumo.Importers\Lumo.Importers.csproj -- wikidata --preset landmarks --limit 200
```

The importer uses Wikidata Query Service for structured entities and then resolves each image through the Wikimedia Commons Action API. It stores the Commons description URL, license name/URL when available, author/credit metadata, and asks Commons for a raster thumbnail so SVG assets such as flags work better in chat clients.

Wikidata/Commons data quality and media licensing still need review before public deployment. Imported license fields are there to make that review possible rather than to replace it.

## Generic manifest

For movies, TV shows, variety shows, people, games, custom image packs and any other source, use the generic manifest importer.

It accepts either a JSON array or JSON Lines (`.jsonl`). Enum names can be strings.

```powershell
dotnet run --project src\Lumo.Importers\Lumo.Importers.csproj -- manifest `
  --input "D:\datasets\movies.jsonl"
```

Example record:

```json
{
  "externalKey": "movie:imdb:tt1533117:scene:001",
  "gameMode": "GuessMovie",
  "entityType": "Movie",
  "entityName": "让子弹飞",
  "prompt": "🎬 猜电影：这是哪部电影？",
  "answer": "让子弹飞",
  "difficulty": 2,
  "mediaKind": "Image",
  "mediaUrl": "https://example.com/media/let-the-bullets-fly-001.jpg",
  "sourceUrl": "https://example.com/source/001",
  "license": "licensed source",
  "aliases": ["Let the Bullets Fly"],
  "tags": ["电影", "华语", "2010s"]
}
```

A single movie/show/song/entity can have many question records with different `ExternalKey` values and different media while sharing the same `entityType + entityName`.

## Self-test

```powershell
dotnet run --project src\Lumo.Importers\Lumo.Importers.csproj -- self-test
```

The self-test creates a temporary SQLite database and verifies that importing the same external key twice results in one updated question. GitHub Actions runs this automatically.
