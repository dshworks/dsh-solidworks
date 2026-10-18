/**
 * The contract between the halves, checked from both sides.
 *
 * protocol/tools.json is read by the C# bridge and by this plugin. These tests
 * read the C# sources as text, so a tool added on one side only, an error code
 * thrown but never documented, or a SOLIDWORKS member that 2016 does not have
 * turns CI red on Linux, without Windows or SOLIDWORKS.
 */
import { execFileSync } from 'node:child_process'
import { readFileSync, readdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { assertObjectJsonSchema } from '@deepseek-ai/dsh-tools'
import { describe, expect, it } from 'vitest'
import { contract } from '../lib/tools.js'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..')
const read = (p) => readFileSync(join(ROOT, p), 'utf8')
function sources(dir, ext) {
  return readdirSync(join(ROOT, dir), { recursive: true })
    .filter((f) => f.endsWith(ext))
    .map((f) => read(join(dir, f)))
    .join('\n')
}
const csharp = sources('bridge/src', '.cs')
const js = sources('lib', '.js')

describe('protocol/tools.json', () => {
  it('every input schema is one dsh accepts', () => {
    for (const tool of contract.tools) expect(() => assertObjectJsonSchema(tool.input), tool.name).not.toThrow()
  })

  it('names are unique sw_* names within dsh limits; budgets and write flags are set', () => {
    const names = contract.tools.map((t) => t.name)
    expect(new Set(names).size).toBe(names.length)
    for (const t of contract.tools) {
      expect(t.name).toMatch(/^sw_[a-z_]{2,40}$/)
      expect(t.budget_s).toBeGreaterThan(0)
      expect(typeof t.write).toBe('boolean')
      expect(t.description.length).toBeGreaterThan(60)
      expect(Object.keys(t).filter((k) => !['name', 'write', 'budget_s', 'description', 'input', 'fetch'].includes(k)), t.name).toEqual([])
      if ('fetch' in t) expect(t.fetch, t.name).toBe(true)
    }
    expect(contract.tools.filter((t) => t.fetch).map((t) => t.name)).toEqual(['sw_bom', 'sw_export'])
  })

  it('is in its house layout, so a diff shows the change and not a reformat', () => {
    expect(execFileSync(process.execPath, [join(ROOT, 'scripts', 'format-contract.mjs'), '--check'], { encoding: 'utf8' })).toBe('')
  })

  it('the bridge has a handler for exactly the tools in the contract', () => {
    const handlers = [...read('bridge/src/Mcp/ToolTable.cs').matchAll(/\["(sw_[a-z_]+)"\] =/g)].map((m) => m[1])
    expect(handlers.sort()).toEqual(contract.tools.map((t) => t.name).sort())
  })

  it('every error code either side raises is documented, and every documented code is raised', () => {
    const raised = new Set([
      ...[...csharp.matchAll(/(?:BridgeException|Failure)\("([A-Z_]+)"/g)].map((m) => m[1]),
      ...[...js.matchAll(/(?:failure|LinkError)\('([A-Z_]+)'/g)].map((m) => m[1]),
      ...[...js.matchAll(/\?\? '([A-Z_]+)'/g)].map((m) => m[1]),
    ])
    const documented = new Set(Object.keys(contract.errors))
    expect([...raised].filter((c) => !documented.has(c))).toEqual([])
    expect([...documented].filter((c) => !raised.has(c))).toEqual([])
  })
})

/**
 * The member names swbridge passes to Dispatch.Call/Get/Set/SetRef/CallRef: the
 * string literal after the first top-level comma. A regex cannot do this: in
 * CallRef(Get(doc, "Extension"), "SaveAs", ...) the member is the second literal.
 * A call whose member is not a literal (a variable, a ?: choice) is returned in
 * `unnamed`: this check could not see that member, so the bridge may not have one.
 */
function membersCalled(text) {
  const names = new Set()
  const unnamed = []
  for (const m of text.matchAll(/\b(?:Call|Get|Set|SetRef|CallRef)\(/g)) {
    if (text[m.index - 1] === '.') continue // table.Call(...), not Dispatch
    let i = m.index + m[0].length
    if (/^(?:object|Bridge)\s/.test(text.slice(i))) continue // the definitions themselves
    let depth = 0
    for (; i < text.length; i++) {
      const c = text[i]
      if (c === '"') { i++; while (text[i] !== '"') i += text[i] === '\\' ? 2 : 1; continue }
      if (c === '(') depth++
      else if (c === ')') { if (depth === 0) break; depth-- }
      else if (c === ',' && depth === 0) break
    }
    const literal = /^,\s*"([A-Za-z0-9_]+)"/.exec(text.slice(i))
    if (literal) names.add(literal[1])
    else if (text[i] === ',') unnamed.push(text.slice(m.index, m.index + 80).split('\n')[0])
  }
  return { names, unnamed }
}

describe('SOLIDWORKS 2016 compatibility', () => {
  const rows = (text) => text.split('\n').filter((l) => l && !l.startsWith('#')).map((l) => l.split('\t'))
  const table = rows(read('bridge/compat/members.tsv'))
  const sw2016 = new Set(rows(read('bridge/compat/sw2016.tsv')).map(([i, m]) => `${i}.${m}`))
  const { names: called, unnamed } = membersCalled(csharp)

  it('every member the bridge calls is in the member table with its interface', () => {
    const listed = new Set(table.map(([, m]) => m))
    expect(called.size).toBeGreaterThan(170)
    expect([...called].filter((m) => !listed.has(m))).toEqual([])
  })

  it('every call names its member as a literal, so the check above sees all of them', () => {
    expect(unnamed).toEqual([])
  })

  it('every member in the table exists on that interface in SOLIDWORKS 2016 SP1', () => {
    expect(table.map(([i, m]) => `${i}.${m}`).filter((r) => !sw2016.has(r))).toEqual([])
  })

  it('the 2016 list can tell: members added after 2016 are absent from it', () => {
    for (const later of ['IModelDocExtension.SaveAs2', 'IModelDocExtension.SaveAs3', 'ICustomPropertyManager.Get6',
      'IModelDocExtension.CreateMassProperty2', 'IEquationMgr.GlobalVariable']) expect(sw2016.has(later), later).toBe(false)
    expect(sw2016.has('IModelDocExtension.SaveAs')).toBe(true)
  })

  it('the bridge references no SOLIDWORKS interop assembly', () => {
    expect(read('bridge/SwBridge.csproj')).not.toMatch(/SolidWorks\.Interop/)
    expect(csharp).not.toMatch(/using SolidWorks\./)
  })
})
