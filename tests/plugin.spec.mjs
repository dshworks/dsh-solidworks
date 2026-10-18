/**
 * The plugin as dsh mounts it: tools built with dsh's own createMcpToolDefinition,
 * the system-prompt section, the pairing kit, and a bridge that pairs from that
 * kit alone, exactly as a user's VM would.
 */
import { createHash } from 'node:crypto'
import { existsSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { Config, apply } from '../lib/index.js'
import { fetchExport } from '../lib/tools.js'
import { fakeBridge, ok, quiet, wait } from './fake-bridge.mjs'

const home = mkdtempSync(join(tmpdir(), 'swplugin-'))
const workspace = mkdtempSync(join(tmpdir(), 'swws-'))
let tools
let sections
let disposers
let bridge

function fakeCtx() {
  tools = new Map()
  sections = []
  disposers = []
  const ctx = {
    logger: quiet,
    get: () => undefined,
    effect: (fn) => { disposers.push(fn()) },
    inject: (_names, cb) => cb(ctx),
    tools: { register: (def) => { tools.set(def.name, def); return () => tools.delete(def.name) } },
    systemPrompt: { section: (s) => { sections.push(s); return () => {} }, getSectionOrder: () => 3100 },
  }
  return ctx
}

const exec = (signal = new AbortController().signal) => ({ signal, agent: { session: { header: { cwd: workspace } } } })
const run = (name, args = {}) => tools.get(name).execute(args, exec())

beforeAll(async () => {
  process.env.DSH_HOME = home
  await apply(fakeCtx(), Config({ link: 'listen', listen: { host: '127.0.0.1', port: 0 }, startupWaitMs: 0 }))
})
afterAll(() => {
  bridge?.close()
  for (const d of disposers) d?.()
  delete process.env.DSH_HOME
})

describe('mounted in listen mode', () => {
  it('registers the 13 contract tools with dsh deadlines past the bridge budgets', () => {
    expect([...tools.keys()]).toEqual(['sw_status', 'sw_open', 'sw_inspect', 'sw_features', 'sw_parameters', 'sw_components',
      'sw_mass_properties', 'sw_edit', 'sw_rebuild', 'sw_export', 'sw_save', 'sw_close', 'sw_view'])
    expect(tools.get('sw_open').timeoutMs).toBe((900 + 30) * 1000)
  })

  it('adds one static guide to the system prompt', () => {
    expect(sections).toHaveLength(1)
    expect(sections[0]).toMatchObject({ name: 'dsh-solidworks', interpolate: false })
    expect(typeof sections[0].text).toBe('string')
    expect(sections[0].text).toContain('mm, degrees and kg')
  })

  it('says where the pairing kit is when no bridge is connected, and never says the token', async () => {
    const token = JSON.parse(readFileSync(join(home, 'dsh-solidworks', 'pairing.json'), 'utf8')).token
    const error = await run('sw_status').then(() => null, (e) => e)
    expect(error.message).toMatch(/^NOT_CONNECTED: /)
    expect(error.message).toContain('pair.cmd')
    expect(error.message).not.toContain(token)
  })

  it('pairs a bridge from nothing but pair.cmd, and the tools reach it', async () => {
    const script = readFileSync(join(home, 'dsh-solidworks', 'windows', 'pair.cmd'), 'utf8')
    const [, addresses, token] = /connect (\S+) (\S+) --install/.exec(script)
    const port = Number(addresses.split(',')[0].split(':')[1])
    const exported = Buffer.from('ISO-10303-21;\nHEADER;\n')
    const sha = createHash('sha256').update(exported).digest('hex')
    bridge = fakeBridge({
      port, token, tools: {
        sw_status: () => ok({ solidworks: { release: 'SOLIDWORKS 2019 SP0' } }),
        sw_export: () => ok({ path: 'C:\\out\\bracket.step', bytes: exported.length, sha256: sha }),
        sw_save: () => ({ content: [{ type: 'text', text: 'SAVE_DISABLED: off' }], isError: true }),
        'resources/read': ({ uri }) => ({ contents: [{ uri, blob: exported.toString('base64') }] }),
      },
    })
    expect((await bridge.outcome).ok).toBe(true)
    for (let i = 0; i < 100; i++) {
      const value = await run('sw_status').catch(() => null)
      if (value) { expect(value.structuredContent.solidworks.release).toBe('SOLIDWORKS 2019 SP0'); break }
      await wait(10)
    }

    const value = await run('sw_export', { path: 'bracket.step' })
    expect(value.structuredContent.local_copy).toBe(join(workspace, 'solidworks', 'bracket.step'))
    expect(readFileSync(value.structuredContent.local_copy)).toEqual(exported)

    await expect(run('sw_save')).rejects.toThrow('SAVE_DISABLED: off')
  })
})

describe('fetching exports', () => {
  const sha = (b) => createHash('sha256').update(b).digest('hex')
  const live = (blob) => ({ readResource: async () => ({ contents: [{ blob: blob.toString('base64') }] }) })
  const exported = (bytes, path = 'C:\\out\\part.step') => ({ content: [], structuredContent: { path, bytes: bytes.length, sha256: sha(bytes) } })

  it('never overwrites a different file of the same name', async () => {
    const ws = mkdtempSync(join(tmpdir(), 'swfetch-'))
    const a = Buffer.from('first')
    const b = Buffer.from('second')
    const one = await fetchExport(live(a), exported(a), ws, 'solidworks')
    const two = await fetchExport(live(b), exported(b), ws, 'solidworks')
    const again = await fetchExport(live(a), exported(a), ws, 'solidworks')
    expect(one.structuredContent.local_copy).toBe(join(ws, 'solidworks', 'part.step'))
    expect(two.structuredContent.local_copy).toBe(join(ws, 'solidworks', 'part-2.step'))
    expect(again.structuredContent.local_copy).toBe(one.structuredContent.local_copy)
  })

  it('refuses a copy that does not match the hash the bridge reported', async () => {
    const ws = mkdtempSync(join(tmpdir(), 'swfetch-'))
    const result = await fetchExport(live(Buffer.from('tampered')), exported(Buffer.from('original')), ws, 'solidworks')
    expect(result.structuredContent.local_copy).toBeUndefined()
    expect(result.structuredContent.local_copy_error).toContain('does not match')
    expect(existsSync(join(ws, 'solidworks', 'part.step'))).toBe(false)
  })

  it('names the file by its Windows name even off Windows', async () => {
    const ws = mkdtempSync(join(tmpdir(), 'swfetch-'))
    writeFileSync(join(ws, 'placeholder'), '')
    const bytes = Buffer.from('x')
    const result = await fetchExport(live(bytes), exported(bytes, 'D:\\deep\\dir\\Gear Box.STEP'), ws, 'cad')
    expect(result.structuredContent.local_copy).toBe(join(ws, 'cad', 'Gear Box.STEP'))
  })
})
