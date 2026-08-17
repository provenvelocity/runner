# This fork: what it is and how to use it

**Repo**: `provenvelocity/runner` (fork of [`actions/runner`](https://github.com/actions/runner))
**Branch**: `feature/evaluate-uses-expressions`
**Fork version marker**: builds from this branch report `2.336.0-provenvelocity` via
`Runner.Listener --version` / logs, distinguishing them from official `actions/runner` releases
(see [src/runnerversion](../src/runnerversion)).

This is a one-way fork: changes are pulled *from* `actions/runner` (`origin`) into this branch, and
pushed *to* `provenvelocity/runner` (`fork`) and, for testing, `kaiser.ghe.com/actions/runner`
(`kaiser`). Nothing from this branch is intended to go back upstream.

## What this fork adds on top of upstream `actions/runner`

1. **Expression evaluation in `uses:`** — `uses: ${{ vars.ACTION_REF }}` and similar are now
   evaluated at runtime, gated by the `actions_evaluate_uses_expressions` feature flag
   (`Runner.Common/Constants.cs`). Originally added in commits `0804d342`/`ef36daec`.
2. **Custom URL in `uses:`** — `uses: https://<host>/<owner>/<repo>[/path]@ref` lets a step
   reference an action hosted on a server other than the one the runner is registered against,
   bypassing the normal server-side resolve call and downloading directly from that host.
   See [docs/adrs/fork-0001-custom-uses-url.md](adrs/fork-0001-custom-uses-url.md).
3. **Secure cross-host authentication** for the feature above — a default-deny allow-list of
   hosts, each backed by a GitHub App, so a token is only ever attached to a request when the
   target host is explicitly trusted (never the primary `GITHUB_TOKEN` forwarded to an arbitrary
   host). See [docs/feature/secure_app_id.md](feature/secure_app_id.md) for the setup guide, or
   [docs/feature/secure_app_id_listener_option.md](feature/secure_app_id_listener_option.md) for a
   stronger-isolation alternative that isn't implemented yet.
4. **Automated upstream sync** — `.github/workflows/sync-upstream.yml` periodically merges
   `actions/runner`'s `main` (or latest stable tag, depending on the version of that workflow) into
   this branch, so the fork doesn't drift far behind upstream security/bug fixes.

## Quick links

| Question | See |
| --- | --- |
| Why does `uses:` support a custom URL, and how does resolution/caching work? | [docs/adrs/fork-0001-custom-uses-url.md](adrs/fork-0001-custom-uses-url.md) |
| How do I set up a GitHub App so a custom `uses:` host gets authenticated downloads? | [docs/feature/secure_app_id.md](feature/secure_app_id.md) |
| How do I build/test this fork locally? | [docs/contribute.md](contribute.md) (unchanged from upstream — `./dev.sh layout && ./dev.sh build && ./dev.sh test`) |
| How do I point ARC (Actions Runner Controller) at a container image built from this fork? | Override `template.spec.containers[0].image` in the `gha-runner-scale-set` Helm chart's `values.yaml` to your custom-built image (built from this repo's [images/Dockerfile](../images/Dockerfile)). ARC's own listener/controller image is unrelated and does not need to change. |
| Is `Runner.Listener` the same as "ARC's listener"? | No — see the "Process architecture context" section of [docs/feature/secure_app_id.md](feature/secure_app_id.md) for the full breakdown. |

## Using the features in a workflow

```yaml
steps:
  # Normal action reference, unaffected by anything in this fork
  - uses: actions/checkout@v4

  # Expression evaluated at runtime (feature flag actions_evaluate_uses_expressions)
  - uses: ${{ vars.CUSTOM_ACTION_REF }}

  # Custom host, authenticated via an allow-listed GitHub App if configured
  # (see docs/feature/secure_app_id.md), otherwise an anonymous request
  - uses: https://github.kp.org/owner/some-action@v1
```

## Keeping this doc current

When adding a new fork-specific feature, add a one-line entry to the table above and link to its
detailed doc (an ADR under `docs/adrs/fork-NNNN-*.md` for a design decision, or a guide under
`docs/feature/*.md` for something with setup steps) rather than growing this file into the full
design doc itself.
