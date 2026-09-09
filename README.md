# LumoBot

Lumo is a cross-platform group entertainment and quiz bot. Telegram and WeChat share the same game engine, question catalog, answer matching and score system.

## Current MVP

Implemented now:

- 🎵 Guess Song (`/guesssong`) with local audio-file delivery on Telegram and WeChat
- 🎬 Guess Movie (`/guessmovie`)
- 🖼️ Mixed Guess Image (`/guessimage`)
- 🀄 Guess Idiom (`/guessidiom`)
- 🪙 Coins and group leaderboard (`/coins`, `/rank`)
- ⏱️ Per-chat 30-second rounds
- 🏁 First-correct-answer wins under concurrent replies
- 🔤 Answer aliases and Unicode-normalized matching
- 💾 SQLite catalog, tags, attribution metadata and score persistence
- 🔁 Idempotent catalog imports through stable `ExternalKey` values
- 📚 Bulk idiom JSON importer
- 🎵 Local music-library scanner using ffprobe + FFmpeg; multiple clips per song
- 🌍 Wikidata + Wikimedia Commons image import presets for countries, cities and landmarks
- 🎞️ TMDB importer for movies, TV and variety/reality/talk shows with multiple backdrops per title
- 📦 Generic JSON/JSONL manifest importer for movies, TV, variety shows and custom datasets
- ✈️ Telegram adapter
- 💬 WeChat adapter through a Wechaty gateway

Discord is intentionally postponed while the WeChat path and content catalog are developed first.

## Architecture

```text
Telegram ───────────────┐
                       │
WeChat ─ Wechaty ─ HTTP┼──► ChatGameService
                       │        │
future adapters ───────┘        ▼
                           GameEngine
                               │
                     ┌─────────┴─────────┐
                     ▼                   ▼
                  Catalog              Scores
                     ▲                   │
                     │                   │
              Lumo.Importers             │
        idioms / music / Wikidata        │
      TMDB / generic JSON manifests      │
                     └─────────┬─────────┘
                               ▼
                             SQLite
```

The platform layer only translates messages and sends media. Game rules stay platform-independent.

## Requirements

- .NET 10 SDK
- Telegram: a BotFather token
- WeChat: Node.js 20+ and a compatible Wechaty Puppet / Puppet Service token
- Music importing: FFmpeg + ffprobe
- TMDB importing: a TMDB API Read Access Token

## Run Telegram

PowerShell:

```powershell
$env:LUMO_TELEGRAM_TOKEN="123456:your-token"
dotnet run --project src/Lumo.Bot.Telegram/Lumo.Bot.Telegram.csproj
```

## Run WeChat

Start the .NET game backend:

```powershell
$env:LUMO_WECHAT_GATEWAY_TOKEN="replace-with-a-long-random-secret"
dotnet run --project src/Lumo.Bot.WeChat/Lumo.Bot.WeChat.csproj
```

Then start the Wechaty protocol gateway:

```powershell
cd gateways\wechaty
npm install
$env:WECHATY_PUPPET="wechaty-puppet-service"
$env:WECHATY_PUPPET_SERVICE_TOKEN="your-puppet-service-token"
$env:LUMO_WECHAT_GATEWAY_TOKEN="replace-with-the-same-secret"
npm start
```

See [`docs/wechat.md`](docs/wechat.md) for the full setup and Puppet notes.

Optional shared database path:

```powershell
$env:LUMO_DATABASE_PATH="D:\Lumo\data\lumo.db"
```

By default the SQLite database is created at `data/lumo.db` relative to the process working directory. If Telegram and WeChat run on the same machine and should share scores/catalog data, point both at the same `LUMO_DATABASE_PATH`.

## Build a real question catalog

See [`docs/importers.md`](docs/importers.md) for the complete importer guide.

Examples:

```powershell
# Scan a local music library and create 3 different 8-second clips per song
dotnet run --project src\Lumo.Importers\Lumo.Importers.csproj -- music `
  --root "D:\Music" --clip-count 3 --duration 8 --tags "华语,冷门"

# Import an idiom JSON dataset
dotnet run --project src\Lumo.Importers\Lumo.Importers.csproj -- idioms `
  --input "D:\datasets\idioms.json" --license "MIT"

# Add country/flag questions from Wikidata + Wikimedia Commons
dotnet run --project src\Lumo.Importers\Lumo.Importers.csproj -- wikidata `
  --preset countries --limit 200

# Import TMDB movies into GuessMovie and mixed GuessImage
$env:TMDB_BEARER_TOKEN="your TMDB API Read Access Token"
dotnet run --project src\Lumo.Importers.Tmdb\Lumo.Importers.Tmdb.csproj -- `
  --type movie --language zh-CN --pages 5 --images-per-title 3

# Import custom movie/TV/variety questions
dotnet run --project src\Lumo.Importers\Lumo.Importers.csproj -- manifest `
  --input "examples\questions.sample.json"
```

Imported media can be a normal HTTP(S) URL or a local absolute file path. Telegram uploads local media directly; the Wechaty gateway uses `FileBox.fromFile` when it sees a local path.

## Commands

```text
/guesssong    猜歌曲
/guessmovie   猜电影
/guessimage   猜图（混合题库）
/guessidiom   猜成语
/coins        金币/积分
/rank         本群排行榜
/stop         结束当前回合
```

Chinese text triggers are supported on both Telegram and WeChat: `猜歌`, `猜电影`, `猜图`, `猜成语`, `金币`, `排行榜`, `结束游戏`.

## Catalog design

The schema separates reusable entities from individual questions:

- `Entities`: movie, city, landmark, idiom, song, show, person, etc.
- `Questions`: game mode, prompt, answer, difficulty, media and a stable import `ExternalKey`.
- `QuestionAliases`: accepted alternate answers, English names and abbreviations.
- `QuestionTags`: country, era, genre, difficulty pool and other filters.
- media metadata: source URL and license/attribution fields.

This lets importers attach many questions/media assets to the same movie, song or other entity without changing the game engine. Re-importing the same external key updates the existing question instead of creating duplicates.

## Personal WeChat note

Personal WeChat automation is not an official Telegram-style Bot API. Lumo deliberately isolates the protocol provider behind Wechaty so Paimon, PadLocal or another compatible Puppet Service can be swapped without rewriting the game engine. Provider stability, supported media types and account-risk profile can differ, so evaluate the selected provider with a dedicated test account before long-running deployment.

## Data-source notes

Wikidata Query Service is used for structured entities and Wikimedia Commons `imageinfo/extmetadata` is used to capture image source/license metadata. Commons license metadata should still be reviewed before public deployment.

For music, Lumo does not fetch commercial recordings from MusicBrainz or streaming sites. The importer works from audio files you provide and have the right to use. MusicBrainz can later be added as optional metadata enrichment rather than as an audio source.

TMDB imports movie/TV metadata and backdrops. TMDB requires attribution for developer API use, including the notice: `This product uses the TMDB API but is not endorsed or certified by TMDB.` See `docs/importers.md` for details. Third-party image rights still need to be considered for the way you deploy the bot.

## Roadmap

1. Telegram MVP and reusable game engine ✅
2. WeChat adapter / Wechaty gateway ✅ initial version
3. Bulk catalog import core ✅
4. TMDB movie / TV / variety importer ✅ initial version
5. Idiom-chain mode
6. Better difficulty/popularity weighting and anti-repeat scheduling
7. Redis-backed distributed game sessions
8. Admin UI and bulk review workflow
9. Discord adapter (later)
