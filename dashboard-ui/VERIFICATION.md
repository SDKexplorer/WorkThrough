# Live console verification

Local verification on 7 October 2026, Node 24.20.0, Angular 22.2.1 / build tooling 22.2.2. The live entry point is `live-console.ts`.

- Production build, strict TypeScript/templates, and Prettier passed. Final JS/CSS is approximately 199 kB raw / 56 kB estimated transfer.
- Embedded assets were served by Demo.Host (replaced by `samples/Shop.Api` on 2026-10-11) on .NET 8 at `/operations/jobs` against real SQLite data. Backend tests exercise nested dashboard paths, PathBase, authentication, CSRF, and payload/action permission separation.
- Correlation filtering and failed-step inspection showed a stored Incident, waiting dependents, two retained attempts, exception evidence, and the saved graph.
- A flow draft was saved without execution. A recurring schedule draft was saved and published while its trigger remained paused. Actor, reason, action, and result appeared in the audit trail.
- Keyboard activation works for navigation, inspection, and command confirmation. The skip link becomes visible on focus and targets main content; hidden placement no longer extends above the viewport.
- At 375 CSS pixels, document width matched viewport width. Tables scroll within their container. Light/dark themes and desktop layout are manually checked; this does not certify every browser, screen reader, or contrast pair.
- No browser warning/error messages were observed during the local check. External OIDC account login, exhaustive accessibility testing, and production load remain unverified.

On 8 October, the full frontend gate and .NET packaging passed with the processor-coverage view (approximately 201 kB raw / 56 kB transfer). Against the existing sample database, the live view showed three required processor/queue pairs, zero ready work for the incident's steps, and coverage from the current worker. Expired historical heartbeats stopped contributing to coverage. The view had no document overflow at 375 CSS pixels and no observed browser warnings/errors. Local screenshot: `artifacts/workthrough-coverage.png`. Backend integration tests cover exact versions, queue matching, schedule inclusion, snapshot bounds, and metadata permission.

See the repository verification record for the runtime/provider/package matrix and hosted CI evidence. Earlier prototype checks are retained below for historical context only.

The earlier fictional-data console concept was removed on 2026-10-11; only the live console remains.
