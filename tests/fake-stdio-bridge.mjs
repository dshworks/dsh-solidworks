// A stdio MCP server standing in for `swbridge.exe mcp` in spawn tests.
import { createInterface } from 'node:readline'
const argv = process.argv.slice(2)
const send = (m) => process.stdout.write(`${JSON.stringify(m)}\n`)
createInterface({ input: process.stdin }).on('line', (line) => {
  const msg = JSON.parse(line)
  if (msg.method === 'initialize') send({ jsonrpc: '2.0', id: msg.id, result: { protocolVersion: msg.params.protocolVersion, capabilities: { tools: {} }, serverInfo: { name: 'fake-stdio', version: '0' } } })
  else if (msg.method === 'tools/call' && msg.params.name === 'exit') process.exit(3)
  else if (msg.method === 'tools/call') send({ jsonrpc: '2.0', id: msg.id, result: { content: [{ type: 'text', text: '{}' }], structuredContent: { argv } } })
})
