import { mkdtempSync, readFileSync, statSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { dialAddresses, dshHome, loadToken, macShare, writeKit } from '../lib/pairing.js'

const iface = (address, internal = false) => [{ family: 'IPv4', address, internal }]

describe('dial addresses', () => {
  it('uses advertise as given, adding the port where missing', () => {
    expect(dialAddresses({ host: '0.0.0.0', port: 47321, advertise: ['10.211.55.2', 'mac.local:9000'] }))
      .toEqual(['10.211.55.2:47321', 'mac.local:9000'])
  })
  it('a specific listen host is the only address', () => {
    expect(dialAddresses({ host: '192.168.1.5', port: 1, advertise: [] })).toEqual(['192.168.1.5:1'])
  })
  it('a wildcard lists VM host-only networks first, skips loopback, dedups', () => {
    const interfaces = { lo0: iface('127.0.0.1', true), en0: iface('192.168.1.5'), bridge100: iface('10.211.55.2'), vmnet8: iface('172.16.1.1'), en1: iface('192.168.1.5') }
    expect(dialAddresses({ host: '0.0.0.0', port: 7, advertise: [], interfaces })).toEqual(['10.211.55.2:7', '172.16.1.1:7', '192.168.1.5:7'])
  })
  it('falls back to loopback when nothing else exists', () => {
    expect(dialAddresses({ host: '0.0.0.0', port: 7, advertise: [], interfaces: { lo: iface('127.0.0.1', true) } })).toEqual(['127.0.0.1:7'])
  })
})

describe('the \\\\Mac\\Home share path', () => {
  it('maps a path under the Mac home to its Parallels share', () => {
    expect(macShare('/Users/ann/.dsh/dsh-solidworks/windows/pair.cmd', 'darwin', '/Users/ann')).toBe('\\\\Mac\\Home\\.dsh\\dsh-solidworks\\windows\\pair.cmd')
  })
  it('is null off macOS and outside the home', () => {
    expect(macShare('/home/ann/x', 'linux', '/home/ann')).toBeNull()
    expect(macShare('/opt/x', 'darwin', '/Users/ann')).toBeNull()
  })
})

describe('token and kit', () => {
  it('follows DSH_HOME like dsh does', () => {
    expect(dshHome({ DSH_HOME: '/tmp/x' })).toBe('/tmp/x')
    expect(dshHome({ DSH_HOME: '  ' })).toMatch(/\.dsh$/)
  })
  it('generates a token once, keeps it private, and reuses it', () => {
    const dir = mkdtempSync(join(tmpdir(), 'swpair-'))
    const a = loadToken('', dir)
    expect(a.length).toBeGreaterThanOrEqual(32)
    expect(statSync(join(dir, 'pairing.json')).mode & 0o777).toBe(0o600)
    expect(loadToken('', dir)).toBe(a)
    expect(loadToken('configured-wins', dir)).toBe('configured-wins')
  })
  it('writes pair.cmd with every address, the token and --install, in CRLF', () => {
    const dir = mkdtempSync(join(tmpdir(), 'swkit-'))
    const exe = join(dir, 'swbridge.exe')
    writeFileSync(exe, 'MZ fake exe')
    const kit = writeKit({ dir, exe, token: 'TOKEN123', addresses: ['10.211.55.2:47321', '192.168.1.5:47321'] })
    const script = readFileSync(kit.script, 'utf8')
    expect(script).toContain('"%~dp0swbridge.exe" connect 10.211.55.2:47321,192.168.1.5:47321 TOKEN123 --install')
    expect(script.split('\r\n').length).toBeGreaterThan(5)
    expect(readFileSync(join(kit.folder, 'swbridge.exe'), 'utf8')).toBe('MZ fake exe')
    expect(statSync(kit.script).mode & 0o777).toBe(0o600)
  })
})
