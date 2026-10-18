// Prefix every JSON line from stdin with the wall-clock time it arrived: {"t": ms, ...event}.
import { createInterface } from 'node:readline'
for await (const line of createInterface({ input: process.stdin })) {
  if (!line.trim()) continue
  try { process.stdout.write(JSON.stringify({ t: Date.now(), ...JSON.parse(line) }) + '\n') }
  catch { process.stdout.write(JSON.stringify({ t: Date.now(), raw: line }) + '\n') }
}
