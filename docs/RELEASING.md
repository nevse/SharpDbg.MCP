# Releasing

A release is a git tag and a GitHub Release. `ci.yml` does the rest in two jobs: `package` reads the
version from the tag, stamps it into `.mcp/server.json`, packs and pushes to nuget.org; `registry`
then publishes that same manifest to the MCP Registry.

The package carries the debug adapter, built from the `external/clrdbg` submodule during the pack, so
every job in `ci.yml` checks the repository out with `submodules: true`. A checkout without them fails
the build rather than shipping a package with no debugger in it.

Nothing here holds a NuGet API key. Publishing uses
[trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing): the
workflow asks GitHub for an OIDC token, nuget.org validates it against a policy naming this
repository, and issues a key that lives one hour.

## One-time setup on nuget.org

Sign in, then **your username → Trusted Publishing**, and add a policy:

| field | value |
|---|---|
| Repository Owner | `nevse` |
| Repository | `dotnet-debugger-mcp` |
| Workflow File | `ci.yml` — the file name only, no `.github/workflows/` prefix |
| Environment | leave empty; the workflow uses no GitHub environment |

The policy must allow **creating new package IDs**, not only new versions of existing ones.
`DotnetDebugger.Mcp` does not exist on nuget.org until the first release, so a policy restricted to
existing packages cannot make it.

Then add one repository secret, **`NUGET_USER`**: your nuget.org *profile name*, not the email you
sign in with. It is not sensitive; it lives in a secret because that is what NuGet's own example does
and it keeps the workflow free of a personal detail.

A policy for a private repository starts out active for seven days only, and becomes permanent on the
first successful publish — that push is what gives nuget.org the immutable GitHub owner and repository
ids that pin the policy against someone deleting the repository and recreating it under the same name.
This repository is public, so that window should not apply, but the status is visible in the same UI
if a publish is ever refused for no other apparent reason.

## Cutting a release

1. Decide the version. The tag carries a leading `v`; the package version is the tag without it, so
   `v0.1.0` publishes `0.1.0`.
2. Tag and push:

   ```bash
   git tag v0.1.0
   git push origin v0.1.0
   ```

3. Create the GitHub Release for that tag. **`ci.yml` publishes on the release, not on the tag**, so
   a pushed tag alone publishes nothing.
4. Watch `ci.yml`. `Package` runs after the unit and integration jobs pass and does the NuGet push;
   `MCP Registry` runs after it and spends most of its time waiting for nuget.org to finish
   validating that push.

The push uses `--skip-duplicate`, so re-running a release that already published is harmless.

## The MCP Registry

Automatic since 0.1.1, and part of `ci.yml` since 0.3.0: the `registry` job needs nothing from you.
It authenticates with `mcp-publisher login github-oidc` — the same OIDC exchange the NuGet push uses,
so there is no second secret — and publishes the very `server.json` the `package` job stamped and
packed, handed over as a build artifact rather than stamped a second time. The registry entry and the
package therefore cannot claim different versions.

The one-time namespace claim is done. `io.github.nevse/dotnet-debugger-mcp` was published by hand on
15 August 2026 for 0.1.0, which established `io.github.nevse/*`. What the registry holds now:

```bash
curl -s "https://registry.modelcontextprotocol.io/v0/servers?search=io.github.nevse"
```

**Why it waits, and why it waits after the push rather than beside it.** The registry proves we own
the name by reading the `mcp-name` marker out of the package README *on nuget.org*, so there is
nothing to verify until nuget has finished validating the push — several minutes for 0.1.1. The job
polls `https://api.nuget.org/v3-flatcontainer/dotnetdebugger.mcp/<version>/readme` for up to 20
minutes against a 25-minute job timeout.

It used to be a workflow of its own, triggered by the same release, so it started polling while
`ci.yml` was still building and spent part of that budget on a package that had not been pushed yet.
That is what cancelled the 0.2.0 registry run on its timeout while the README was still on its way.
`needs: [package]` costs nothing — the polling never made nuget any faster — and the timeout now
covers nuget's validation alone.

The marker itself is in `docs/nuget-readme.md`, which opens with
`<!-- mcp-name: io.github.nevse/dotnet-debugger-mcp -->`, matching `name` in
`src/SharpDbg.MCP/.mcp/server.json`. Changing either without the other breaks ownership verification.

### Re-publishing a version

`publish-registry.yml` is kept for exactly this, and no longer triggers on a release. Use it for a
version already on nuget.org whose registry entry is missing or stale — a `registry` job that failed,
or a release cut before any of this existed:

```bash
gh workflow run publish-registry.yml -f version=0.1.1
```

The version must already be live on nuget.org — the workflow waits for it rather than creating it.
This is how 0.1.1 was brought up to date after the workflow was added.

Publishing by hand needs `mcp-publisher login github`, a browser device flow, and the real version
written into the manifest first: the committed `server.json` carries `0.0.0-dev`, and both CI paths
rewrite it. Prefer the dispatch above.
