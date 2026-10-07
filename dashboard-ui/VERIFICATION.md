# Live console verification

Local verification on 7 October 2026, Node 24.20.0, Angular 22.2.1 / build tooling 22.2.2. The live entry point is `live-console.ts`; concept fixtures are not bootstrapped.

- Production build, strict TypeScript/templates, and Prettier passed. Final JS/CSS is approximately 199 kB raw / 56 kB estimated transfer.
- Embedded assets were served by Demo.Host on .NET 8 at `/operations/jobs` against real SQLite data. Backend tests exercise nested dashboard paths, PathBase, authentication, CSRF, and payload/action permission separation.
- Correlation filtering and failed-step inspection showed a stored Incident, waiting dependents, two retained attempts, exception evidence, and the saved graph.
- A flow draft was saved without execution. A recurring schedule draft was saved and published while its trigger remained paused. Actor, reason, action, and result appeared in the audit trail.
- Keyboard activation works for navigation, inspection, and command confirmation. The skip link becomes visible on focus and targets main content; hidden placement no longer extends above the viewport.
- At 375 CSS pixels, document width matched viewport width. Tables scroll within their container. Light/dark themes and desktop layout are manually checked; this does not certify every browser, screen reader, or contrast pair.
- No browser warning/error messages were observed during the local check. External OIDC account login, exhaustive accessibility testing, and production load remain unverified.

See the repository verification record for the runtime/provider/package matrix. Earlier prototype checks are retained below for historical context only.

# Console concept verification

Local verification on 7 October 2026, Node 24.20.0, Angular 22.2.1 / build tooling 22.2.2.

- Production build and strict TypeScript/template checks passed; initial JavaScript/CSS approximately 165 kB raw, 49 kB estimated transfer, below the configured budget.
- Prettier formatting check passed. A separate CI job now installs from the lockfile, checks formatting, and builds; hosted CI has not run.
- npm reported zero known dependency vulnerabilities during installation.
- Browser interaction checks passed: select a blocked node, inspect input, switch to the successful fixture and inspect output, combine correlation and status filters, show empty results, open recovery preview, and dismiss it with Escape.
- Dark/light theme toggling passed. Key normal-text/status/chart color pairs were calculated for contrast; two chart-label colors were corrected. This is not a full accessibility certification.
- Responsive checks at 375, 768, and 1280 CSS pixels found a navigation overflow at 375; it was corrected and document width then matched viewport width. The table intentionally scrolls within its labeled region on narrow screens.
- No browser console errors were observed. Reduced-motion behavior is implemented in CSS; OS-level reduced-motion and screen-reader testing remain unverified.

These checks apply only to the standalone concept. They do not verify live metrics, real provider data, generic graph layout, authentication integration, or recovery commands. The .NET runtime was not changed or retested for this UI-only work. All fixtures remain isolated from the shipped dashboard.
