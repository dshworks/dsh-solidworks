/**
 * The sw_* tools, as dsh sees them.
 *
 * Names, descriptions and input schemas come from protocol/tools.json, the
 * contract the bridge serves too; nothing about a tool is written twice. Each
 * definition is built with dsh's own `createMcpToolDefinition`, so argument
 * validation, error results and image admission (sw_view's PNG reaches the
 * model only when its route declares image input) behave exactly as for any
 * MCP server dsh connects to.
 */
import { createHash } from 'node:crypto'
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, extname, join, resolve, win32 } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createMcpToolDefinition } from '@deepseek-ai/dsh-mcp-client'

const HERE = dirname(fileURLToPath(import.meta.url))

/** The shared contract, read once. */
export const contract = JSON.parse(readFileSync(join(HERE, '..', 'protocol', 'tools.json'), 'utf8'))

/** A tool failure in MCP's shape: the model reads "CODE: sentence". */
export function failure(code, message) {
  return { content: [{ type: 'text', text: `${code}: ${message}` }], isError: true }
}

/**
 * Register every contract tool on the tools service.
 * @param {object} ctx - the context carrying `tools` (and optional attachments/llm for images).
 * @param {object} options
 * @param {() => Promise<object | null>} options.client - the live bridge's MCP client, or null.
 * @param {() => string} options.notConnected - what to tell the model when there is none.
 * @param {boolean} options.remote - the bridge runs on another machine.
 * @param {string} options.fetchDir - workspace folder for fetched exports; empty disables.
 * @param {(tool: string) => string} [options.outdated] - what to say when the bridge does not
 *   know a tool: a bridge paired before this plugin was updated keeps running its old exe.
 * @returns {string[]} registered tool names.
 */
export function registerTools(ctx, { client, notConnected, remote, fetchDir, outdated }) {
  const names = []
  for (const tool of contract.tools) {
    const definition = createMcpToolDefinition(ctx, {
      name: tool.name,
      rawName: tool.name,
      description: tool.description,
      inputSchema: tool.input,
      call: (args, exec) => callBridge(tool, args, exec),
    })
    // The bridge enforces the budget on SOLIDWORKS; dsh's own deadline sits
    // past it, so the model gets the bridge's precise TIMEOUT, not a generic abort.
    // register() ties the registration to ctx, so disposal unregisters it.
    ctx.tools.register({ ...definition, timeoutMs: (tool.budget_s + 30) * 1000 })
    names.push(tool.name)
  }
  return names

  async function callBridge(tool, args, exec) {
    let live
    try {
      live = await client()
    } catch (error) {
      return failure(error.code ?? 'NOT_CONNECTED', error.message)
    }
    if (!live) return failure('NOT_CONNECTED', notConnected())
    try {
      const result = await live.callTool(tool.name, args, { signal: exec.signal, timeoutMs: (tool.budget_s + 15) * 1000 })
      if (result.isError && outdated && /^BAD_REQUEST: unknown tool/.test(result.content?.[0]?.text ?? '')) {
        return failure('BAD_REQUEST', outdated(tool.name))
      }
      if (tool.fetch && remote && fetchDir && !result.isError) {
        return await fetchExport(live, result, workspaceOf(exec), fetchDir, exec.signal)
      }
      return result
    } catch (error) {
      return failure(error.code ?? 'INTERNAL', error.message)
    }
  }
}

function workspaceOf(exec) {
  return exec.agent?.session?.header?.cwd ?? process.cwd()
}

/**
 * Copy a file the bridge wrote from the Windows machine into the dsh workspace,
 * check it against the bridge's own SHA-256, and add `local_copy` next to it.
 * The file is the result itself (sw_export: path, bytes, sha256 at the top) or
 * one object inside it (sw_bom's `csv`). A failed copy keeps the tool's success
 * and says why the copy is missing; a result without a file is left alone.
 */
export async function fetchExport(live, result, workspace, fetchDir, signal) {
  const top = { ...result.structuredContent }
  const key = typeof top.sha256 === 'string' ? null
    : Object.keys(top).find((k) => typeof top[k]?.sha256 === 'string' && typeof top[k]?.path === 'string')
  if (key === undefined) return result
  const facts = key === null ? top : (top[key] = { ...top[key] })
  try {
    const read = await live.readResource(`swbridge:export/${facts.sha256}`, { signal, timeoutMs: 300_000 })
    const blob = read.contents?.[0]?.blob
    if (typeof blob !== 'string') throw new Error('the bridge sent no file content')
    const bytes = Buffer.from(blob, 'base64')
    const sha = createHash('sha256').update(bytes).digest('hex')
    if (sha !== facts.sha256) throw new Error('the copy does not match the sha256 the bridge reported')
    const folder = resolve(workspace, fetchDir)
    mkdirSync(folder, { recursive: true })
    const target = freeName(folder, win32.basename(facts.path), sha)
    writeFileSync(target, bytes)
    facts.local_copy = target
  } catch (error) {
    facts.local_copy_error = error.code === 'MEMBER_MISSING' ? 'this bridge cannot send files' : error.message
  }
  return { ...result, content: [{ type: 'text', text: JSON.stringify(top) }], structuredContent: top }
}

/** `name`, or `name-2`, `name-3`... — never overwriting a different file. */
function freeName(folder, name, sha) {
  const ext = extname(name)
  const stem = name.slice(0, name.length - ext.length)
  for (let n = 1; ; n++) {
    const candidate = join(folder, n === 1 ? name : `${stem}-${n}${ext}`)
    if (!existsSync(candidate)) return candidate
    if (createHash('sha256').update(readFileSync(candidate)).digest('hex') === sha) return candidate
  }
}
