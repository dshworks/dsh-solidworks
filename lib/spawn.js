/**
 * Spawn mode: dsh and SOLIDWORKS on the same Windows PC.
 *
 * The plugin starts the bundled swbridge.exe on the first SOLIDWORKS tool call
 * — not at boot, so a session that never touches CAD runs nothing — and talks
 * MCP over its stdio. dsh's own plugin subprocesses are not sandboxed, which
 * matters here: a restricted, low-integrity process cannot see the user's
 * SOLIDWORKS in the Running Object Table. dsh runs in the user's desktop
 * session, so the child does too.
 */
import { existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { LinkError, McpClient } from './mcp.js'

const HERE = dirname(fileURLToPath(import.meta.url))

/** The swbridge.exe shipped inside this package. */
export const BUNDLED_BRIDGE = join(HERE, '..', 'bin', 'swbridge.exe')

/**
 * Command-line flags for the bridge from the plugin's config. The bridge owns
 * the policy; the plugin only passes what the user configured.
 * @param {object} bridge - the `bridge` config section.
 * @returns {string[]} argv after `mcp`.
 */
export function bridgeFlags(bridge) {
  const argv = []
  if (bridge.pid) argv.push('--pid', String(bridge.pid))
  if (bridge.out) argv.push('--out', bridge.out)
  for (const dir of bridge.allowWrite) argv.push('--allow-write', dir)
  if (bridge.allowSave) argv.push('--allow-save')
  return argv
}

export class SpawnedBridge {
  /**
   * @param {object} options
   * @param {object} options.subprocess - dsh's subprocess service.
   * @param {string} options.exe - swbridge.exe path.
   * @param {string[]} options.flags - from {@link bridgeFlags}.
   * @param {object} options.clientInfo - MCP clientInfo.
   * @param {{info: Function, warn: Function}} options.logger - diagnostics.
   * @param {string} [options.platform] - for tests; the bridge only runs on win32.
   */
  constructor({ subprocess, exe, flags, clientInfo, logger, platform = process.platform }) {
    this.platform = platform
    this.subprocess = subprocess
    this.exe = exe
    this.flags = flags
    this.clientInfo = clientInfo
    this.logger = logger
    this.current = null
    this.starting = null
    this.lastExit = null
  }

  /** The running bridge's client, starting it when needed. */
  async client() {
    if (this.current && !this.current.client.closed) return this.current.client
    this.starting ??= this.start().finally(() => { this.starting = null })
    return (await this.starting).client
  }

  async start() {
    if (this.platform !== 'win32') {
      throw new LinkError('NOT_CONNECTED', 'spawn mode needs dsh on the Windows PC that runs SOLIDWORKS; set link: listen to reach another machine')
    }
    if (!existsSync(this.exe)) throw new LinkError('NOT_CONNECTED', `swbridge.exe is missing at ${this.exe}`)
    const handle = this.subprocess.spawn({
      argv: [this.exe, 'mcp', ...this.flags],
      cwd: dirname(this.exe),
      stdio: { stdin: 'pipe', stdout: 'pipe', stderr: { maxBytes: 65_536 } },
      graceMs: 3000,
    })
    const exited = handle.done.then((outcome) => {
      const tail = handle.collected.stderr?.readFrom(0).text.trim().split('\n').slice(-3).join(' | ') ?? ''
      this.lastExit = { ...outcome, tail, at: new Date() }
      return `swbridge.exe exited (code ${outcome.exitCode ?? outcome.signal})${tail ? `: ${tail}` : ''}`
    }, (error) => `swbridge.exe could not start: ${error.message}`)
    // stdout ends a moment before the exit is reported; wait briefly for the exit so the
    // reason carries the code and the bridge's last words instead of "closed".
    const client = new McpClient({
      input: handle.stdout,
      write: (line) => handle.stdin.write(line),
      close: () => handle.terminate(),
      ended: () => Promise.race([exited, new Promise((r) => setTimeout(() => r('swbridge.exe closed its output'), 3000))]),
    })
    exited.then((reason) => client.fail(reason))
    const current = { client, handle }
    await client.initialize(this.clientInfo)
    this.logger.info(`swbridge ${client.serverInfo?.version ?? '?'} started`)
    this.current = current
    return current
  }

  close() {
    this.current?.client.fail('dsh is shutting down')
    this.current?.handle.terminate()
    this.current = null
  }
}
