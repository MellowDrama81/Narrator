# Mellow Narrator Web

The web version of Mellow Narrator is a completely client-side Angular 22 application using Angular
Material, TypeScript, and IndexedDB. LLM requests are sent directly from the browser to the configured
OpenAI-compatible endpoint.

## Local development

Install Node.js 22.22.3+, Node.js 24.15.0+, or Node.js 26+, pnpm 11.9.0, and the .NET 10 SDK. From this
directory:

```powershell
corepack enable
corepack prepare pnpm@11.9.0 --activate
pnpm install --frozen-lockfile
pnpm start
```

Open [http://localhost:4200](http://localhost:4200).

## Tests and production build

```powershell
pnpm test
pnpm run build
```

The production output is written to `dist/`.

## GitHub Pages

The production web application is hosted at
[https://mellowdrama81.github.io/Narrator](https://mellowdrama81.github.io/Narrator). Build the project-site
artifact with:

```powershell
pnpm run build:github-pages
```

The resulting static site is written to `dist/github-pages/`. It includes a `.nojekyll` marker and a
`404.html` fallback for Angular routes and has production social-card URLs resolved to the GitHub Pages
site URL. Pushes to `master` are deployed from the `Narrator` repository by
`.github/workflows/pages.yml`; its Pages publishing source must be set to **GitHub Actions**.

## Shared prompt templates

The authored prompt templates live in the repository-level `prompts/` directory. The web app imports
the generated `src/app/core/prompt-templates.generated.ts` module. It is regenerated automatically
before development, tests, and production builds:

```powershell
pnpm prompts:generate
pnpm prompts:check
```

Do not edit the generated TypeScript module directly.

## Browser storage and LLM access

Settings, definitions, stories, and trash are stored in IndexedDB and are scoped to the page's origin.
The provider must allow browser CORS requests from the development or deployed origin. An HTTPS-hosted
copy may also be blocked from calling an unsecured HTTP model endpoint by browser mixed-content rules.
