import { copyFileSync, cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const root = process.cwd();
const angularOutput = join(root, 'dist', 'Mellow.Narrator.Web', 'browser');
const pagesOutput = join(root, 'dist', 'github-pages');
const siteOrigin = 'https://mellowdrama81.github.io';

if (!existsSync(angularOutput)) throw new Error(`Angular output not found at ${angularOutput}`);

rmSync(pagesOutput, { recursive: true, force: true });
mkdirSync(pagesOutput, { recursive: true });
cpSync(angularOutput, pagesOutput, { recursive: true });

const indexPath = join(pagesOutput, 'index.html');
const index = readFileSync(indexPath, 'utf8').replaceAll('__SITE_ORIGIN__', siteOrigin);
writeFileSync(indexPath, index);
copyFileSync(indexPath, join(pagesOutput, '404.html'));
writeFileSync(join(pagesOutput, '.nojekyll'), '');
