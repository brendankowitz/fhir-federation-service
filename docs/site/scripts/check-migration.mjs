import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { runInNewContext } from 'node:vm';
import { parse } from 'parse5';

const site = process.argv[2];
const baseline = JSON.parse(readFileSync(new URL('../../../tools/legacy-docs-urls.json', import.meta.url)));
const base = '/fhir-federation-service/';
const sha = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
const href = `https://github.com/brendankowitz/fhir-federation-service/commit/${sha}`;

function* walk(node) {
  yield node;
  for (const child of node.childNodes ?? []) yield* walk(child);
}
const elements = path => [...walk(parse(readFileSync(join(site, path), 'utf8')))];
const attr = (element, key) => element.attrs?.find(({ name }) => name === key)?.value;

for (const [name, anchors] of Object.entries(baseline)) {
  const slug = name.slice(0, -'.html'.length);
  const page = elements(slug === 'index' ? name : `${slug}/index.html`);
  for (const id of anchors) assert(page.some(node => attr(node, 'id') === id), `${name}: lost #${id}`);
  if (slug === 'index') {
    assert(page.some(node => node.tagName === 'a' && attr(node, 'href') === href), `Homepage must link to ${href}`);
    continue;
  }
  const legacy = elements(name);
  const target = `${base}${slug}/`;
  assert(legacy.some(node => node.tagName === 'meta' && attr(node, 'http-equiv') === 'refresh' &&
    attr(node, 'content') === `0; url=${target}`), `${name}: missing static fallback`);
  const scripts = legacy.filter(node => node.tagName === 'script');
  const search = '?q=one%20two&view=full';
  for (const id of anchors) {
    const hash = '#' + id;
    const destinations = [];
    const location = new URL(`${base}${name}${search}${hash}`, 'https://brendankowitz.github.io');
    location.replace = value => destinations.push(new URL(value, location).href);
    for (const script of scripts) {
      runInNewContext(script.childNodes.map(node => node.value).join(''), { location }, { filename: name, timeout: 500 });
    }
    assert.deepEqual(destinations, [location.origin + target + search + hash], `${name}: redirect lost destination, query or #${id}`);
  }
}
assert(existsSync(join(site, 'pagefind/pagefind.js')) && readdirSync(join(site, 'pagefind')).some(name => name.endsWith('.pf_meta')), 'Missing Pagefind index');
console.log(`Verified homepage revision, ${Object.keys(baseline).length} legacy URLs, ${Object.values(baseline).flat().length} anchors and search index.`);
