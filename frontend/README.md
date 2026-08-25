# OntoPilot Frontend

The OntoPilot frontend is a React and TypeScript single-page application for managing documents,
reviewing extraction jobs, exploring RDF/OWL ontologies, resolving conflicts, and auditing changes.

For the complete project overview and deployment instructions, see the repository
[README](../README.md).

## Stack

- React 19
- TypeScript 6
- Vite 8
- Tailwind CSS 4
- Radix UI and shadcn components
- React Flow and Dagre for ontology visualization
- Recharts for metrics
- KaTeX for ontology expressions

## Development

Requirements:

- Node.js 22+
- pnpm
- OntoPilot backend running at `http://localhost:5072`

```powershell
pnpm install
pnpm dev
```

The Vite server runs at <http://localhost:5173> and proxies `/api` requests to the backend.

## Validation

```powershell
pnpm lint
pnpm build
```

## End-to-end tests

The `.NET` smoke suite lives under `e2e/dotnet/` and exercises three flows
(session round-trip, upload → extract → publish, vocabulary SKOS CRUD) against a
live .NET backend. To execute it locally:

```powershell
pnpm install                           # picks up @playwright/test
pnpm test:e2e:dotnet:install           # one-time: downloads Chromium with system deps
pnpm test:e2e:dotnet                   # runs the dot-net project
```

The backend boots automatically via the Playwright `webServer` block in
`playwright.config.ts` unless `DOTNET_BASE_URL` is already set in the environment
(set it to point at a backend you started manually). When the backend is not
reachable on `/api/health`, the specs self-skip via `beforeAll` so a clean
machine does not fail the run — start the backend and rerun to actually execute
the assertions.

## Production Image

The frontend Dockerfile builds the static Vite bundle and serves it through Nginx. Nginx also
proxies `/api` to the backend service, keeping browser requests same-origin.
