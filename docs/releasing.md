# Releasing Drasi to nuget.org

Tagged releases (`vMAJOR.MINOR.PATCH`) pack the managed library plus every RID
native binary and publish to nuget.org with [Trusted
Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
(OIDC, no long-lived API key).

## Versioning

- SemVer 2.0. The package version is the tag without the `v` prefix.
- `0.x` may include breaking changes. `1.0.0` starts the stable contract.
- Set `<Version>` in `src/Drasi/Drasi.csproj` to the version you are about to
  tag so local packs match CI. The release workflow also passes `-p:Version=`
  from the tag.

## Changelog

`CHANGELOG.md` follows Keep a Changelog. Before tagging:

1. Move items from `## [Unreleased]` into `## [X.Y.Z] - YYYY-MM-DD`.
2. Add a compare link for the new version at the bottom of the file.
3. Commit that changelog on `main`.

## First-time nuget.org setup

Do this once, before the first `v*` tag.

1. Create a nuget.org account that can push under the Drasi org (or the owner
   that will list `Drasi`).
2. On nuget.org: username menu → **Trusted Publishing** → add a policy:
   - **Repository Owner:** `drasi-project`
   - **Repository:** `drasi-dotnet`
   - **Workflow File:** `native-binaries.yml` (file name only)
   - **Environment:** `nuget`
3. In this GitHub repo:
   - Settings → Variables → `NUGET_USER` = the nuget.org **username** (profile
     name, not email).
   - Settings → Environments → create `nuget` (optional reviewers/wait timer).

Until that policy exists, the publish job will fail at `NuGet/login`. Pack and
verify-load still run on every tag.

## Cutting a release

```bash
# on main, changelog already committed
git tag -a v0.1.0 -m "v0.1.0"
git push origin v0.1.0
```

The **Native binaries** workflow builds every RID, packs `Drasi.{version}.nupkg`
and `.snupkg`, verifies load without Rust, then (on a `v*` tag) logs in with
OIDC and pushes to nuget.org. It also creates a GitHub Release with the nupkgs
attached.

## Local pack (one RID, not for nuget.org)

```bash
./scripts/pack.sh osx-arm64
```
