/**
 * The front pages may not say what the code does not do. Every number, name and
 * version the READMEs and llms.txt publish is checked against its source here, so
 * a stale page turns CI red instead of misleading someone.
 */
import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import semver from 'semver'
import { describe, expect, it } from 'vitest'
import { contract } from '../lib/tools.js'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..')
const read = (p) => readFileSync(join(ROOT, p), 'utf8')
const pkg = JSON.parse(read('package.json'))
const pages = { 'README.md': read('README.md'), 'README.zh.md': read('README.zh.md') }
const names = contract.tools.map((t) => t.name)

describe('claims', () => {
  it('the tool count on every page is the contract\'s', () => {
    for (const [page, text] of Object.entries(pages)) {
      const counts = [...text.matchAll(/(\d+) (?:tools|个工具)/g)].map((m) => Number(m[1]))
      expect(counts.length, page).toBeGreaterThan(0)
      for (const n of counts) expect(n, page).toBe(names.length)
    }
  })

  it('every page\'s tool table lists exactly the contract\'s tools, in order', () => {
    for (const [page, text] of Object.entries(pages)) {
      const rows = [...text.matchAll(/^\| `(sw_[a-z_]+)` \|/gm)].map((m) => m[1])
      expect(rows, page).toEqual(names)
    }
    const llms = [...read('llms.txt').matchAll(/^- (sw_[a-z_]+)\(/gm)].map((m) => m[1])
    expect(llms).toEqual(names)
  })

  it('llms.txt lists exactly the contract\'s error codes', () => {
    const line = /Codes: ([A-Z_, ]+)\./.exec(read('llms.txt'))[1]
    expect(line.split(', ').sort()).toEqual(Object.keys(contract.errors).sort())
  })

  it('the member count on the pages is the member table\'s', () => {
    const rows = read('bridge/compat/members.tsv').split('\n').filter((l) => l && !l.startsWith('#')).length
    for (const [page, text] of Object.entries(pages)) {
      const claimed = [...text.matchAll(/(\d+) (?:members|个成员)/g)].map((m) => Number(m[1]))
      expect(claimed.length, page).toBeGreaterThan(0)
      for (const n of claimed) expect(n, page).toBe(rows)
    }
  })

  it('the unit test count on the pages is the number of tests', () => {
    const tests = readdirSync(join(ROOT, 'tests')).filter((f) => f.endsWith('.spec.mjs'))
      .map((f) => [...read(join('tests', f)).matchAll(/^\s+it\(/gm)].length).reduce((a, b) => a + b, 0)
    for (const [page, text] of Object.entries(pages)) {
      const claimed = /\| (?:Unit tests|单元测试)[^|]*\| (\d+)/.exec(text)
      expect(claimed, page).not.toBeNull()
      expect(Number(claimed[1]), page).toBe(tests)
    }
  })

  it('the dsh version the badge says was verified is the one the tests pin', () => {
    const pinned = pkg.devDependencies['@deepseek-ai/dsh-mcp-client']
    for (const text of Object.values(pages)) expect(text).toContain(`dsh-${pinned.replace(/-/g, '--')}%20verified`)
  })

  it('the install gate admits the dsh lines the pages promise', () => {
    for (const version of ['0.2.0-rc.2', '0.2.1-rc.2']) {
      expect(semver.satisfies(version, pkg.engines.dsh, { includePrerelease: true }), version).toBe(true)
      for (const [name, range] of Object.entries(pkg.peerDependencies)) {
        if (name.startsWith('@deepseek-ai/dsh')) expect(semver.satisfies(version, range, { includePrerelease: true }), `${name} ${version}`).toBe(true)
      }
    }
    expect(pkg.dependencies ?? {}).toEqual({}) // a host package in dependencies would shadow the host's copy
  })

  it('the bridge and the plugin carry one version', () => {
    expect(/<Version>([^<]+)<\/Version>/.exec(read('bridge/SwBridge.csproj'))[1]).toBe(pkg.version)
    expect(/<assemblyIdentity version="([^"]+)"/.exec(read('bridge/app.manifest'))[1]).toBe(`${pkg.version}.0`)
    expect(read('CHANGELOG.md')).toContain(`## ${pkg.version} `)
    expect(read('CHANGELOG.md')).toContain(read('bridge/swbridge.exe.sha256').trim())
  })

  it('the package ships the bridge, the contract and the patch', () => {
    for (const f of ['bin/swbridge.exe', 'protocol', 'lib', 'cordis.patch.yml']) expect(pkg.files).toContain(f)
    expect(read('cordis.patch.yml')).toContain(`name: '${pkg.name}'`)
  })

  it('every relative link on the pages resolves', () => {
    for (const [page, text] of Object.entries(pages)) {
      for (const [, target] of text.matchAll(/\]\(((?!https?:|#|mailto:)[^)\s]+)\)/g)) {
        expect(existsSync(join(ROOT, target.split('#')[0])), `${page} -> ${target}`).toBe(true)
      }
    }
  })
})
