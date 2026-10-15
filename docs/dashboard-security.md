# Console security

The console is an ASP.NET Core endpoint group in your host. Sign-in, permissions and tenants are covered in [sign-in and permissions](sign-in.md); this page covers what the console itself enforces.

## Mounting it

```csharp
builder.Services.AddWorkThroughDashboard();
// ... the host's own authentication, as in samples/Shop.Dashboard
app.UseAuthentication();
app.UseAuthorization();
app.MapWorkThroughDashboard("/operations");
```

`MapWorkThroughDashboard` refuses to start without host authentication unless `AllowAnonymous` is set explicitly. Anonymous mode attributes commands to a development actor; use it only for an isolated local sample. One console serves one store and environment.

## Authorization

Each operation has a `WorkThrough.<permission>` policy that, by default, requires the matching `workthrough.permission` claim. Replace a policy after `AddWorkThroughDashboard` to add host rules; it receives a `DashboardResource` with the request path and route values. Correlation IDs are never access control.

## Commands and audit

Every change (retry, cancel, recover, publish, limits, schedules, calendars, retention, and so on):

- needs an anti-forgery token from `/api/csrf`, a unique command ID, a reason and, where it applies, the expected attempt or revision;
- takes its actor from the server's claims (`iss` and `sub`), never from the request body;
- commits the change, its command receipt, a redacted summary and its audit entry in one transaction.

Repeating a command returns its first result; reusing its ID for different work is refused. Denied attempts are not audit entries; log them in the host. The audit trail is append-only through WorkThrough, not against a database administrator.

## Sensitive data

- `executions.read` shows metadata only. Inputs, outputs, progress text, error messages and stack traces need `payloads.read`, which administrators do not get automatically.
- Error text can contain secrets. `JobWorkerOptions.DescribeFailure` redacts it before it is stored. Default logs name failed jobs without their input.
- With payload protection ([ADR-027](adr/0027-payload-protection.md)), a console host without the key ring shows encrypted envelopes even with `payloads.read`. Leaving the keys out of the console host keeps business data from operators.

## Other views

The workers view shows the store's engine protocol, the workers holding back the next level ([ADR-021](adr/0021-rolling-upgrades.md)), each worker's level, and processor addresses served through an upcast.
