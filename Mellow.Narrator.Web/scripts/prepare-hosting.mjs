import { cpSync, existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const root = process.cwd();
const angularOutput = join(root, 'dist', 'Mellow.Narrator.Web', 'browser');
const staticOutput = join(root, 'dist', 'static');
const serverOutput = join(root, 'dist', 'server');

if (!existsSync(angularOutput)) throw new Error(`Angular output not found at ${angularOutput}`);
rmSync(staticOutput, { recursive: true, force: true });
rmSync(serverOutput, { recursive: true, force: true });
mkdirSync(staticOutput, { recursive: true });
mkdirSync(serverOutput, { recursive: true });
cpSync(angularOutput, staticOutput, { recursive: true });
writeFileSync(join(serverOutput, 'index.js'), `
async function withAbsoluteMetadata(response, requestUrl) {
  const contentType = response.headers.get("content-type") || "";
  if (!contentType.includes("text/html")) return response;

  const origin = new URL(requestUrl).origin;
  const html = (await response.text()).replaceAll("__SITE_ORIGIN__", origin);
  const headers = new Headers(response.headers);
  headers.delete("content-length");
  return new Response(html, { status: response.status, headers });
}

export default {
  async fetch(request, env) {
    const response = await env.ASSETS.fetch(request);
    if (response.status !== 404 || request.method !== "GET") {
      return withAbsoluteMetadata(response, request.url);
    }
    const url = new URL(request.url);
    url.pathname = "/index.html";
    const fallback = await env.ASSETS.fetch(new Request(url, request));
    return withAbsoluteMetadata(fallback, request.url);
  }
};
`.trimStart());
