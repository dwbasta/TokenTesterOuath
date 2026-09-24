# Contributing

## Deployment standards

- IIS deployment scripts must support clean first-time provisioning without requiring manual pre-steps in IIS Manager.
- Before creating a new IIS site, deployment scripts must automatically resolve HTTP binding conflicts caused by `Default Web Site` for the target binding.
- Existing non-default sites must never be modified automatically when binding conflicts are detected; deployment should fail with a clear error for those cases.
- Script behavior must be idempotent across repeated deployments.
- Deployment scripts must not overwrite existing runtime configuration files by default.
- Runtime configuration overwrite must require an explicit switch and clear operator intent.

## Runtime configuration policy

- Protected app runtime file: `jwtsettings.json`
- MCP runtime files: `jwtsettings.json`, `obosettings.json`
- On redeploy, preserve existing runtime values unless explicitly overridden.
- Prompted or parameter-provided values may be used for first-time creation.

## Identity provider extensibility

- Authentication and authorization logic must remain provider-agnostic and must not hardcode a single identity provider implementation.
- Provider differences (claim names, scope formats, token endpoint patterns, grant-type support) must be represented through configuration, not source branching per provider.
- Authorization policies must support normalized role/scope/permission evaluation across Entra, Auth0/Okta, and ADFS-compatible claims.
- MCP downstream token acquisition must use capability-aware configuration and fail with explicit errors when a provider does not support a requested grant flow.
- New provider support must include deterministic startup validation and clear diagnostics for missing authority, audiences, issuers, and grant settings.

## Safety requirements

- Avoid destructive actions outside the target site and known safe defaults.
- Any automatic binding cleanup must be limited to the exact desired binding and default IIS site only.
- Scripts must emit clear status messages for binding removals and failure conditions.