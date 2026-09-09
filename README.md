# LumoBot

Lumo is a cross-platform group entertainment and quiz bot. Telegram and WeChat share the same game engine, question catalog, answer matching and score system.

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
- ✈️ Telegram adapter
- 💬 WeChat adapter through a Wechaty gateway

Discord is intentionally postponed while the WeChat path is developed first.

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
                     └─────────┬─────────┘
                               ▼
                             SQLite
```

The platform layer only translates messages and sends media. Game rules stay platform-independent.

## Requirements

- .NET 10 SDK
- Telegram: a BotFather token
- WeChat: Node.js 20+ and a compatible Wechaty Puppet / Puppet Service token

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

The MVP schema separates reusable entities from individual questions:

- `Entities`: movie, city, landmark, idiom, song, show, person, etc.
- `Questions`: game mode, prompt, answer, difficulty and media.
- `QuestionAliases`: accepted alternate answers, English names and abbreviations.
- `QuestionTags`: country, era, genre, difficulty pool and other filters.
- media metadata: source URL and license/attribution fields.

This lets importers attach many questions/media assets to the same movie, song or other entity without changing the game engine.

## Personal WeChat note

Personal WeChat automation is not an official Telegram-style Bot API. Lumo deliberately isolates the protocol provider behind Wechaty so Paimon, PadLocal or another compatible Puppet Service can be swapped without rewriting the game engine. Provider stability, supported media types and account-risk profile can differ, so evaluate the selected provider with a dedicated test account before long-running deployment.

## Roadmap

1. Telegram MVP and reusable game engine ✅
2. WeChat adapter / Wechaty gateway ✅ initial version
3. Idiom-chain mode
4. Bulk importers for idioms, Wikidata/Wikimedia/Openverse and local licensed music libraries
5. Improve WeChat media delivery and deployment
6. Redis-backed distributed game sessions
7. Admin UI and bulk review workflow
8. Discord adapter (later)
