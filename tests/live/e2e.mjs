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
 * It only opens copies of the stock SOLIDWORKS samples, read-only, makes its own
 * new documents, and closes everything it opened or made. It needs a SOLIDWORKS
 * 2016 or later and costs nothing.
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
const hiddenBefore = status.loaded_without_window ?? 0
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

// --- design: parts, an assembly and a drawing made from nothing ---------------
// Every number checked here is computed from the geometry asked for, not read
// back from the tool that made it.
const near = (a, b, tol = 1e-3) => typeof a === 'number' && Math.abs(a - b) <= tol
const same3 = (a, b) => Array.isArray(a) && a.length === 3 && a.every((v, i) => near(v, b[i]))
const made = [] // titles of documents this run created, closed at the end
const fresh = async (type) => {
  const r = facts(await call('sw_new', { type }))
  if (r.title) made.push(r.title)
  return r
}

const newPart = await fresh('part')
check('new part is ours and has no file', newPart.type === 'part' && newPart.path === '' && newPart.opened_by_bridge === true, JSON.stringify(newPart))
// A 100 x 60 x 20 plate on the top plane, a 20 mm hole through it from its top face, 5 mm corner fillets.
const plate = facts(await call('sw_build', { steps: [
  { op: 'sketch', on: 'top', entities: [{ type: 'rectangle', corner: [-50, 0, -30], opposite: [50, 0, 30] }] },
  { op: 'extrude', depth: 20, name: 'Plate' },
  { op: 'sketch', face_at: [0, 20, 0], entities: [{ type: 'circle', center: [0, 20, 0], diameter: 20 }] },
  { op: 'extrude', cut: true, end: 'through_all', name: 'Hole' },
  { op: 'fillet', radius: 5, edges_at: [[50, 10, 30], [50, 10, -30], [-50, 10, 30], [-50, 10, -30]], name: 'Corners' },
] }))
const top = 6000 - Math.PI * 100 - 4 * 25 * (1 - Math.PI / 4) // the top face's area, mm2
check('build: every step made its feature', plate.ok === true && plate.steps?.map((s) => s.feature).join() === 'Sketch1,Plate,Sketch2,Hole,Corners', JSON.stringify(plate.steps?.map((s) => s.feature)))
check('build: volume is the geometry asked for', near(plate.model?.volume_mm3, top * 20), `${plate.model?.volume_mm3} vs ${top * 20}`)
check('build: box is the geometry asked for', same3(plate.model?.box_mm?.min, [-50, 0, -30]) && same3(plate.model?.box_mm?.max, [50, 20, 30]), JSON.stringify(plate.model?.box_mm))
check('build: features carry drivable dimensions', plate.steps?.[1]?.dimensions?.[0]?.name === 'D1@Plate' && plate.steps?.[4]?.dimensions?.[0]?.value === 5, JSON.stringify(plate.steps?.[1]?.dimensions))

const probe = facts(await call('sw_probe', { at: [[20, 20, 0], [20, 0, 0]] }))
const [topFace, bottomFace] = probe.entities ?? []
check('probe: the top face, by a point on it', topFace?.surface === 'plane' && same3(topFace?.normal, [0, 1, 0]) && near(topFace?.area_mm2, top), JSON.stringify(topFace))
check('probe: two parallel faces 20 mm apart', near(probe.between?.distance_mm, 20) && probe.between?.parallel === true && same3(bottomFace?.normal, [0, -1, 0]), JSON.stringify(probe.between))
const hole = facts(await call('sw_probe', { at: [[10, 10, 0]] })).entities?.[0]
check('probe: the hole is a 20 mm cylinder on the Y axis', hole?.surface === 'cylinder' && near(hole?.diameter_mm, 20) && near(Math.abs(hole?.axis?.[1] ?? 0), 1) && hole?.feature === 'Hole', JSON.stringify(hole))

const failing = facts(await call('sw_build', { steps: [
  { op: 'sketch', face_at: [30, 0, 0], entities: [{ type: 'circle', center: [30, 0, 0], diameter: 6 }] },
  { op: 'extrude', depth: 5, name: 'Peg' },
  { op: 'fillet', radius: 1, edges_at: [[400, 0, 0]] },
] }))
check('build: a failing step undoes the steps before it', failing.ok === false && failing.failed?.step === 3 && failing.rolled_back?.includes('Peg') && near(failing.model?.volume_mm3, top * 20), JSON.stringify({ failed: failing.failed, rolled_back: failing.rolled_back, v: failing.model?.volume_mm3 }))
const treeBefore = facts(await call('sw_features')).features?.map((f) => f.name).join() ?? ''
const halfDrawn = facts(await call('sw_build', { steps: [
  { op: 'sketch', face_at: [30, 20, 0], entities: [{ type: 'circle', center: [30, 20, 0], diameter: 6 }, { type: 'circle', center: [-30, 20, 0], diameter: 0 }] },
] }))
const treeAfter = facts(await call('sw_features')).features?.map((f) => f.name).join() ?? ''
check('build: a sketch that fails halfway is not left behind', halfDrawn.failed?.step === 1 && halfDrawn.failed?.partial_sketch && halfDrawn.rolled_back?.includes(halfDrawn.failed.partial_sketch) && treeAfter === treeBefore,
  JSON.stringify({ failed: halfDrawn.failed, rolled_back: halfDrawn.rolled_back, same: treeAfter === treeBefore }))
const planes = facts(await call('sw_features')).features?.filter((f) => f.type === 'RefPlane').map((f) => f.name) ?? []
const refused = facts(await call('sw_build', { steps: [{ op: 'delete', features: [planes[0]] }] }))
check('build: deletes only what it built', refused.ok === false && refused.failed?.code === 'BAD_REQUEST' && /not made by sw_build/.test(refused.failed?.error ?? ''), refused.failed?.error)

const study = facts(await call('sw_study', { variants: [{ dimensions: { 'D1@Plate': 10 } }, { label: 'thick', dimensions: { 'D1@Plate': 40 } }] }))
check('study: each variant measured', near(study.variants?.[0]?.volume_mm3, top * 10) && near(study.variants?.[1]?.volume_mm3, top * 40) && study.variants?.[1]?.label === 'thick', JSON.stringify(study.variants?.map((v) => v.volume_mm3)))
const depthNow = facts(await call('sw_parameters', { filter: 'D1@Plate' })).dimensions?.[0]?.value
check('study: the model is put back', study.restored === true && depthNow === 20, `restored=${study.restored} D1@Plate=${depthNow}`)

const sheet = await call('sw_view', { views: ['front', 'top', 'right', 'isometric'], display: 'shaded_edges', width: 800, height: 600 })
const sheetPng = Buffer.from(sheet.content?.find((c) => c.type === 'image')?.data ?? '', 'base64')
check('view: four views on one picture of the asked size', sheetPng.length > 24 && sheetPng.readUInt32BE(16) === 800 && sheetPng.readUInt32BE(20) === 600, `${sheetPng.length} bytes`)

const platePath = `e2e-plate-${stamp}.sldprt`
const saved = facts(await call('sw_save', { path: platePath }))
check('save as: the new part gets a file, and stays ours', saved.bytes > 0 && /e2e-plate-/.test(saved.doc?.path ?? '') && saved.doc?.opened_by_bridge === true, JSON.stringify(saved).slice(0, 200))
const plateFile = saved.doc?.path
made[made.indexOf(newPart.title)] = saved.doc?.title // saved under a new name

// A sketch the bridge did not make: saved, closed and reopened, the bridge has forgotten it.
// Building on it and failing, or deleting what was built on it, must leave it in the part.
const userPart = await fresh('part')
await call('sw_build', { doc: userPart.title, steps: [{ op: 'sketch', on: 'top', name: 'UserSketch', entities: [{ type: 'circle', center: [0, 0, 0], diameter: 30 }] }] })
const userFile = facts(await call('sw_save', { doc: userPart.title, path: `e2e-user-${stamp}.sldprt` })).doc?.path
made.splice(made.indexOf(userPart.title), 1)
await call('sw_close', { doc: userFile })
const reopened = facts(await call('sw_open', { path: userFile }))
const userTree = facts(await call('sw_features', { doc: userFile })).features?.map((f) => f.name).join() ?? ''
const onTheirs = facts(await call('sw_build', { doc: userFile, steps: [
  { op: 'extrude', sketch: 'UserSketch', depth: 5 },
  { op: 'fillet', radius: 1, edges_at: [[400, 0, 0]] },
] }))
const userTreeAfter = facts(await call('sw_features', { doc: userFile })).features?.map((f) => f.name).join() ?? ''
check('build: rolling back keeps a sketch the bridge did not make', reopened.opened_by_bridge === true && onTheirs.failed?.step === 2 && userTreeAfter === userTree && !onTheirs.rolled_back?.includes('UserSketch'),
  JSON.stringify({ rolled_back: onTheirs.rolled_back, not_rolled_back: onTheirs.not_rolled_back, same: userTreeAfter === userTree }))
const builtOnTheirs = facts(await call('sw_build', { doc: userFile, steps: [{ op: 'extrude', sketch: 'UserSketch', depth: 5, name: 'Puck' }] }))
const deletedPuck = facts(await call('sw_build', { doc: userFile, steps: [{ op: 'delete', features: ['Puck'] }] }))
const afterDelete = facts(await call('sw_features', { doc: userFile })).features?.map((f) => f.name).join() ?? ''
check('build: deleting what it built keeps the user\'s sketch it consumed', builtOnTheirs.ok === true && deletedPuck.steps?.[0]?.kept?.includes('UserSketch') && afterDelete === userTree,
  JSON.stringify({ step: deletedPuck.steps?.[0], same: afterDelete === userTree }))
const taken = await call('sw_build', { doc: userFile, steps: [{ op: 'sketch', on: 'front', name: 'UserSketch', entities: [{ type: 'circle', center: [0, 0, 0], diameter: 5 }] }] })
check('build: a name already in the part is refused before anything is made', errorCode(taken) === 'BAD_REQUEST' && facts(await call('sw_features', { doc: userFile })).features?.map((f) => f.name).join() === userTree, errorCode(taken))
await call('sw_close', { doc: userFile, discard: true })

// A cut sketched on a plane at the body's boundary points away from the material; with no
// direction given it is made the other way, and says so.
const block = await fresh('part')
const blind = facts(await call('sw_build', { doc: block.title, steps: [
  { op: 'sketch', on: 'top', entities: [{ type: 'rectangle', corner: [-20, 0, -20], opposite: [20, 0, 20] }] },
  { op: 'extrude', depth: 10 },
  { op: 'sketch', on: 'top', entities: [{ type: 'circle', center: [0, 0, 0], diameter: 10 }] },
  { op: 'extrude', cut: true, depth: 4 },
] }))
check('build: a cut that pointed away from the body is reversed', blind.steps?.[3]?.reversed === true && near(blind.model?.volume_mm3, 40 * 40 * 10 - Math.PI * 25 * 4),
  JSON.stringify({ step: blind.steps?.[3], v: blind.model?.volume_mm3 }))

const cup = await fresh('part')
const cupBuilt = facts(await call('sw_build', { doc: cup.title, steps: [
  { op: 'sketch', on: 'front', entities: [{ type: 'rectangle', corner: [-25, -25, 0], opposite: [25, 25, 0] }] },
  { op: 'extrude', end: 'mid_plane', depth: 50 },
  { op: 'shell', thickness: 2, faces_at: [[0, 25, 0]] },
] }))
check('build: a shelled cube, open at the top', near(cupBuilt.model?.volume_mm3, 50 ** 3 - 46 * 46 * 48), `${cupBuilt.model?.volume_mm3} vs 23432`)
const tube = await fresh('part')
const tubeBuilt = facts(await call('sw_build', { doc: tube.title, steps: [
  { op: 'sketch', on: 'front', entities: [{ type: 'rectangle', corner: [10, 0, 0], opposite: [20, 30, 0] }, { type: 'centerline', from: [0, 0, 0], to: [0, 30, 0] }] },
  { op: 'revolve' },
  { op: 'chamfer', distance: 1, edges_at: [[0, 30, 20]] },
] }))
const tubeV = Math.PI * (400 - 100) * 30 - 0.5 * 2 * Math.PI * (20 - 1 / 3)
check('build: a revolved tube with a chamfer', near(tubeBuilt.model?.volume_mm3, tubeV), `${tubeBuilt.model?.volume_mm3} vs ${tubeV}`)

// Two plates stacked by mates, then a third pushed 10 mm into both.
const assembly = await fresh('assembly')
const stacked = facts(await call('sw_assemble', { doc: assembly.title, steps: [
  { op: 'insert', path: plateFile },
  { op: 'insert', path: plateFile, at: [0, 50, 0] },
  { op: 'mate', type: 'coincident', a: { at: [20, 20, 0] }, b: { at: [20, 50, 0] }, align: 'anti_aligned' },
] }))
const second = stacked.components?.find((c) => /-2$/.test(c.name))
check('assemble: a mate moves the second plate onto the first', stacked.ok === true && same3(second?.origin_mm, [0, 20, 0]), JSON.stringify(stacked.components))
const concentric = facts(await call('sw_assemble', { doc: assembly.title, steps: [{ op: 'mate', type: 'concentric', a: { at: [10, 10, 0] }, b: { at: [10, 30, 0] } }] }))
check('assemble: holes made concentric', concentric.ok === true && concentric.steps?.[0]?.mate, JSON.stringify(concentric.steps ?? concentric.failed))
const clear = facts(await call('sw_interference', { doc: assembly.title }))
check('interference: stacked plates only touch', clear.interferences === 0, JSON.stringify(clear))
await call('sw_assemble', { doc: assembly.title, steps: [{ op: 'insert', path: plateFile, at: [0, 10, 0], fixed: true }] })
const clash = facts(await call('sw_interference', { doc: assembly.title }))
check('interference: a plate 10 mm into two others', clash.interferences === 2 && clash.items?.every((i) => near(i.volume_mm3, top * 10)), JSON.stringify(clash.items))
// A part that is not open: loaded without a window for the insert, then left to the assembly.
const bolt = facts(await call('sw_assemble', { doc: assembly.title, steps: [{ op: 'insert', path: `${samples}\\bolt.sldprt`, at: [300, 0, 0] }] }))
check('assemble: inserts a file that is not open', bolt.ok === true && same3(bolt.steps?.[0]?.origin_mm, [300, 0, 0]), JSON.stringify(bolt.steps ?? bolt.failed))
const bom = await call('sw_bom', { doc: assembly.title, csv: `e2e-bom-${stamp}.csv` })
const plateRow = facts(bom).items?.find((i) => /^e2e-plate-/.test(i.file))
check('bom: three of one plate, one bolt', facts(bom).rows === 2 && plateRow?.quantity === 3 && facts(bom).items?.find((i) => i.file === 'bolt.sldprt')?.quantity === 1, JSON.stringify(facts(bom).items?.map((i) => `${i.file} x${i.quantity}`)))
const bomLocal = facts(await fetchExport(client, bom, mkdtempSync(join(tmpdir(), 'swe2e-')), 'out')).csv
const csvText = bomLocal?.local_copy ? readFileSync(bomLocal.local_copy, 'utf8') : ''
check('bom: the CSV arrives and says 3', /^\uFEFF?Item,File,Configuration,Quantity/.test(csvText) && /e2e-plate-\d+\.sldprt,Default,3\r\n/.test(csvText), csvText.slice(0, 160))

const drawing = facts(await call('sw_drawing', { doc: plateFile }))
if (drawing.doc?.title) made.push(drawing.doc.title)
check('drawing: four views at the sheet scale, dimensions in', drawing.views?.length === 4 && drawing.views.every((v) => v.scale === drawing.sheet?.scale) && drawing.dimensions_imported >= 2,
  JSON.stringify({ sheet: drawing.sheet, views: drawing.views?.map((v) => `${v.orientation}@${v.scale}`), dims: drawing.dimensions_imported }))
// Saved next to its part under the same name, the drawing must be the one "<name>.slddrw" means.
const drawingFile = facts(await call('sw_save', { doc: drawing.doc?.title, path: `e2e-plate-${stamp}.slddrw` }))
made[made.indexOf(drawing.doc?.title)] = drawingFile.doc?.title
const drawingPdf = await call('sw_export', { doc: `e2e-plate-${stamp}.slddrw`, path: `e2e-drawing-${stamp}.pdf` })
check('export: a drawing named by its file is the drawing, not its part', facts(drawingPdf).doc === `e2e-plate-${stamp}.slddrw`, facts(drawingPdf).doc)
const drawingPdfLocal = facts(await fetchExport(client, drawingPdf, mkdtempSync(join(tmpdir(), 'swe2e-')), 'out'))
check('drawing: exports to PDF', (drawingPdfLocal.local_copy ? readFileSync(drawingPdfLocal.local_copy).subarray(0, 5).toString('latin1') : '') === '%PDF-', drawingPdfLocal.local_copy ?? drawingPdfLocal.local_copy_error)

for (const title of [...made].reverse()) {
  const r = await call('sw_close', { doc: title, discard: true })
  if (r.isError) check(`close ${title}`, false, r.content?.[0]?.text)
}

if (theirActive) await call('sw_open', { path: theirActive }) // already open: this only re-activates it
const after = facts(await call('sw_status'))
check('nothing of ours left open', !(after.documents ?? []).some((d) => d.opened_by_bridge), JSON.stringify((after.documents ?? []).filter((d) => d.opened_by_bridge)))
const theirsAfter = (after.documents ?? []).map((d) => `${d.path}|dirty=${d.dirty}`).sort()
check('their documents are as they were', JSON.stringify(theirsAfter) === JSON.stringify(theirs), `${theirs.length} before, ${theirsAfter.length} after`)
check('their active document is active again', (after.documents ?? []).find((d) => d.active)?.path === theirActive, theirActive ?? 'none')
check('nothing of ours left loaded without a window', (after.loaded_without_window ?? 0) === hiddenBefore, `${hiddenBefore} before, ${after.loaded_without_window ?? 0} after`)

report.failed = failed
console.log(JSON.stringify({ release, failed, calls: report.calls.map((c) => `${c.name}:${c.ms}`).join(' ') }))
await server.close()
process.exit(failed === 0 ? 0 : 1)
