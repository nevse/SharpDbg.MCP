# Releasing

A release is a git tag and a GitHub Release. The `package` job in `ci.yml` does the rest: it reads
the version from the tag, stamps it into `.mcp/server.json`, packs, and pushes to nuget.org.

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

3. Create the GitHub Release for that tag. **Both workflows trigger on the release, not the tag**, so
   a pushed tag alone publishes nothing.
4. Watch two things. `ci.yml`'s `Package` job, which runs only after the unit and integration jobs
   pass and does the NuGet push; and `publish-registry.yml`, which starts at the same time and spends
   most of its run waiting for nuget.org before it updates the registry.

The push uses `--skip-duplicate`, so re-running a release that already published is harmless.

## The MCP Registry

Automatic since 0.1.1: `publish-registry.yml` runs on the same release and needs nothing from you.
It authenticates with `mcp-publisher login github-oidc` — the same OIDC exchange the NuGet push uses,
so there is no second secret — and publishes `.mcp/server.json` with the version stamped in.

The one-time namespace claim is done. `io.github.nevse/dotnet-debugger-mcp` was published by hand on
15 August 2026 for 0.1.0, which established `io.github.nevse/*`. What the registry holds now:

```bash
curl -s "https://registry.modelcontextprotocol.io/v0/servers?search=io.github.nevse"
```

**Why it is a separate workflow rather than a step in `package`.** The registry proves we own the name
by reading the `mcp-name` marker out of the package README *on nuget.org*, so there is nothing to
verify until nuget has finished validating the push — several minutes for 0.1.1, and on a release the
`package` job is only pushing at about that moment. The workflow polls
`https://api.nuget.org/v3-flatcontainer/dotnetdebugger.mcp/<version>/readme` for up to 20 minutes, and
being its own workflow means it can sleep through that without holding up the package.

The marker itself is in `docs/nuget-readme.md`, which opens with
`<!-- mcp-name: io.github.nevse/dotnet-debugger-mcp -->`, matching `name` in
`src/SharpDbg.MCP/.mcp/server.json`. Changing either without the other breaks ownership verification.

### Re-publishing a version

For a release cut before this workflow existed, or a publish that failed and needs retrying:

```bash
gh workflow run publish-registry.yml -f version=0.1.1
```

The version must already be live on nuget.org — the workflow waits for it rather than creating it.
This is how 0.1.1 was brought up to date after the workflow was added.

Publishing by hand needs `mcp-publisher login github`, a browser device flow, and the real version
written into the manifest first: the committed `server.json` carries `0.0.0-dev`, and both CI paths
rewrite it. Prefer the dispatch above.
