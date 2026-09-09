# WeChat adapter

Lumo keeps WeChat protocol access outside the .NET game engine.

```text
WeChat account
    │
    ▼
Wechaty + selected Puppet
    │  HTTP
    ▼
Lumo.Bot.WeChat
    │
    ▼
ChatGameService / GameEngine / SQLite
```

This split is intentional: personal WeChat does not provide a Telegram-style official Bot API, and Puppet implementations can change over time. Replacing a Puppet should not require changing Lumo's games, scores or question catalog.

## 1. Start the .NET WeChat backend

PowerShell:

```powershell
$env:LUMO_WECHAT_GATEWAY_TOKEN="replace-with-a-long-random-secret"
$env:LUMO_WECHAT_BIND_URL="http://127.0.0.1:5080"
dotnet run --project src/Lumo.Bot.WeChat/Lumo.Bot.WeChat.csproj
```

Optional shared database path:

```powershell
$env:LUMO_DATABASE_PATH="C:\LumoBot\data\lumo.db"
```

Health check:

```text
GET http://127.0.0.1:5080/health
```

## 2. Install the Wechaty gateway

```powershell
cd gateways\wechaty
npm install
```

The repository includes `wechaty-puppet-service`. If you choose a different direct Puppet provider, install that provider package as well and set `WECHATY_PUPPET` to its package name.

## 3. Recommended route: Puppet Service

Example using Wechaty Puppet Service:

```powershell
$env:WECHATY_PUPPET="wechaty-puppet-service"
$env:WECHATY_PUPPET_SERVICE_TOKEN="your-puppet-service-token"
$env:LUMO_WECHAT_GATEWAY_TOKEN="replace-with-the-same-secret-as-the-dotnet-backend"
$env:LUMO_WECHAT_BACKEND_URL="http://127.0.0.1:5080"
$env:WECHATY_LOG="info"
npm start
```

Wechaty currently lists personal-WeChat services such as Paimon and PadLocal in its Puppet Service documentation. The token identifies the underlying service, so Lumo's gateway source does not need to know whether the provider is Paimon, PadLocal or another compatible service.

This is the preferred Lumo route because the game uses image and audio delivery and does not want to be tied to one desktop WeChat build.

### Optional Windows-only route: Puppet XP

`wechaty-puppet-xp` is a free local Windows Puppet and does not require a service token, but it is tightly coupled to specific desktop WeChat versions. At the time this integration was added, its project documents `wechaty-puppet-xp@2.1.1` for WeChat `3.9.10.27`; the compatibility table marks text send/receive as supported while newer-version media-send support is incomplete.

For text-only development or experiments:

```powershell
cd gateways\wechaty
npm install wechaty-puppet-xp@2.1.1
$env:WECHATY_PUPPET="wechaty-puppet-xp"
$env:LUMO_WECHAT_GATEWAY_TOKEN="replace-with-the-same-secret-as-the-dotnet-backend"
npm start
```

Do not downgrade a main WeChat installation just for Lumo. Use a dedicated test environment if evaluating this route.

## 4. Login and test in a group

When the selected Puppet requests QR login, the gateway prints a terminal QR code and a QR URL.

Add the bot account to a test group and send:

```text
菜单
猜成语
猜电影
猜图
金币
排行榜
```

After a question starts, group members answer with ordinary messages; no slash command is required.

Private chats are ignored by default. To enable them:

```powershell
$env:LUMO_WECHAT_ALLOW_PRIVATE="true"
```

## Gateway contract

Wechaty sends this shape to `POST /api/wechat/messages`:

```json
{
  "conversationId": "room-or-contact-id",
  "senderId": "wechat-contact-id",
  "senderName": "display name",
  "text": "猜电影",
  "messageId": "optional-message-id",
  "isGroup": true
}
```

Lumo returns actions:

```json
{
  "actions": [
    {
      "kind": "text",
      "text": "...",
      "mediaUrl": null
    }
  ]
}
```

Supported action kinds are `text`, `image` and `audio`.

## Operational note

Personal WeChat automation is not the same as an official public Bot API. Puppet providers differ in protocol, supported features and account-risk profile. Use a dedicated test account while evaluating a provider, avoid spam or high-frequency unsolicited messaging, and verify the provider's current terms before relying on it for a long-running public bot.
