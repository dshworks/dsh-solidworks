import { createServer } from 'node:net'
import { afterEach, describe, expect, it } from 'vitest'
import { LinkServer, mac } from '../lib/link.js'
import { fakeBridge, ok, quiet, wait } from './fake-bridge.mjs'

const TOKEN = 'a-pairing-token-long-enough-to-be-real'
const servers = []
async function listen(options = {}) {
  const server = new LinkServer({ host: '127.0.0.1', port: 0, token: TOKEN, clientInfo: { name: 't', version: '0' }, logger: quiet, ...options })
  await server.listen()
  servers.push(server)
  return server
}
async function connected(server) {
  for (let i = 0; i < 100 && !(server.client && server.client.serverInfo); i++) await wait(10)
  return server.client
}
afterEach(async () => { await Promise.all(servers.splice(0).map((s) => s.close())) })

describe('link handshake', () => {
  it('computes the same HMAC as the bridge (vector shared with swbridge selftest)', () => {
    expect(mac('test-token', 'dsh-solidworks/1 bridge AAAA BBBB')).toBe('4ff47302c3f3c645618457d02ce8b167f0ec61c149f20f21553e44f7b3a08389')
  })

  it('pairs a bridge holding the token and carries tool calls', async () => {
    const server = await listen()
    const bridge = fakeBridge({ port: server.port, token: TOKEN, tools: { sw_status: () => ok({ release: 'SOLIDWORKS 2016 SP1' }) } })
    expect(await bridge.outcome).toEqual({ ok: true })
    const client = await connected(server)
    const result = await client.callTool('sw_status', {}, { timeoutMs: 2000 })
    expect(result.structuredContent.release).toBe('SOLIDWORKS 2016 SP1')
    expect(server.live.bridge.machine).toBe('TEST-PC')
    bridge.close()
  })

  it('refuses a bridge with the wrong token and says why', async () => {
    const server = await listen()
    const bridge = fakeBridge({ port: server.port, token: 'some-other-token-of-the-same-length!!' })
    expect(await bridge.outcome).toEqual({ ok: false, reason: expect.stringContaining('wrong pairing token') })
    expect(server.client).toBeNull()
    expect(server.lastRefusal.reason).toContain('wrong pairing token')
  })

  it('lets the bridge reject a listener that cannot prove the token', async () => {
    // An impostor that says "ok" without knowing the token: the bridge must not serve it.
    const impostor = createServer((socket) => {
      socket.write(`${JSON.stringify({ t: 'hello', v: 1, nonce: 'bm9uY2U=' })}\n`)
      socket.once('data', () => socket.write(`${JSON.stringify({ t: 'ok', mac: '0'.repeat(64) })}\n`))
    })
    await new Promise((r) => impostor.listen(0, '127.0.0.1', r))
    const bridge = fakeBridge({ port: impostor.address().port, token: TOKEN })
    expect(await bridge.outcome).toEqual({ ok: false, reason: 'server proof mismatch' })
    impostor.close()
  })

  it('keeps the first bridge and refuses a second one by name', async () => {
    const server = await listen()
    const first = fakeBridge({ port: server.port, token: TOKEN, machine: 'VM-2016' })
    expect((await first.outcome).ok).toBe(true)
    await connected(server)
    const second = fakeBridge({ port: server.port, token: TOKEN, machine: 'VM-2019' })
    expect(await second.outcome).toEqual({ ok: false, reason: expect.stringContaining('VM-2016') })
    expect(server.live.bridge.machine).toBe('VM-2016')
    first.close()
  })

  it('frees the slot when the live bridge stops answering, so another can pair', async () => {
    const server = await listen({ heartbeatMs: 50, pingTimeoutMs: 100 })
    const silent = fakeBridge({ port: server.port, token: TOKEN, answerPings: false, machine: 'GONE' })
    expect((await silent.outcome).ok).toBe(true)
    await connected(server)
    for (let i = 0; i < 100 && server.client; i++) await wait(10)
    expect(server.client).toBeNull()
    const next = fakeBridge({ port: server.port, token: TOKEN, machine: 'BACK' })
    expect((await next.outcome).ok).toBe(true)
    silent.close()
    next.close()
  })

  it('sends notifications/cancelled when dsh aborts a call', async () => {
    const server = await listen()
    let release
    const bridge = fakeBridge({ port: server.port, token: TOKEN, tools: { sw_open: () => new Promise((r) => { release = r }) } })
    await bridge.outcome
    const client = await connected(server)
    const abort = new AbortController()
    const call = client.callTool('sw_open', { path: 'C:\\a.sldprt' }, { signal: abort.signal, timeoutMs: 5000 })
    await wait(30)
    abort.abort()
    await expect(call).rejects.toMatchObject({ code: 'CANCELLED' })
    await wait(30)
    const cancel = bridge.seen.find((m) => m.method === 'notifications/cancelled')
    const sent = bridge.seen.find((m) => m.method === 'tools/call')
    expect(cancel.params.requestId).toBe(sent.id)
    release(ok({}))
    bridge.close()
  })

  it('fails in-flight calls when the bridge disconnects', async () => {
    const server = await listen()
    const bridge = fakeBridge({ port: server.port, token: TOKEN, tools: { sw_rebuild: () => new Promise(() => {}) } })
    await bridge.outcome
    const client = await connected(server)
    const call = client.callTool('sw_rebuild', {}, { timeoutMs: 5000 })
    await wait(30)
    bridge.close()
    await expect(call).rejects.toMatchObject({ code: 'NOT_CONNECTED' })
  })
})
