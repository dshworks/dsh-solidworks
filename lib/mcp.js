/**
 * A minimal MCP client over one line-delimited JSON-RPC stream.
 *
 * The bridge speaks MCP in both modes — over a spawned child's stdio and over the
 * dial-out socket — so the plugin needs exactly one client. It sends requests,
 * matches responses by id, and turns an aborted call into
 * `notifications/cancelled`, which the bridge maps to "drop it if it has not
 * started". Nothing here knows about SOLIDWORKS.
 */

/** Newest first; the bridge echoes the one it supports. */
export const PROTOCOL_VERSION = '2025-06-18'

/** Raised when the stream ends or a request outlives its deadline. */
export class LinkError extends Error {
  /**
   * @param {string} code - closed vocabulary shared with protocol/tools.json.
   * @param {string} message - a sentence the model can act on.
   */
  constructor(code, message) {
    super(message)
    this.code = code
  }
}

export class McpClient {
  /**
   * @param {object} wire - the two halves of the stream.
   * @param {import('node:stream').Readable} wire.input - bytes from the bridge.
   * @param {(line: string) => void} wire.write - send one JSON line to the bridge.
   * @param {(reason: string) => void} [wire.close] - tear the stream down.
   * @param {() => string | Promise<string>} [wire.ended] - why the stream ended, when the
   *   owner knows better than "closed" (a spawned bridge's exit code and last words).
   */
  constructor({ input, write, close, ended }) {
    this.write = write
    this.closeStream = close ?? (() => {})
    this.pending = new Map()
    this.nextId = 1
    this.closed = null
    this.serverInfo = null
    let buffer = ''
    input.setEncoding('utf8')
    input.on('data', (chunk) => {
      buffer += chunk
      let newline
      while ((newline = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, newline).trim()
        buffer = buffer.slice(newline + 1)
        if (line) this.receive(line)
      }
    })
    input.on('end', async () => this.fail(ended ? await ended() : 'the bridge closed the connection'))
    input.on('error', (error) => this.fail(`the bridge connection failed: ${error.message}`))
  }

  /** Run the MCP handshake. Resolves with the server's identity. */
  async initialize(clientInfo, timeoutMs = 15_000) {
    const result = await this.request('initialize', {
      protocolVersion: PROTOCOL_VERSION,
      capabilities: {},
      clientInfo,
    }, { timeoutMs })
    this.serverInfo = result.serverInfo ?? null
    this.notify('notifications/initialized', {})
    return result
  }

  /**
   * One tools/call. Resolves with the raw MCP CallToolResult, isError included.
   * @param {string} name - tool name.
   * @param {object} args - tool arguments.
   * @param {{signal?: AbortSignal, timeoutMs: number}} options - the caller's deadline.
   */
  callTool(name, args, options) {
    return this.request('tools/call', { name, arguments: args }, options)
  }

  /** resources/read for one URI. */
  readResource(uri, options) {
    return this.request('resources/read', { uri }, options)
  }

  request(method, params, { signal, timeoutMs } = {}) {
    if (this.closed) return Promise.reject(new LinkError('NOT_CONNECTED', this.closed))
    const id = this.nextId++
    return new Promise((resolve, reject) => {
      const settle = (fn, value) => {
        clearTimeout(timer)
        signal?.removeEventListener('abort', onAbort)
        this.pending.delete(id)
        fn(value)
      }
      const onAbort = () => {
        this.notify('notifications/cancelled', { requestId: id, reason: 'cancelled by dsh' })
        settle(reject, new LinkError('CANCELLED', `${method} was cancelled`))
      }
      const timer = timeoutMs
        ? setTimeout(() => {
          this.notify('notifications/cancelled', { requestId: id, reason: 'deadline' })
          settle(reject, new LinkError('TIMEOUT', `the bridge did not answer ${method} within ${Math.round(timeoutMs / 1000)} s`))
        }, timeoutMs)
        : undefined
      if (signal?.aborted) return onAbort()
      signal?.addEventListener('abort', onAbort, { once: true })
      this.pending.set(id, {
        resolve: (value) => settle(resolve, value),
        reject: (error) => settle(reject, error),
      })
      this.send({ jsonrpc: '2.0', id, method, params })
    })
  }

  notify(method, params) {
    if (!this.closed) this.send({ jsonrpc: '2.0', method, params })
  }

  send(message) {
    try {
      this.write(`${JSON.stringify(message)}\n`)
    } catch (error) {
      this.fail(`could not write to the bridge: ${error.message}`)
    }
  }

  receive(line) {
    let message
    try {
      message = JSON.parse(line)
    } catch {
      return // not ours: a stray diagnostic line
    }
    const waiter = this.pending.get(message.id)
    if (!waiter) return
    if (message.error) waiter.reject(new LinkError('INTERNAL', `bridge error ${message.error.code}: ${message.error.message}`))
    else waiter.resolve(message.result)
  }

  /** End the client: every in-flight call rejects with the reason. Idempotent. */
  fail(reason) {
    if (this.closed) return
    this.closed = reason
    for (const waiter of [...this.pending.values()]) waiter.reject(new LinkError('NOT_CONNECTED', reason))
    this.pending.clear()
    this.closeStream(reason)
  }
}
