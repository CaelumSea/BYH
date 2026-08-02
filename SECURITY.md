# Security Policy

## Reporting a Vulnerability

BYH handles LLM provider credentials (OpenAI-compatible API keys for
translation / OCR). If you find a way that a key, secret, or sensitive user
data could leak, **please do not open a public issue**.

Instead, report it privately:

- Open a **private security advisory** via the GitHub
  ["Security" tab → "Report a vulnerability"](https://github.com/CaelumSea/BYH/security/advisories/new),
  **or**
- Email the maintainer directly.

Please include:

- A description of the issue and its potential impact
- Steps to reproduce (a minimal repro is ideal)
- The BYH version (`ProductVersion` from the exe properties)

You should get an initial response within **72 hours**. If the report is
accepted, we'll coordinate a disclosure timeline with you.

## What counts as a security issue

- API keys / secrets appearing in logs, error messages, URLs, or the
  clipboard-history store unencrypted
- A way to read another OS user's `%LOCALAPPDATA%\BYH\` data
- Crash or hook deadlock that another local process can trigger
- DPAPI or store corruption that loses user data

## What does NOT count

- Bugs in third-party LLM providers (report to them)
- A provider key you leaked by pasting it into a public issue (rotate it at
  the provider — BYH cannot revoke a key it doesn't hold in plaintext)
- Feature requests or general bugs — use the normal issue templates

## How BYH stores secrets (for context)

- API keys are written via `--set-secret secret://provider/{Id}` and stored
  **DPAPI-encrypted** (CurrentUser scope) at
  `%LOCALAPPDATA%\BYH\secrets\{sha256}.bin`.
- Keys are referenced by `secret://` URI in config JSON; they are **never**
  serialized inline into any `.json` file.
- The redacted logger scrubs `api_key=...`, `bearer ...`, and similar
  patterns to `[REDACTED]` before writing to disk.
