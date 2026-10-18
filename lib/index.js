/**
 * dsh-solidworks — SOLIDWORKS for DeepSeek Harness.
 *
 * Two halves, one protocol:
 *
 *   swbridge.exe  (bridge/, C# on .NET Framework 4.8, ships in bin/)
 *       runs as the user, next to a running SOLIDWORKS, and serves MCP. Every
 *       SOLIDWORKS call is late-bound through IDispatch on one STA thread, so
 *       the same exe drives SOLIDWORKS 2016 and every release after it.
 *   this plugin
 *       registers the sw_* tools in dsh and carries their calls to the bridge:
 *       over stdio when dsh runs on the same Windows PC (spawn), or over a link
 *       the bridge dials when SOLIDWORKS runs elsewhere, typically a Windows VM
 *       on the Mac running dsh (listen).
 *
 * protocol/tools.json is the contract both halves read: tool names, schemas,
 * budgets and error codes exist once.
 *
 * The safety rules live on the Windows side, where the files are: documents
 * open read-only unless asked otherwise, exports go only into allowed folders
 * and never overwrite unasked, saving over a source file is off unless the
 * bridge is started with --allow-save, and the bridge never closes a document
 * with someone's unsaved work. dsh can ask; only the bridge decides.
 */
import { readFileSync } from 'node:fs'
import z from '@deepseek-ai/schemastery'
import { LinkServer } from './link.js'
import { dialAddresses, loadToken, stateDir, writeKit } from './pairing.js'
import { BUNDLED_BRIDGE, SpawnedBridge, bridgeFlags } from './spawn.js'
import { registerTools } from './tools.js'

export const name = 'dsh-solidworks'

const { version } = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'))
const CLIENT_INFO = { name: 'dsh-solidworks', version }

export const Config = z.object({
  /**
   * How dsh reaches SOLIDWORKS. `auto` spawns the bridge when dsh itself runs
   * on Windows and listens for one everywhere else.
   */
  link: z.union(['auto', 'spawn', 'listen']).default('auto'),
  /**
   * Where to listen for a bridge (listen mode). The port answers only a bridge
   * that proves the pairing token. 0.0.0.0 lets a VM reach it.
   */
  listen: z.object({
    host: z.string().default('0.0.0.0'),
    port: z.natural().max(65535).default(47321),
  }),
  /**
   * host[:port] entries the bridge should dial, written into the pairing kit.
   * Empty lists this machine's own addresses, VM networks first.
   */
  advertise: z.array(String).default([]),
  /** Pairing token. Empty keeps a generated one in $DSH_HOME/dsh-solidworks/pairing.json. */
  token: z.string().default(''),
  /** Spawn mode: the bridge's own options, passed as its flags. */
  bridge: z.object({
    exe: z.string().default(''),
    pid: z.natural().default(0),
    out: z.string().default(''),
    allowWrite: z.array(String).default([]),
    allowSave: z.boolean().default(false),
  }),
  /**
   * Listen mode: copy each export into this folder of the session workspace.
   * Empty leaves exports on the Windows side only.
   */
  fetchDir: z.string().default('solidworks'),
  /**
   * Listen mode: right after dsh starts, how long a call waits for the bridge on
   * Windows to come back (it retries every few seconds) before saying it is not connected.
   */
  startupWaitMs: z.natural().max(120_000).default(15_000),
})

const GUIDE = [
  'SOLIDWORKS (sw_* tools): the tools drive a SOLIDWORKS running on the user\'s Windows desktop, through a bridge.',
  'Call sw_status first in a task; it tells you the release (2016 and later), the open documents, and whether the bridge is connected.',
  'Units are always mm, degrees and kg, whatever the document uses. Paths are Windows paths on the SOLIDWORKS machine.',
  'Geometry has one coordinate space everywhere: model-space points [x, y, z] in mm, Y up; the front plane is z = 0, top is y = 0, right is x = 0 (a 12 mm plate on the top plane has its top face at y = 12, not z = 12). sw_build, sw_probe, sw_assemble and every box_mm use it. Faces and edges are picked by a point on them.',
  'To model a part: sw_new, then sw_build steps (sketch on a plane or on a face, extrude, revolve, fillet, chamfer, shell). Compare the volume and box it returns with what you meant, and look with sw_view (views: front, top, right, isometric on one picture).',
  'When the user says "this face" or "that hole", read their selection with sw_probe and no points.',
  'To assemble: sw_save each new part with a path, sw_new type assembly, then sw_assemble insert and mate; verify with sw_interference (0 means nothing overlaps) and sw_bom.',
  'sw_open opens read-only by default. sw_edit changes values in memory and rebuilds; sw_study tries many values in one call and puts them back. Read rebuild results: a call can succeed and still leave features failing.',
  'sw_drawing makes a dimensioned drawing of a saved model; sw_export writes PDF, STEP and other formats and never touches the source. sw_save over a source file works only if the user enabled it.',
  'Never claim a value you did not read back.',
].join('\n')

/**
 * @param {import('@deepseek-ai/cordis').Context} ctx - host plugin context.
 * @param {ReturnType<typeof Config>} config - validated configuration.
 */
export async function apply(ctx, config) {
  const mode = config.link === 'auto' ? (process.platform === 'win32' ? 'spawn' : 'listen') : config.link
  const logger = ctx.logger
  let connection
  let notConnected
  // A bridge that does not know a tool is older than this plugin: on another machine the
  // installed exe stays until pair.cmd runs again; the bundled one (spawn) is always current.
  let outdated = (tool) => `the bridge does not have ${tool}; it is older than this plugin (${version}). ` +
    'Update the bridge, then try again. Tell the user exactly this.'

  if (mode === 'spawn') {
    const spawned = new SpawnedBridge({
      subprocess: ctx.get('subprocess'),
      exe: config.bridge.exe || BUNDLED_BRIDGE,
      flags: bridgeFlags(config.bridge),
      clientInfo: CLIENT_INFO,
      logger,
    })
    ctx.effect(() => () => spawned.close(), 'dsh-solidworks.spawn')
    connection = () => spawned.client()
    notConnected = () => {
      const exit = spawned.lastExit
      return `swbridge.exe is not running${exit ? ` (last exit: ${exit.tail || `code ${exit.exitCode}`})` : ''}; ` +
        'start SOLIDWORKS on this PC and try again'
    }
  } else {
    const dir = stateDir()
    const token = loadToken(config.token, dir)
    const server = new LinkServer({ ...config.listen, token, clientInfo: CLIENT_INFO, logger })
    let kit = null
    let listening = false
    try {
      const port = await server.listen()
      listening = true
      kit = writeKit({ dir, exe: BUNDLED_BRIDGE, token, addresses: dialAddresses({ host: config.listen.host, port, advertise: config.advertise }) })
      logger.info(`waiting for a SOLIDWORKS bridge on ${config.listen.host}:${port}; pairing kit: ${kit.share ?? kit.script}`)
    } catch (error) {
      // Another dsh process may already hold the port (a web and a headless
      // profile at once). The tools still register and say so when called.
      logger.warn(`cannot listen on ${config.listen.host}:${config.listen.port}: ${error.message}`)
      notConnected = () => `this dsh cannot listen on port ${config.listen.port} (${error.code ?? error.message}); ` +
        'another dsh process probably has it, or set listen.port to a free port'
    }
    ctx.effect(() => () => server.close(), 'dsh-solidworks.listen')
    outdated = (tool) => {
      const bridge = server.live?.bridge
      return `the bridge on ${bridge?.machine ?? 'the Windows PC'} is swbridge ${bridge?.version ?? 'of an older release'} and does not have ${tool}; ` +
        `this plugin is ${version}. On that PC, signed in as its SOLIDWORKS user, run pair.cmd from ` +
        `${kit?.share ?? kit?.folder ?? 'the pairing kit'} again: it installs the new bridge over the old one. Tell the user exactly this.`
    }
    // A bridge retries every few seconds, so for a moment after dsh starts there is
    // none yet. The first calls wait that moment out instead of failing.
    const started = Date.now()
    connection = async () => (listening ? server.waitForClient(Math.max(0, started + config.startupWaitMs - Date.now())) : null)
    notConnected ??= () => {
      const refused = server.lastRefusal
      return 'no SOLIDWORKS bridge is connected to dsh. On the Windows PC that runs SOLIDWORKS, ' +
        `signed in as its user, run pair.cmd from ${kit?.share ? `${kit.share} (the Mac's home folder as Parallels shares it)` : kit?.folder}` +
        `${kit?.share ? `, or copy the folder ${kit.folder} there first` : ' after copying that folder there'}. ` +
        'It installs the bridge for that user and keeps it connected. ' +
        `${refused ? `The last bridge that tried was refused (${refused.reason}). ` : ''}` +
        'Tell the user exactly this; do not guess a workaround.'
    }
  }

  ctx.inject(['tools'], (toolCtx) => {
    registerTools(toolCtx, {
      client: connection,
      notConnected,
      outdated,
      remote: mode === 'listen',
      fetchDir: config.fetchDir,
    })
  })

  // Static on purpose: a section that changed with the connection state would
  // break the provider's prompt cache. Connection problems reach the model as
  // tool errors instead.
  ctx.inject(['systemPrompt'], (promptCtx) => {
    promptCtx.systemPrompt.section({
      name: 'dsh-solidworks',
      order: promptCtx.systemPrompt.getSectionOrder('MCP_SERVERS'),
      interpolate: false,
      text: GUIDE,
    })
  })
}
