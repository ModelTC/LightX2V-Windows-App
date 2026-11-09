# Publishing an Internal Beta

The source repository is [ModelTC/LightX2V-Windows-App](https://github.com/ModelTC/LightX2V-Windows-App). `main` contains buildable source. Models, environments, personal acceptance reports, and historical packages are not committed. A separate license has not yet been selected for the app's original code; third-party licenses are retained.

## Branch Builds

On a branch push or pull request, GitHub Actions validates the source and pinned setup payloads, runs Python tests, runs the Windows regression suite, and builds a complete Windows x64 ZIP. Workflow artifacts are retained for 30 days; a branch build does not automatically publish a Release.

## Publishing a Version Tag

Update the project version and `docs/releases/v<version>.md`, complete code review and relevant hardware validation, then tag the committed code:

```powershell
$version = ([xml](Get-Content LightX2V.Windows/LightX2V.Windows.csproj -Raw)).Project.PropertyGroup.Version
$tag = "v$version"
git status --short
git tag -a $tag -m "Windows beta $tag"
git push origin main
git push origin $tag
```

Write public release titles, notes, and repository metadata in English. Use absolute GitHub URLs for documentation links in release notes.

The tag must match the project version. Tag builds still use read-only permissions. The separate publishing job runs only for version tags and uses the temporary token automatically provided by GitHub; no personal access token is required.

Before publication, the workflow automatically verifies the ZIP's SHA-256, every file hash, and the source commit and clean state in `build-manifest.json`. It first creates a draft pre-release and uploads the app ZIP and `.sha256`, then makes the release public only after confirming that both assets have been uploaded. If a Release with the same name already exists, the workflow refuses to overwrite it so that distributed files are not replaced. Check the Actions logs after a failure. Existing drafts or published releases must be reviewed manually before retrying; do not blindly rerun the workflow to overwrite them.

The release page retains that version's assets permanently. The 30-day workflow artifact retention period does not affect Release downloads. Version tags use the pre-release channel and retain a download URL for each version.

## Hardware Acceptance

CI does not install graphics drivers or run GPU inference. Release notes must distinguish real image generation, off-screen/simulated checks, and incomplete hardware validation. See [testing.md](testing.md) for hardware acceptance and recorded results.

Use `build.ps1` for manual builds on a development machine. Only packages built from clean, committed source matching the target version tag may be distributed as official internal betas. Do not upload temporary packages from the pre-migration workspace or QA archives containing personal paths to a Release.

## Recovering an Uploaded Draft

Drafts cannot be read through the published Release lookup-by-tag endpoint. The publishing helper locates a draft through the authenticated list endpoint, downloads and verifies both assets and the source commit associated with the tag again, then changes only the publication state. It does not replace assets or move tags.

If the uploads completed but the final step failed, manually run **Publish verified draft beta** in Actions and enter the version tag. This workflow accepts only an existing, complete draft. It refuses changes if the version is already public, an asset is missing, or validation fails.
