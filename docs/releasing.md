# Publishing an Internal Beta

The source repository is [ModelTC/LightX2V-Windows-App](https://github.com/ModelTC/LightX2V-Windows-App). `main` contains buildable source. Models, environments, personal acceptance reports, and historical packages are not committed. A separate license has not yet been selected for the app's original code; third-party licenses are retained.

## Branch Builds

On a branch push or pull request, GitHub Actions validates the source and pinned setup payloads, runs Python tests, compiles the C# check projects, and builds a complete Windows x64 ZIP. Workflow artifacts are retained for 30 days; a branch build does not automatically publish a Release.

## Publishing a Version Tag

Update the project version and `docs/releases/v<version>.md`, complete code review and relevant hardware validation, then tag the committed code:

```powershell
git status --short
git tag -a v0.3.5 -m "First Windows internal beta v0.3.5"
git push origin main
git push origin v0.3.5
```

The tag must match the project version. Tag builds still use read-only permissions. The separate publishing job runs only for version tags and uses the temporary token automatically provided by GitHub; no personal access token is required.

Before publication, the workflow automatically verifies the ZIP's SHA-256, every file hash, and the source commit and clean state in `build-manifest.json`. It first creates a draft pre-release and uploads the app ZIP and `.sha256`, then makes the release public only after confirming that both assets have been uploaded. If a Release with the same name already exists, the workflow refuses to overwrite it so that distributed files are not replaced. Check the Actions logs after a failure. Existing drafts or published releases must be reviewed manually before retrying; do not blindly rerun the workflow to overwrite them.

The release page retains that version's assets permanently. The 30-day workflow artifact retention period does not affect Release downloads. The initial release uses the pre-release channel, is not marked Stable, and does not overwrite Latest on a rolling basis. The channel strategy can later be expanded to follow the Mac version.

## Hardware Acceptance

CI does not install graphics drivers or run GPU inference. Release notes must distinguish real image generation, off-screen/simulated checks, and incomplete hardware validation. See [testing.md](testing.md) for the first release's AMD validation scope and the CUDA acceptance status at that time.

Use `build.ps1` for manual builds on a development machine. Only packages built from clean, committed source matching the target version tag may be distributed as official internal betas. Do not upload temporary packages from the pre-migration workspace or QA archives containing personal paths to a Release.

## Recovering an Uploaded Draft

Drafts cannot be read through the published Release lookup-by-tag endpoint. The publishing helper locates a draft through the authenticated list endpoint, downloads and verifies both assets and the source commit associated with the tag again, then changes only the publication state. It does not replace assets or move tags.

If the uploads completed but the final step failed, manually run **Publish verified draft beta** in Actions and enter the version tag. Maintainers can also trigger recovery by pushing an explicitly named publishing branch such as `codex/publish-v0.3.5` from a commit containing the publishing script fix. This workflow accepts only an existing, complete draft. It refuses changes if the version is already public, an asset is missing, or validation fails.
