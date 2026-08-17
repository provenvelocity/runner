# Allow `uses:` to specify a custom URL, overriding the derived host

**Date**: 2026-08-17

**Status**: Accepted

*This is a fork-only ADR (not part of upstream `actions/runner`). Named without a numeric
prefix so it never collides with upstream's sequentially-numbered ADRs during future syncs.*

## Context

Normally a step's `uses:` value only supports:

- `owner/repo[/path]@ref` — resolved against the runner's configured server (github.com or
  the connected GHES instance) via a server-side resolve call
  (`ResolveActionsDownloadInfoAsync` / `ResolveActionDownloadInfoAsync`), which returns a
  signed download URL and token.
- `./path` — a local action in the same repository.
- `docker://image` — a container action.
- `$/path` — a self-repository reference (workflow repo).

There was no way to reference an action hosted on a different server (for example, a
different GHES instance) than the one the runner is registered against. Every action
reference was always resolved against the same "derived" host.

## Decision

`uses:` may now also be a fully-qualified URL:

```yaml
steps:
  - uses: https://ghes.example.com/owner/repo@v1
```

When `uses:` is an absolute `http`/`https` URL, the runner:

1. Parses `{scheme}://{host}[:port]` as the action's source host, and the URL path as
   `{owner}/{repo}[/path]@ref` (same shape as the normal form).
2. Skips the server-side resolve call entirely for this action — no
   `ResolveActionsDownloadInfoAsync`/`ResolveActionDownloadInfoAsync` request is made.
3. Builds the tarball/zipball download URL directly, using the same github.com-vs-GHES
   convention already used everywhere else in the runner (`UrlUtil.IsHostedServer`):
   `api.{host}` for a dotcom-style host, `{host}/api/v3` for a GHES-style host.
4. Reuses the job's own `GITHUB_TOKEN` as the auth token for the request (there's no
   per-action token issuance for a host outside the runner's configured server).
5. Namespaces the on-disk action cache directory by source host, so the same
   `owner/repo@ref` downloaded from two different hosts can't collide.

All other `uses:` forms (`owner/repo@ref`, `./local`, `docker://`, `$/self`) are
completely unaffected and continue to resolve against the runner's normally derived
server.

This composes with the existing `${{ }}` expression support in `uses:` — the URL can
itself be produced by an expression, e.g. `uses: ${{ vars.ACTION_URL }}`.

## Consequences

- Workflows can reference actions hosted on a server other than the one the runner is
  registered against, without a service-side resolve call to that server.
- Because there's no server-side resolve, there's no ref-to-SHA resolution, no repo/org
  policy enforcement, and no immutable-action verification for these actions — the ref
  given in `uses:` is trusted and used as-is. This matches the existing trust model of
  `uses:` (arbitrary code execution from wherever it points), so it's not a new class of
  risk, but it's a different resolution path with fewer safety nets than the normal one.
- The custom host must serve GitHub-REST-API-compatible tarball/zipball endpoints
  (`/repos/{owner}/{repo}/tarball/{ref}`, optionally under `/api/v3`); a plain
  git-clonable-only host is not supported by this implementation.
- `GITHUB_TOKEN` is sent to whatever host is named in `uses:`. Workflow authors should
  only point `uses:` at hosts they trust with that token.
