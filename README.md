# LumoBot

Lumo is a cross-platform group entertainment and quiz bot. The same game engine is designed to serve Telegram, Discord and, later, WeChat adapters.

## MVP games

- 🎵 Guess Song (`/guesssong`)
- 🎬 Guess Movie (`/guessmovie`)
- 🖼️ Mixed Guess Image (`/guessimage`)
- 🀄 Guess Idiom (`/guessidiom`)
- 🔗 Idiom Chain (planned next)
- 🪙 Coins and leaderboard (`/coins`, `/rank`)

## Architecture

```text
Telegram / Discord / WeChat
          │
          ▼
      Chat Adapter
          │
          ▼
   Lumo.Application
   GameEngine + matching
          │
    ┌─────┴─────┐
    ▼           ▼
Questions     Scores
    │           │
    └─────┬─────┘
          ▼
      SQLite MVP
```

The platform layer only translates messages and sends media. Game rules, answer matching, question catalog and score logic stay platform-independent.

## Requirements

- .NET 10 SDK
- A Telegram bot token from BotFather

## Run Telegram bot

```bash
export LUMO_TELEGRAM_TOKEN="123456:your-token"
dotnet run --project src/Lumo.Bot.Telegram/Lumo.Bot.Telegram.csproj
```

On Windows PowerShell:

```powershell
$env:LUMO_TELEGRAM_TOKEN="123456:your-token"
dotnet run --project src/Lumo.Bot.Telegram/Lumo.Bot.Telegram.csproj
```

The SQLite database is created automatically in `data/lumo.db`. Sample quiz content is seeded on first run. Replace demo media with your own licensed/open content before public deployment.

## Telegram group setup

For natural group answers (users type the answer directly instead of replying with a command), disable Privacy Mode for the bot in BotFather or grant the bot the appropriate group access.

## Data and copyright

Lumo intentionally separates metadata from media assets. Each media asset can store source URL, license and attribution metadata. Do not import copyrighted movie screenshots or commercial music recordings without the right to redistribute them.

## Roadmap

1. Telegram MVP and reusable game engine
2. Rich importers for idioms, Wikidata/Wikimedia/Openverse and local music libraries
3. Discord adapter
4. Redis-backed distributed game sessions
5. Admin UI and bulk review workflow
6. WeChat adapter
