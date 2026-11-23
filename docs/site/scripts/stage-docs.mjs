import { copyFileSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { rewriteLinks } from './rewrite-links.mjs';

const site = fileURLToPath(new URL('../', import.meta.url));
const root = resolve(site, '../..');
const base = '/fhir-federation-service/';
const project = 'https://brendankowitz.github.io' + base;
const repo = 'https://github.com/brendankowitz/fhir-federation-service';
const references = join(root, 'artifacts/docs/references');
const legacy = JSON.parse(readFileSync(join(root, 'tools/legacy-docs-urls.json')));
const revision = process.argv[2];
const pages = [
  ...readdirSync(site).filter(name => name.endsWith('.md')).map(name => join(site, name)),
  join(root, 'docs/guides/client-developer-guide.md'),
  ...['configuration', 'contracts', 'client-api'].map(name => join(references, name + '.md')),
];
const routes = new Map(pages.map(path => [path, base + basename(path, '.md') + '/']));
const dest = join(site, 'src/content/docs/generated');
const figures = join(site, 'public/architecture');
rmSync(figures, { recursive: true, force: true });
mkdirSync(figures, { recursive: true });
rmSync(dest, { recursive: true, force: true });
mkdirSync(dest, { recursive: true });

for (const page of pages) {
  const slug = basename(page, '.md');
  const text = readFileSync(page, 'utf8').replace(/^\uFEFF/, '').replaceAll('\r\n', '\n');
  const article = /^(?:---\n(?<header>[\s\S]*?)\n---\n)?\s*# (?<title>[^\n]+)\n(?<body>[\s\S]*)$/.exec(text);
  if (!article) throw new Error(`${page}: expected a leading title`);
  const { header, title, body } = article.groups;
  const oldTitle = legacy[slug + '.html']?.[0];
  const markdown = rewriteLinks(body, url => {
    if (url.startsWith(project)) return base + url.slice(project.length);
    if (/^(?:[a-z][\w+.-]*:|[/#?]|$)/i.test(url)) return url;
    const [, pathname, suffix] = /^([^?#]*)([\s\S]*)$/.exec(url);
    const path = resolve(dirname(page) === references ? site : dirname(page), decodeURIComponent(pathname));
    if (routes.has(path)) return routes.get(path) + suffix;
    const segments = relative(root, path).split(sep);
    const stat = statSync(path, { throwIfNoEntry: false });
    if (!stat || segments[0] === '..') throw new Error(`${page}: missing source target ${url}`);
    const encoded = segments.map(encodeURIComponent).join('/');
    if (dirname(path) === join(root, 'assets/architecture') && path.endsWith('.svg')) {
      copyFileSync(path, join(figures, basename(path)));
      return base + 'architecture/' + encodeURIComponent(basename(path)) + suffix;
    }
    if (encoded.startsWith('docs/site/public/')) return base + encoded.slice('docs/site/public/'.length) + suffix;
    return `${repo}/${stat.isDirectory() ? 'tree' : 'blob'}/${revision}/${encoded}${suffix}`;
  });
  writeFileSync(join(dest, basename(page)), `---\n${header ?? 'title: ' + JSON.stringify(title)}\nslug: ${slug}\n---\n\n${oldTitle ? `<span id="${oldTitle}"></span>\n` : ''}${markdown}`);
}
