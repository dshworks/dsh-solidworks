/**
 * Pairing: the token both ends prove, and the kit that carries it to Windows.
 *
 * The token never enters the model's context. A tool result is part of the
 * transcript; a token printed there would be in every saved session. Instead
 * the plugin writes a pairing kit — swbridge.exe and a pair.cmd that runs it
 * with the token and this machine's addresses — into its own folder under
 * DSH_HOME, and the "not connected" answer only says where the kit is.
 *
 * On a Mac, Parallels shares the home folder with Windows as \\Mac\Home, so a
 * VM user double-clicks the kit where it already is. Anywhere else, the user
 * copies the folder over once.
 */
import { createHash, randomBytes } from 'node:crypto'
import { chmodSync, copyFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { homedir, hostname, networkInterfaces } from 'node:os'
import { join, relative, resolve, sep } from 'node:path'

/**
 * The harness home by dsh's own rule (`resolveDshHome` in
 * @deepseek-ai/dsh-home-paths): a non-blank $DSH_HOME, else ~/.dsh.
 * @param {Record<string, string | undefined>} [env] - environment to read.
 */
export function dshHome(env = process.env) {
  const fromEnv = env.DSH_HOME
  const selected = fromEnv !== undefined && fromEnv.trim().length > 0 ? fromEnv : join(homedir(), '.dsh')
  if (selected === '~') return homedir()
  return resolve(selected.startsWith('~/') || selected.startsWith('~\\') ? join(homedir(), selected.slice(2)) : selected)
}

/** The plugin's own folder under the harness home. */
export function stateDir(env = process.env) {
  return join(dshHome(env), 'dsh-solidworks')
}

/**
 * The pairing token: the configured one, else the one kept in
 * `pairing.json`, else a new one written there (mode 0600). Kept across
 * restarts, so a paired bridge stays paired.
 * @param {string} configured - the `token` config value; empty means managed.
 * @param {string} dir - the plugin's state folder.
 */
export function loadToken(configured, dir) {
  if (configured) return configured
  const file = join(dir, 'pairing.json')
  if (existsSync(file)) {
    const saved = JSON.parse(readFileSync(file, 'utf8'))
    if (typeof saved.token === 'string' && saved.token.length >= 32) return saved.token
  }
  mkdirSync(dir, { recursive: true })
  const token = randomBytes(24).toString('base64url')
  writeFileSync(file, `${JSON.stringify({ token, created: new Date().toISOString() }, null, 2)}\n`, { mode: 0o600 })
  return token
}

/**
 * Addresses the Windows side can dial, best first. `advertise` wins when set;
 * a specific listen host is itself the answer; a wildcard lists every
 * non-loopback IPv4 address, host-only VM networks first, since that is where
 * a VM finds its host.
 * @param {object} options
 * @param {string} options.host - the listen host.
 * @param {number} options.port - the bound port.
 * @param {string[]} options.advertise - configured host[:port] entries.
 * @param {ReturnType<typeof networkInterfaces>} [options.interfaces] - for tests.
 * @returns {string[]} host:port entries.
 */
export function dialAddresses({ host, port, advertise, interfaces = networkInterfaces() }) {
  if (advertise.length > 0) return advertise.map((a) => (/:\d+$/.test(a) ? a : `${a}:${port}`))
  if (host !== '0.0.0.0' && host !== '::') return [`${host}:${port}`]
  const vm = /^(bridge1\d\d|vnic\d|vmnet\d|vEthernet|prl)/i
  const found = []
  for (const [name, entries] of Object.entries(interfaces)) {
    for (const entry of entries ?? []) {
      if (entry.family !== 'IPv4' && entry.family !== 4) continue
      if (entry.internal) continue
      found.push({ address: entry.address, rank: vm.test(name) ? 0 : 1 })
    }
  }
  found.sort((a, b) => a.rank - b.rank)
  const unique = [...new Set(found.map((f) => f.address))]
  return (unique.length > 0 ? unique : ['127.0.0.1']).map((a) => `${a}:${port}`)
}

/**
 * Write the kit: a copy of swbridge.exe and pair.cmd. Rewritten only when its
 * content changes, so an unchanged kit keeps its timestamps.
 * @param {object} options
 * @param {string} options.dir - the plugin's state folder.
 * @param {string} options.exe - the bundled swbridge.exe.
 * @param {string} options.token - the pairing token.
 * @param {string[]} options.addresses - from {@link dialAddresses}.
 * @returns {{folder: string, script: string, share: string | null}} where it is, and its \\Mac\Home path when there is one.
 */
export function writeKit({ dir, exe, token, addresses }) {
  const folder = join(dir, 'windows')
  mkdirSync(folder, { recursive: true })
  const target = join(folder, 'swbridge.exe')
  if (existsSync(exe) && (!existsSync(target) || digest(target) !== digest(exe))) copyFileSync(exe, target)
  const script = join(folder, 'pair.cmd')
  const body = [
    '@echo off',
    `rem dsh-solidworks pairing kit for ${hostname()}.`,
    'rem Run it on the Windows PC where SOLIDWORKS runs, signed in as the person who uses SOLIDWORKS.',
    'rem It installs the bridge for this user only (no administrator needed), connects it to dsh,',
    'rem and starts it at every sign-in. Remove it with: swbridge uninstall',
    `"%~dp0swbridge.exe" connect ${addresses.join(',')} ${token} --install`,
    'echo.',
    'pause',
    '',
  ].join('\r\n')
  if (!existsSync(script) || readFileSync(script, 'utf8') !== body) writeFileSync(script, body, { mode: 0o600 })
  chmodSync(script, 0o600)
  return { folder, script, share: macShare(script) }
}

/**
 * The \\Mac\Home\... path Parallels gives a VM for a file in the Mac user's
 * home, or null off macOS or outside the home.
 * @param {string} path - absolute path on this machine.
 */
export function macShare(path, platform = process.platform, home = homedir()) {
  if (platform !== 'darwin') return null
  const rel = relative(home, path)
  if (rel.startsWith('..') || rel === path) return null
  return `\\\\Mac\\Home\\${rel.split(sep).join('\\')}`
}

function digest(file) {
  return createHash('sha256').update(readFileSync(file)).digest('hex')
}
