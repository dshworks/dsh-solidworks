/**
 * A bridge double for link tests: the swbridge.exe side of the handshake and of
 * MCP, over a real TCP socket.
 *
 * It mirrors bridge/src/Link.cs line for line: the same MAC strings, the same
 * refusal when the server cannot prove the token, and an MCP server that answers
 * ping while a tool call is still running, as the real one does. The point is
 * that the plugin's link is tested against the protocol the exe speaks, not
 * against a friendlier one.
 */
import { randomBytes } from 'node:crypto'
import { connect } from 'node:net'
import { LABEL, mac } from '../lib/link.js'

/**
 * @param {object} options
 * @param {number} options.port - where the plugin listens.
 * @param {string} options.token - the token this bridge holds.
 * @param {Record<string, (args: object) => object | Promise<object>>} [options.tools] - tool handlers.
 * @param {boolean} [options.answerPings] - false simulates a bridge that went silent.
 * @param {string} [options.machine] - reported machine name.
 */
export function fakeBridge({ port, token, tools = {}, answerPings = true, machine = 'TEST-PC' }) {
  const seen = []
  let resolveOutcome
  const outcome = new Promise((r) => { resolveOutcome = r })
  const socket = connect(port, '127.0.0.1')
  socket.setEncoding('utf8')
  let buffer = ''
  let stage = 'hello'
  const bridgeNonce = randomBytes(32).toString('base64')
  let serverNonce = ''
  const send = (obj) => socket.write(`${JSON.stringify(obj)}\n`)

  socket.on('data', async (chunk) => {
    buffer += chunk
    let i
    while ((i = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, i)
      buffer = buffer.slice(i + 1)
      const msg = JSON.parse(line)
      if (stage === 'hello') {
        serverNonce = msg.nonce
        send({ t: 'auth', v: 1, nonce: bridgeNonce, mac: mac(token, `${LABEL} bridge ${serverNonce} ${bridgeNonce}`), bridge: { version: 'test', machine, user: 'tester' } })
        stage = 'answer'
      } else if (stage === 'answer') {
        if (msg.t !== 'ok') { resolveOutcome({ ok: false, reason: msg.reason }); socket.destroy(); return }
        if (msg.mac !== mac(token, `${LABEL} server ${bridgeNonce} ${serverNonce}`)) { resolveOutcome({ ok: false, reason: 'server proof mismatch' }); socket.destroy(); return }
        stage = 'mcp'
        resolveOutcome({ ok: true })
      } else {
        seen.push(msg)
        handle(msg)
      }
    }
  })
  socket.on('close', () => resolveOutcome({ ok: false, reason: 'closed' }))
  socket.on('error', () => {})

  async function handle(msg) {
    if (msg.method === 'initialize') return send({ jsonrpc: '2.0', id: msg.id, result: { protocolVersion: msg.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'fake', version: '0' } } })
    if (msg.method === 'ping') { if (answerPings) send({ jsonrpc: '2.0', id: msg.id, result: {} }); return }
    if (msg.method === 'tools/call') {
      const run = tools[msg.params.name]
      const result = run ? await run(msg.params.arguments) : { content: [{ type: 'text', text: 'BAD_REQUEST: unknown tool' }], isError: true }
      return send({ jsonrpc: '2.0', id: msg.id, result })
    }
    if (msg.method === 'resources/read' && tools['resources/read']) {
      return send({ jsonrpc: '2.0', id: msg.id, result: await tools['resources/read'](msg.params) })
    }
    if (msg.id !== undefined) send({ jsonrpc: '2.0', id: msg.id, error: { code: -32601, message: 'method not found' } })
  }

  return { outcome, seen, close: () => socket.destroy() }
}

/** A tool result in MCP's shape, as the bridge sends it. */
export function ok(facts) {
  return { content: [{ type: 'text', text: JSON.stringify(facts) }], structuredContent: facts }
}

export const wait = (ms) => new Promise((r) => setTimeout(r, ms))

export const quiet = { info() {}, warn() {} }
