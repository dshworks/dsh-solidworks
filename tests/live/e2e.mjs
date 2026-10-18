#!/usr/bin/env node
/**
 * Live end-to-end run against a real SOLIDWORKS, through the real link.
 *
 * This listens with the plugin's own LinkServer, waits for a swbridge.exe to
 * dial in, and drives the tools the way dsh does (tools/call over MCP),
 * checking each answer against something the tool did not say about itself:
 * a volume ratio after an edit, file magic bytes, a sha256 recomputed on this
 * side, an error code on a path the bridge must refuse.
 *
 *   SW_LINK_TOKEN=... SW_SAMPLES='C:\dshsw\samples' node tests/live/e2e.mjs
 *
 * Then, on the Windows side: swbridge connect <this-host>:47321 <token> --out C:\dshsw\out
 *
 * It only opens copies of the stock SOLIDWORKS samples, read-only, and closes
 * what it opened. It needs a SOLIDWORKS 2016 or later and costs nothing.
 */
import { createHash } from 'node:crypto'
import { mkdtempSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { LinkServer } from '../../lib/link.js'
import { fetchExport } from '../../lib/tools.js'

const token = process.env.SW_LINK_TOKEN
const samples = process.env.SW_SAMPLES ?? 'C:\\dshsw\\samples'
const port = Number(process.env.SW_LINK_PORT ?? 47321)
if (!token) throw new Error('set SW_LINK_TOKEN')

const report = { checks: [], calls: [] }
let failed = 0
const check = (name, ok, detail = '') => {
  report.checks.push({ name, ok, detail })
  if (!ok) failed++
  console.log(`${ok ? 'ok  ' : 'FAIL'} ${name}${detail ? ` -- ${detail}` : ''}`)
}

const server = new LinkServer({ host: '0.0.0.0', port, token, clientInfo: { name: 'e2e', version: '0' }, logger: { info: console.log, warn: console.log } })
await server.listen()
console.log(`listening on ${port}; waiting for a bridge`)
const deadline = Date.now() + 300_000
while (!server.client || !server.client.serverInfo) {
  if (Date.now() > deadline) throw new Error('no bridge connected within 300 s')
  await new Promise((r) => setTimeout(r, 500))
}
const client = server.client
console.log(`bridge: ${JSON.stringify(server.live.bridge)}`)

async function call(name, args = {}, budget = 900_000) {
  const t = Date.now()
  const result = await client.callTool(name, args, { timeoutMs: budget })
  const ms = Date.now() - t
  const text = result.content?.find((c) => c.type === 'text')?.text ?? ''
  report.calls.push({ name, args, ms, isError: !!result.isError, text: text.slice(0, 300) })
  console.log(`  ${name} ${ms} ms${result.isError ? ` ERROR ${text}` : ''}`)
  return result
}
const facts = (r) => r.structuredContent ?? {}
const errorCode = (r) => r.isError ? (r.content?.[0]?.text ?? '').split(':')[0] : null

// --- session -----------------------------------------------------------------
const status = facts(await call('sw_status'))
const debris = (status.documents ?? []).filter((d) => d.path.toLowerCase().startsWith(samples.toLowerCase()))
if (debris.length > 0) throw new Error(`sample copies are already open in SOLIDWORKS (a previous run?): ${debris.map((d) => d.path).join(', ')}`)
// The SOLIDWORKS may be someone's working session: remember what they had, and give it back.
const theirs = (status.documents ?? []).map((d) => `${d.path}|dirty=${d.dirty}`).sort()
const theirActive = (status.documents ?? []).find((d) => d.active)?.path
const release = status.solidworks?.release ?? ''
check('status names a release', /^SOLIDWORKS 20\d\d SP\d+/.test(release), release)
report.release = release

// --- a part: open, read, measure ---------------------------------------------
const part = `${samples}\\box.sldprt`
const opened = await call('sw_open', { path: part })
check('open part read-only', !opened.isError && facts(opened).read_only === true && facts(opened).type === 'part', JSON.stringify(facts(opened)).slice(0, 200))
const inspect = facts(await call('sw_inspect'))
check('inspect has configurations', Array.isArray(inspect.configurations) && inspect.configurations.length > 0, JSON.stringify(inspect.configurations))
check('inspect has a bounding box', Array.isArray(inspect.box_mm?.size) && inspect.box_mm.size.every((v) => v > 0), JSON.stringify(inspect.box_mm))
const features = facts(await call('sw_features'))
check('features listed', features.total > 0 && features.features.length > 0, `${features.total} features, ${features.with_errors} with errors`)
const params = facts(await call('sw_parameters'))
check('dimensions listed', params.dimensions?.length > 0, params.dimensions?.slice(0, 4).map((d) => `${d.name}=${d.value}${d.unit}`).join(' '))
const mass0 = facts(await call('sw_mass_properties'))
check('mass properties', mass0.volume_mm3 > 0 && mass0.mass_kg > 0, `V=${mass0.volume_mm3} mm3 m=${mass0.mass_kg} kg`)

// Edit: scale the extrusion depth, rebuild, and check the volume moved by the
// same ratio. The ratio is an oracle the tool cannot fake by echoing input.
const depth = params.dimensions.find((d) => /@(Boss-)?Extrude/i.test(d.name) && d.unit === 'mm' && !d.driven)
if (depth) {
  const edit = facts(await call('sw_edit', { dimensions: { [depth.name]: depth.value * 1.5 } }))
  const change = edit.changes?.[0]
  check('edit applied and read back', change?.ok === true && Math.abs(change.after - depth.value * 1.5) < 1e-6, JSON.stringify(change))
  check('rebuild ok after edit', edit.rebuild?.ok === true, JSON.stringify(edit.rebuild))
  const mass1 = facts(await call('sw_mass_properties'))
  report.volumes = [mass0.volume_mm3, mass1.volume_mm3]
  check('volume follows the edit', mass1.volume_mm3 > mass0.volume_mm3, `${mass0.volume_mm3} -> ${mass1.volume_mm3} mm3`)
  // The stock box.sldprt is a 100 mm cube shelled at 5 mm with one face open:
  // 100^3 - 90^2 * 95 = 230,500 mm3. At 150 mm deep it must be 100*100*150 - 90^2 * 145.
  if (Math.abs(mass0.volume_mm3 - 230_500) < 0.5) {
    check('volume is exactly what the geometry says', Math.abs(mass1.volume_mm3 - (100 * 100 * 150 - 90 * 90 * 145)) < 0.5, `${mass1.volume_mm3} vs 325500`)
  }
} else {
  check('found an extrusion depth to edit', false, params.dimensions.map((d) => d.name).join(' '))
}

// --- picture ------------------------------------------------------------------
const view = await call('sw_view', { view: 'isometric', width: 640, height: 480 })
const image = view.content?.find((c) => c.type === 'image')
const png = image ? Buffer.from(image.data, 'base64') : Buffer.alloc(0)
check('view is a PNG', png.subarray(0, 8).equals(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])), `${png.length} bytes`)
if (png.length > 24) check('view has the asked size', png.readUInt32BE(16) === 640 && png.readUInt32BE(20) === 480, `${png.readUInt32BE(16)}x${png.readUInt32BE(20)}`)

// --- export, fetched to this side and hashed here -----------------------------
const stamp = Date.now()
const step = await call('sw_export', { path: `e2e-${stamp}.step` })
check('export step', !step.isError && facts(step).bytes > 0, JSON.stringify(facts(step)).slice(0, 200))
if (!step.isError) {
  const local = facts(await fetchExport(client, step, mkdtempSync(join(tmpdir(), 'swe2e-')), 'out'))
  const bytes = local.local_copy ? readFileSync(local.local_copy) : Buffer.alloc(0)
  check('fetched copy hashes the same', createHash('sha256').update(bytes).digest('hex') === facts(step).sha256, local.local_copy ?? local.local_copy_error)
  check('fetched copy is STEP', bytes.subarray(0, 13).toString('latin1') === 'ISO-10303-21;', bytes.subarray(0, 20).toString('latin1'))
}
const again = await call('sw_export', { path: `e2e-${stamp}.step` })
check('export refuses to overwrite', errorCode(again) === 'FILE_EXISTS', errorCode(again))

// --- refusals -----------------------------------------------------------------
check('export outside allowed folders is refused', errorCode(await call('sw_export', { path: 'C:\\Windows\\e2e.step' })) === 'PATH_DENIED')
check('save is off by default', errorCode(await call('sw_save')) === 'SAVE_DISABLED')
check('missing file', errorCode(await call('sw_open', { path: `${samples}\\no-such-part.sldprt` })) === 'FILE_NOT_FOUND')
check('components needs an assembly', errorCode(await call('sw_components')) === 'WRONG_DOC_TYPE')
check('unknown document', errorCode(await call('sw_inspect', { doc: 'not-open.sldprt' })) === 'DOC_NOT_OPEN')
check('dirty document is not discarded silently', errorCode(await call('sw_close')) === 'DIRTY')
const closed = await call('sw_close', { discard: true })
check('close with discard', !closed.isError && facts(closed).discarded_changes === true, JSON.stringify(facts(closed)))

// --- an assembly ----------------------------------------------------------------
const asm = await call('sw_open', { path: `${samples}\\bolt-assembly.sldasm` })
check('open assembly', !asm.isError && facts(asm).type === 'assembly', JSON.stringify(facts(asm)).slice(0, 200))
const comps = facts(await call('sw_components', { depth: 2 }))
check('components listed', comps.components?.length > 0, comps.components?.map((c) => `${c.name}:${c.state}`).join(' '))
const rebuild = facts(await call('sw_rebuild'))
check('assembly rebuild reports', typeof rebuild.ok === 'boolean', JSON.stringify(rebuild).slice(0, 200))
// A rebuild marks the assembly modified, so closing it means discarding that.
check('close assembly', !(await call('sw_close', { discard: true })).isError)

// --- a drawing to PDF -----------------------------------------------------------
const drw = await call('sw_open', { path: `${samples}\\box.slddrw` })
check('open drawing', !drw.isError && facts(drw).type === 'drawing', JSON.stringify(facts(drw)).slice(0, 200))
if (!drw.isError) {
  const pdf = await call('sw_export', { path: `e2e-${stamp}.pdf` })
  check('drawing to PDF', !pdf.isError && facts(pdf).bytes > 0, JSON.stringify(facts(pdf)).slice(0, 200))
  if (!pdf.isError) {
    const local = facts(await fetchExport(client, pdf, mkdtempSync(join(tmpdir(), 'swe2e-')), 'out'))
    const head = local.local_copy ? readFileSync(local.local_copy).subarray(0, 5).toString('latin1') : ''
    check('PDF magic', head === '%PDF-', head)
  }
  await call('sw_close', { discard: true })
}

if (theirActive) await call('sw_open', { path: theirActive }) // already open: this only re-activates it
const after = facts(await call('sw_status'))
check('nothing of ours left open', !(after.documents ?? []).some((d) => d.opened_by_bridge), JSON.stringify((after.documents ?? []).filter((d) => d.opened_by_bridge)))
const theirsAfter = (after.documents ?? []).map((d) => `${d.path}|dirty=${d.dirty}`).sort()
check('their documents are as they were', JSON.stringify(theirsAfter) === JSON.stringify(theirs), `${theirs.length} before, ${theirsAfter.length} after`)
check('their active document is active again', (after.documents ?? []).find((d) => d.active)?.path === theirActive, theirActive ?? 'none')

report.failed = failed
console.log(JSON.stringify({ release, failed, calls: report.calls.map((c) => `${c.name}:${c.ms}`).join(' ') }))
await server.close()
process.exit(failed === 0 ? 0 : 1)
