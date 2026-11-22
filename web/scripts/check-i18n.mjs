// Checks that every language defines the same keys as the reference (fr) and that every key used by the
// interface exists. Run: npm run check:i18n
import { readFileSync, readdirSync } from 'node:fs'
import { pathToFileURL } from 'node:url'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = join(dirname(fileURLToPath(import.meta.url)), '..', 'src')
const langs = ['fr', 'en', 'es', 'it', 'de']
const messages = {}
for (const l of langs) messages[l] = (await import(pathToFileURL(join(root, 'locales', `${l}.js`)).href)).default

let failed = false
const report = (text) => { failed = true; console.error(text) }

for (const l of langs.filter((x) => x !== 'fr')) {
  for (const k of Object.keys(messages.fr)) if (!(k in messages[l])) report(`[${l}] missing key: ${k}`)
  for (const k of Object.keys(messages[l])) if (!(k in messages.fr)) report(`[${l}] unknown key: ${k}`)
}

// Keys written literally in the sources: t('x.y') / tOrNull('x.y'), plus those built from the schema.
const used = new Set()
for (const file of readdirSync(root).filter((f) => f.endsWith('.vue') || f.endsWith('.js'))) {
  const text = readFileSync(join(root, file), 'utf8')
  for (const m of text.matchAll(/\bt(?:OrNull)?\(\s*'([a-zA-Z]+\.[A-Za-z0-9_.]+)'/g)) used.add(m[1])
}
const { sections } = await import(pathToFileURL(join(root, 'schema.js')).href)
for (const s of sections) {
  used.add(`s.${s.id}`)
  for (const g of s.groups) {
    used.add(`g.${g.id}`)
    for (const f of g.fields) used.add(`f.${f.path}`)
  }
}
for (const k of used) if (!(k in messages.fr)) report(`used but not defined: ${k}`)

if (failed) process.exit(1)
console.log(`i18n OK: ${Object.keys(messages.fr).length} keys x ${langs.length} languages, ${used.size} keys used`)
