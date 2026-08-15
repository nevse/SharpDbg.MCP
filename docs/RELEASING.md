# Releasing

A release is a git tag and a GitHub Release. The `package` job in `ci.yml` does the rest: it reads
the version from the tag, stamps it into `.mcp/server.json`, packs, and pushes to nuget.org.

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

3. Create the GitHub Release for that tag. **The workflow triggers on the release, not the tag**, so
   a pushed tag alone publishes nothing.
4. Watch the `Package` job. It runs only after the unit and integration jobs pass.

The push uses `--skip-duplicate`, so re-running a release that already published is harmless.

## After the first release: the MCP Registry

Only needed once the package is live on nuget.org, because the registry verifies that the package
exists and that its README declares a matching `mcp-name`. Both are already in place:
`docs/nuget-readme.md` opens with `<!-- mcp-name: io.github.nevse/dotnet-debugger-mcp -->`, matching
`name` in `src/SharpDbg.MCP/.mcp/server.json`.

Wait for nuget.org to finish validating the package — its README has to be reachable at
`https://api.nuget.org/v3-flatcontainer/dotnetdebugger.mcp/<version>/readme` — then:

```bash
# from https://github.com/modelcontextprotocol/registry/releases/latest
./mcp-publisher login github
./mcp-publisher publish src/SharpDbg.MCP/.mcp/server.json
```

The first publish claims the `io.github.nevse/*` namespace, which is why it is worth doing by hand
and watching, rather than adding a step nobody has seen run. Automating it afterwards uses the same
GitHub OIDC the NuGet push already relies on.

Note that the committed `server.json` carries `0.0.0-dev`; CI rewrites it at pack time. Publishing to
the registry by hand means passing the real version — edit the file for that run, or take the copy
from inside the published `.nupkg`.
