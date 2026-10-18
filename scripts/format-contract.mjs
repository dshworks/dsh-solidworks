#!/usr/bin/env node
/**
 * Rewrite protocol/tools.json in its house layout: a value stays on one line
 * when it fits in 140 columns and is broken open otherwise, so a short schema
 * property reads as one line and sw_build's step schema still reads as a tree.
 *
 *   node scripts/format-contract.mjs          # rewrite
 *   node scripts/format-contract.mjs --check  # exit 1 if not formatted
 */
import { readFileSync, writeFileSync } from 'node:fs'

const FILE = new URL('../protocol/tools.json', import.meta.url)
const WIDTH = 140

function format(value, indent) {
  const flat = JSON.stringify(value, null, 1).replace(/\n\s*/g, ' ').replace(/\[ /g, '[').replace(/ \]/g, ']').replace(/\{ /g, '{ ').replace(/ \}/g, ' }')
  if (value === null || typeof value !== 'object' || indent.length + flat.length <= WIDTH) return flat
  const inner = `${indent}  `
  if (Array.isArray(value)) return `[\n${value.map((v) => inner + format(v, inner)).join(',\n')}\n${indent}]`
  return `{\n${Object.entries(value).map(([k, v]) => `${inner}${JSON.stringify(k)}: ${format(v, inner)}`).join(',\n')}\n${indent}}`
}

const text = readFileSync(FILE, 'utf8')
const out = `${format(JSON.parse(text), '')}\n`
if (process.argv.includes('--check')) {
  if (out !== text) { console.error('protocol/tools.json is not formatted; run node scripts/format-contract.mjs'); process.exit(1) }
} else writeFileSync(FILE, out)
