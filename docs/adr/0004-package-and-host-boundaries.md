# ADR-004: Optional hosts and small inward dependencies

Status: accepted.

Context: APIs, background workers, and dashboards must be independently deployable with straightforward NuGet setup.

Decision: keep a framework-free Core package and optional Hosting, provider, Dashboard, and Generators packages. Use standard dependency injection and BackgroundService. Expose separate command and query capabilities without requiring CQRS infrastructure or a mediator.

Alternatives: a monolithic web/worker package; a mandatory messaging framework; dashboard deserialization using processor assemblies.

Consequences: more package artifacts, but deployment roles remain independent. Samples and package-consumer smoke tests must keep the setup honest.
