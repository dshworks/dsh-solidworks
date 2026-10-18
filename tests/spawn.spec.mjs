import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { SpawnedBridge, bridgeFlags } from '../lib/spawn.js'
import { quiet } from './fake-bridge.mjs'

const HERE = dirname(fileURLToPath(import.meta.url))

/** dsh's subprocess seam, with real pipes: a stdio child speaking MCP. */
function subprocess(calls) {
  return {
    spawn(spec) {
      calls.push(spec)
      const child = spawn(process.execPath, [join(HERE, 'fake-stdio-bridge.mjs'), ...spec.argv.slice(1)], { stdio: ['pipe', 'pipe', 'pipe'] })
      let err = ''
      child.stderr.on('data', (c) => { err += c })
      return {
        stdin: child.stdin, stdout: child.stdout, stderr: undefined, control: undefined,
        done: new Promise((r) => child.on('close', (exitCode, signal) => r({ exitCode, signal }))),
        collected: { stderr: { readFrom: () => ({ text: err, nextOffset: err.length, lossy: false }) } },
        terminate: () => child.kill(),
        waitForExit: async () => true,
      }
    },
  }
}

describe('spawn mode', () => {
  it('maps the bridge config to swbridge flags', () => {
    expect(bridgeFlags({ pid: 12, out: 'D:\\out', allowWrite: ['E:\\a', 'E:\\b'], allowSave: true }))
      .toEqual(['--pid', '12', '--out', 'D:\\out', '--allow-write', 'E:\\a', '--allow-write', 'E:\\b', '--allow-save'])
    expect(bridgeFlags({ pid: 0, out: '', allowWrite: [], allowSave: false })).toEqual([])
  })

  it('refuses off Windows with a sentence that says what to do', async () => {
    const b = new SpawnedBridge({ subprocess: subprocess([]), exe: 'swbridge.exe', flags: [], clientInfo: {}, logger: quiet, platform: 'linux' })
    await expect(b.client()).rejects.toMatchObject({ code: 'NOT_CONNECTED', message: expect.stringContaining('link: listen') })
  })

  it('starts the bridge lazily, once, and restarts it after it exits', async () => {
    const calls = []
    const b = new SpawnedBridge({ subprocess: subprocess(calls), exe: process.execPath, flags: ['--pid', '7'], clientInfo: { name: 't', version: '0' }, logger: quiet, platform: 'win32' })
    expect(calls).toHaveLength(0)
    const [one, two] = await Promise.all([b.client(), b.client()])
    expect(one).toBe(two)
    expect(calls).toHaveLength(1)
    expect(calls[0].argv.slice(1)).toEqual(['mcp', '--pid', '7'])
    expect(calls[0].stdio).toMatchObject({ stdin: 'pipe', stdout: 'pipe' })
    const result = await one.callTool('sw_status', {}, { timeoutMs: 2000 })
    expect(result.structuredContent.argv).toEqual(['mcp', '--pid', '7'])
    await one.callTool('exit', {}, { timeoutMs: 2000 }).catch(() => {})
    for (let i = 0; i < 100 && !one.closed; i++) await new Promise((r) => setTimeout(r, 10))
    expect(one.closed).toContain('exited')
    const three = await b.client()
    expect(three).not.toBe(one)
    expect(calls).toHaveLength(2)
    b.close()
  })
})
