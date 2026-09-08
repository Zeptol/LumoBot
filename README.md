# LumoBot

Lumo is a cross-platform group entertainment and quiz bot. The same game engine is designed to serve Telegram, Discord and, later, WeChat adapters.

## Current MVP

Implemented now:

- 🎵 Guess Song (`/guesssong`) — engine and audio delivery are ready; import licensed/open audio to populate the song pool.
- 🎬 Guess Movie (`/guessmovie`)
- 🖼️ Mixed Guess Image (`/guessimage`)
- 🀄 Guess Idiom (`/guessidiom`)
- 🪙 Coins and group leaderboard (`/coins`, `/rank`)
- ⏱️ Per-chat 30-second rounds
- 🏁 First-correct-answer wins under concurrent replies
- 🔤 Answer aliases and Unicode-normalized matching
- 💾 SQLite catalog, tags, attribution metadata and score persistence

Planned next: idiom chain, bulk importers, Discord, Redis-backed sessions, admin UI and WeChat.

## Architecture

```text
Telegram / Discord / WeChat
          │
          ▼
      Chat Adapter
          │
          ▼
      Lumo.Games
  GameEngine + matching
          │
          ▼
   Lumo.Application
       contracts
          │
    ┌─────┴─────┐
    ▼           ▼
Catalog       Scores
    │           │
    └─────┬─────┘
          ▼
 Lumo.Infrastructure
        SQLite
```

The platform layer only translates messages and sends media. Game rules, answer matching, question catalog and score logic stay platform-independent.

## Requirements

- .NET 10 SDK
- A Telegram bot token from BotFather

## Run the Telegram bot

Linux/macOS:

```bash
export LUMO_TELEGRAM_TOKEN="123456:your-token"
dotnet run --project src/Lumo.Bot.Telegram/Lumo.Bot.Telegram.csproj
```

Windows PowerShell:

```powershell
$env:LUMO_TELEGRAM_TOKEN="123456:your-token"
dotnet run --project src/Lumo.Bot.Telegram/Lumo.Bot.Telegram.csproj
```

Optional custom database location:

```powershell
$env:LUMO_DATABASE_PATH="D:\Lumo\data\lumo.db"
```

By default the SQLite database is created at `data/lumo.db`. Sample idiom, movie and mixed-image questions are seeded on first run. The song engine intentionally starts without commercial recordings; add audio you have the right to redistribute.

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

Chinese text triggers are also supported: `猜歌`, `猜电影`, `猜图`, `猜成语`, `金币`, `排行榜`, `结束游戏`.

## Telegram group setup

For natural group answers — users simply type the answer instead of replying with a command — disable Privacy Mode for the bot in BotFather or grant the bot the appropriate group access.

## Catalog design

The MVP schema already separates reusable entities from individual questions:

- `Entities`: movie, city, landmark, idiom, song, show, person, etc.
- `Questions`: game mode, prompt, answer, difficulty and media.
- `QuestionAliases`: accepted alternate answers, English names and abbreviations.
- `QuestionTags`: country, era, genre, difficulty pool and other filters.
- media metadata: source URL and license/attribution fields.

This lets a future importer attach many questions/media assets to the same movie, song or other entity without changing the game engine.

## Data and copyright

Lumo intentionally separates metadata from media assets. Keep source and license information for imported assets. Do not redistribute copyrighted movie screenshots, TV footage, variety-show frames, lyrics or commercial music recordings unless you have the necessary rights.

## Roadmap

1. Telegram MVP and reusable game engine
2. Idiom-chain mode
3. Importers for idioms, Wikidata/Wikimedia/Openverse and local licensed music libraries
4. Discord adapter
5. Redis-backed distributed game sessions
6. Admin UI and bulk review workflow
7. WeChat adapter
