/**
 * The listening end of the dial-out link.
 *
 * When SOLIDWORKS runs on another machine — most often a Windows VM on the Mac
 * that runs dsh — the bridge there connects here, not the other way round.
 * Windows Firewall blocks inbound connections by default and opening a port
 * takes an administrator; an outbound connection takes neither and works from
 * behind the VM's NAT.
 *
 * Before any tool call crosses, both ends prove they hold the pairing token
 * without sending it: HMAC-SHA256 over a fresh nonce from each side. The bridge
 * also checks this end's proof, so a listener without the token cannot drive
 * SOLIDWORKS.
 *
 * One bridge is live at a time, and the first one keeps the link. "Newest wins"
 * looked friendlier and was wrong: two paired machines (a 2016 VM and a 2019
 * VM) took the link from each other on every reconnect, in the middle of calls.
 * A second bridge is refused with the name of the machine that holds the link,
 * and waits. A heartbeat frees the slot within seconds when the live bridge
 * actually goes away (VM paused, network gone), so a restart is never locked out.
 */
import { createHmac, randomBytes, timingSafeEqual } from 'node:crypto'
import { createServer } from 'node:net'
import { McpClient } from './mcp.js'

/** Versioned label inside every MAC, shared with bridge/src/Link.cs. */
export const LABEL = 'dsh-solidworks/1'

/**
 * HMAC-SHA256 as lowercase hex, the exact construction the bridge uses.
 * @param {string} token - the pairing token.
 * @param {string} message - what is being proven.
 */
export function mac(token, message) {
  return createHmac('sha256', Buffer.from(token, 'utf8')).update(message, 'utf8').digest('hex')
}

function sameHex(a, b) {
  const x = Buffer.from(String(a), 'utf8')
  const y = Buffer.from(String(b), 'utf8')
  return x.length === y.length && timingSafeEqual(x, y)
}

export class LinkServer {
  /**
   * @param {object} options
   * @param {string} options.host - interface to listen on.
   * @param {number} options.port - TCP port.
   * @param {string} options.token - the pairing token.
   * @param {object} options.clientInfo - MCP clientInfo for the handshake.
   * @param {{info: Function, warn: Function}} options.logger - where link events go.
   */
  constructor({ host, port, token, clientInfo, logger, heartbeatMs = 15_000, pingTimeoutMs = 10_000 }) {
    this.heartbeatMs = heartbeatMs
    this.pingTimeoutMs = pingTimeoutMs
    this.host = host
    this.port = port
    this.token = token
    this.clientInfo = clientInfo
    this.logger = logger
    /** @type {{client: McpClient, bridge: object, since: Date} | null} */
    this.live = null
    this.lastRefusal = null
    this.server = createServer((socket) => this.accept(socket))
  }

  /** Start listening. Resolves with the bound port (useful when port is 0). */
  listen() {
    return new Promise((resolve, reject) => {
      this.server.once('error', reject)
      this.server.listen(this.port, this.host, () => {
        this.server.off('error', reject)
        this.port = this.server.address().port
        resolve(this.port)
      })
    })
  }

  close() {
    this.live?.client.fail('dsh is shutting down')
    this.live = null
    return new Promise((resolve) => this.server.close(() => resolve()))
  }

  /** The live bridge's MCP client, or null. */
  get client() {
    return this.live && !this.live.client.closed && this.live.ready ? this.live.client : null
  }

  /**
   * The live client, waiting up to `ms` for a bridge to pair first. A dsh that
   * just started has none yet: the bridge on Windows is between retries.
   */
  async waitForClient(ms) {
    const until = Date.now() + ms
    while (!this.client && Date.now() < until) await new Promise((r) => setTimeout(r, 100))
    return this.client
  }

  accept(socket) {
    socket.setNoDelay(true)
    socket.setKeepAlive(true, 30_000)
    const peer = `${socket.remoteAddress}:${socket.remotePort}`
    const serverNonce = randomBytes(32).toString('base64')
    let buffer = ''
    const deadline = setTimeout(() => refuse('no handshake within 10 s'), 10_000)

    const refuse = (reason, tell = true) => {
      clearTimeout(deadline)
      this.lastRefusal = { peer, reason, at: new Date() }
      this.logger.warn(`refused a bridge from ${peer}: ${reason}`)
      if (tell) socket.end(`${JSON.stringify({ t: 'denied', reason })}\n`)
      else socket.destroy()
    }

    const onData = (chunk) => {
      buffer += chunk
      const newline = buffer.indexOf('\n')
      if (newline < 0) {
        if (buffer.length > 8192) refuse('handshake line too long', false)
        return
      }
      socket.off('data', onData)
      clearTimeout(deadline)
      const rest = buffer.slice(newline + 1)
      let auth
      try {
        auth = JSON.parse(buffer.slice(0, newline))
      } catch {
        return refuse('not the dsh-solidworks link protocol', false)
      }
      if (auth.t !== 'auth' || auth.v !== 1 || typeof auth.nonce !== 'string') return refuse('not the dsh-solidworks link protocol', false)
      if (!sameHex(mac(this.token, `${LABEL} bridge ${serverNonce} ${auth.nonce}`), auth.mac)) {
        return refuse('wrong pairing token; run the pair.cmd dsh wrote again')
      }
      const holder = this.live && !this.live.client.closed ? this.live.bridge : null
      if (holder) {
        return refuse(`another bridge holds the link (${holder.machine ?? holder.peer}, since ${this.live.since.toISOString()}); this one will keep retrying`)
      }
      socket.write(`${JSON.stringify({ t: 'ok', mac: mac(this.token, `${LABEL} server ${auth.nonce} ${serverNonce}`) })}\n`)
      this.adopt(socket, auth.bridge ?? {}, peer, rest)
    }

    socket.setEncoding('utf8')
    socket.on('data', onData)
    socket.on('error', () => {})
    socket.write(`${JSON.stringify({ t: 'hello', v: 1, nonce: serverNonce })}\n`)
  }

  adopt(socket, bridge, peer, rest) {
    const client = new McpClient({
      input: socket,
      write: (line) => socket.write(line),
      close: () => socket.destroy(),
    })
    if (rest) socket.unshift(rest)
    const live = { client, bridge: { ...bridge, peer }, since: new Date(), ready: false }
    this.live = live
    this.logger.info(`bridge connected from ${peer} (${bridge.machine ?? 'unknown machine'}, swbridge ${bridge.version ?? '?'})`)
    client.initialize(this.clientInfo).then(() => { live.ready = true }, (error) => {
      this.logger.warn(`bridge from ${peer} failed the MCP handshake: ${error.message}`)
      client.fail(error.message)
    })
    const heartbeat = setInterval(() => {
      client.request('ping', {}, { timeoutMs: this.pingTimeoutMs }).catch(() => client.fail('the bridge stopped answering'))
    }, this.heartbeatMs)
    heartbeat.unref?.()
    socket.on('close', () => {
      clearInterval(heartbeat)
      client.fail('the bridge disconnected')
      if (this.live?.client === client) {
        this.live = null
        this.logger.info(`bridge from ${peer} disconnected`)
      }
    })
  }
}
