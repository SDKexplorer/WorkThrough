# WorkThrough Angular console

The default entry point is `LiveConsoleComponent`. It uses same-origin APIs from the configured ASP.NET dashboard mount path. The production bundle is embedded into `WorkThrough.Dashboard`; package consumers do not need Node. The earlier `console.ts` / `example-data.ts` concept remains isolated from this entry point and is not shipped as live data.

Use Node 24, `npm ci`, `npm run format:check`, and `npm run build`, then build the .NET host. Output is `../artifacts/console-preview/browser`. Run `samples/Demo.Host` in Development and open `/operations/jobs`. A bare static server has no job API and cannot serve as the live console.

The console includes job filtering, saved graphs with selectable nodes, protected attempt/stack inspection, audited recovery, flow drafts/publication/retirement, recurring schedule controls, worker/capacity observations, and audit history. UI permission checks improve usability; ASP.NET policies enforce access. No placeholder metrics or simulated successful operations appear in the live entry point.
