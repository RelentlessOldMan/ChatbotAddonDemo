// Renders every Mermaid diagram in README.md and docs/**/*.md to a PNG in docs/images/diagrams,
// and keeps a "Download as PNG" link under each diagram. GitHub draws Mermaid in the browser,
// so without this there is no image to save or paste into slides.
//
// Usage (from the repository root): node scripts/render-diagrams.mjs
// Needs Node.js. Uses the installed Chrome or Edge, headless (no window appears); nothing is billed.

import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/(\w:)/, '$1')), '..');
const outDir = path.join(root, 'docs', 'images', 'diagrams');
const linkMarker = '<!-- diagram-png -->';

const browser = [
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
].find(fs.existsSync);
if (!browser) throw new Error('Chrome or Edge is needed to render the diagrams.');

const work = fs.mkdtempSync(path.join(os.tmpdir(), 'diagrams-'));
const puppeteerConfig = path.join(work, 'puppeteer.json');
fs.writeFileSync(puppeteerConfig, JSON.stringify({ executablePath: browser, headless: 'new' }));

function markdownFiles(dir) {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap(e =>
    e.isDirectory() ? markdownFiles(path.join(dir, e.name)) : e.name.endsWith('.md') ? [path.join(dir, e.name)] : []);
}

const slug = text => text.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');

fs.rmSync(outDir, { recursive: true, force: true });
fs.mkdirSync(outDir, { recursive: true });

const files = [path.join(root, 'README.md'), ...markdownFiles(path.join(root, 'docs'))];
let count = 0;
for (const file of files) {
  const eol = fs.readFileSync(file, 'utf8').includes('\r\n') ? '\r\n' : '\n';
  const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
  const prefix = slug(path.relative(root, file).replace(/\.md$/, '').replace(/^docs[\\/]/, '').replace(/^README$/, 'readme'));
  const used = new Set();
  const out = [];
  let heading = '';
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const h = /^#{1,6}\s+(.*)$/.exec(line);
    if (h) heading = slug(h[1].replace(/^[\d.]+\s*/, '')).split('-').slice(0, 6).join('-');
    if (line.includes(linkMarker)) continue; // regenerated below
    out.push(line);
    if (line.trim() !== '```mermaid') continue;

    const body = [];
    while (++i < lines.length && lines[i].trim() !== '```') { body.push(lines[i]); out.push(lines[i]); }
    out.push(lines[i]);

    let name = `${prefix}--${heading || 'top'}`;
    for (let n = 2; used.has(name); n++) name = `${prefix}--${heading}-${n}`;
    used.add(name);

    const source = path.join(work, `${name}.mmd`);
    fs.writeFileSync(source, body.join('\n'));
    const png = path.join(outDir, `${name}.png`);
    execFileSync(process.platform === 'win32' ? 'npx.cmd' : 'npx',
      ['-y', '@mermaid-js/mermaid-cli@11', '-p', puppeteerConfig, '-i', source, '-o', png, '-b', 'white', '-s', '3', '-q'],
      { stdio: 'inherit', shell: process.platform === 'win32', env: { ...process.env, PUPPETEER_SKIP_DOWNLOAD: '1' } });

    const link = path.relative(path.dirname(file), png).split(path.sep).join('/');
    out.push(`<sub>[Download this diagram as PNG](${link})</sub> ${linkMarker}`);
    count++;
    console.log(`${path.relative(root, png)}`);
  }
  fs.writeFileSync(file, out.join(eol));
}
fs.rmSync(work, { recursive: true, force: true });
console.log(`${count} diagrams rendered.`);
