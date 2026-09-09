import qrcodeTerminal from 'qrcode-terminal'
import { FileBox } from 'file-box'
import { WechatyBuilder } from 'wechaty'

const backendUrl = (process.env.LUMO_WECHAT_BACKEND_URL || 'http://127.0.0.1:5080').replace(/\/$/, '')
const gatewayToken = process.env.LUMO_WECHAT_GATEWAY_TOKEN || ''
const puppet = process.env.WECHATY_PUPPET
const allowPrivate = /^(1|true|yes)$/i.test(process.env.LUMO_WECHAT_ALLOW_PRIVATE || '')
const botName = process.env.WECHATY_NAME || 'lumo-wechat'

if (!puppet) {
  console.error('Missing WECHATY_PUPPET. Example: wechaty-puppet-service')
  process.exit(2)
}

const bot = WechatyBuilder.build({
  name: botName,
  puppet,
})

bot.on('scan', (qrcode, status) => {
  console.log(`Wechaty scan status: ${status}`)
  qrcodeTerminal.generate(qrcode, { small: true })
  console.log(`QR URL: https://wechaty.js.org/qrcode/${encodeURIComponent(qrcode)}`)
})

bot.on('login', user => {
  console.log(`Lumo WeChat gateway logged in as ${user.name()}`)
})

bot.on('logout', user => {
  console.log(`Lumo WeChat gateway logged out: ${user.name()}`)
})

bot.on('error', error => {
  console.error('Wechaty error:', error)
})

bot.on('message', async message => {
  try {
    if (message.self() || message.age() > 120) {
      return
    }

    const room = message.room()
    if (!room && !allowPrivate) {
      return
    }

    const text = message.text()?.trim()
    if (!text) {
      return
    }

    const talker = message.talker()
    const displayName = room
      ? (await room.alias(talker)) || talker.name()
      : talker.name()

    const conversationId = room ? room.id : `private:${talker.id}`

    const response = await fetch(`${backendUrl}/api/wechat/messages`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        ...(gatewayToken ? { 'x-lumo-gateway-token': gatewayToken } : {}),
      },
      body: JSON.stringify({
        conversationId,
        senderId: talker.id,
        senderName: displayName,
        text,
        messageId: message.id,
        isGroup: Boolean(room),
      }),
    })

    if (!response.ok) {
      const body = await response.text()
      throw new Error(`Lumo backend returned ${response.status}: ${body}`)
    }

    const payload = await response.json()
    for (const action of payload.actions || []) {
      await sendAction(message, action)
    }
  } catch (error) {
    console.error('Failed to handle WeChat message:', error)
  }
})

async function sendAction(message, action) {
  if (action.kind === 'image' || action.kind === 'audio') {
    if (action.mediaUrl) {
      await message.say(FileBox.fromUrl(action.mediaUrl))
    }

    if (action.text) {
      await message.say(action.text)
    }

    return
  }

  if (action.text) {
    await message.say(action.text)
  }
}

console.log(`Connecting Wechaty gateway to ${backendUrl}`)
await bot.start()
