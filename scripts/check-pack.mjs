// prepack: the exe in the package must be the one this source builds, byte for byte.
// The bridge build is deterministic under the SDK pinned in global.json, so its SHA-256 is
// committed in bridge/swbridge.exe.sha256 and CI rebuilds it to the same hash.
import { createHash } from 'node:crypto'
import { existsSync, readFileSync } from 'node:fs'

const exe = 'bin/swbridge.exe'
if (!existsSync(exe)) throw new Error(`${exe} is missing: run npm run build:bridge`)
const have = createHash('sha256').update(readFileSync(exe)).digest('hex')
const want = readFileSync('bridge/swbridge.exe.sha256', 'utf8').trim()
if (have !== want) {
  throw new Error(`${exe} is ${have}, but this source builds ${want}. Run npm run build:bridge; if the bridge source changed, update bridge/swbridge.exe.sha256 to the new build.`)
}
console.log(`swbridge.exe ${have}`)
