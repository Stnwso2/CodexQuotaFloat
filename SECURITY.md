# Security Policy

## Supported versions

Only the latest release is supported with security updates.

## Reporting a vulnerability

Please use GitHub's [private vulnerability reporting](https://github.com/Stnwso2/CodexQuotaFloat/security/advisories/new). Do not include Codex access tokens, account identifiers, or the contents of `.codex/auth.json` in a public issue.

The application reads Codex authentication data locally and sends quota requests only to the fixed HTTPS endpoint declared in `Program.cs`. It does not write authentication data to its settings file.
