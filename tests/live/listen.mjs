#!/usr/bin/env node
// Wait for one bridge, call sw_status once, print what it says. For checking a pairing by hand:
//   SW_LINK_TOKEN=... node tests/live/listen.mjs
import { LinkServer } from '../../lib/link.js'
const server = new LinkServer({ host: '0.0.0.0', port: Number(process.env.SW_LINK_PORT ?? 47321), token: process.env.SW_LINK_TOKEN, clientInfo: { name: 'listen', version: '0' }, logger: { info: console.log, warn: console.log } })
await server.listen()
console.log(`listening on ${server.port}`)
const client = await server.waitForClient(Number(process.env.SW_WAIT_MS ?? 120_000))
if (!client) { console.log('no bridge connected'); process.exit(1) }
const status = await client.callTool('sw_status', {}, { timeoutMs: 60_000 })
console.log(JSON.stringify({ bridge: server.live.bridge, solidworks: status.structuredContent?.solidworks ?? status.content?.[0]?.text }))
await server.close()
