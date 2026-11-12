# Maintenance requirements

- Run relevant behavioral and interaction checks locally (including the Windows AMD host), then commit and push each verified batch. Use isolated workspaces; preserve existing user data and security settings.
- GitHub CI must compile, verify the complete package and pinned payloads, and publish Latest. Do not run behavioral tests, install their dependencies, or add test switches to GitHub builds. Keep local regression suites maintained.
- Wait for the build and Latest publication corresponding to the pushed commit. Report the exact source commit, direct download and build results; never present an older package as the result of current changes.
- Latest and Stable each retain one current package at a fixed download URL. Stable is a manual promotion of verified Latest bytes, without rebuilding or changing Latest. Do not promote before AMD and RTX acceptance is complete.
- Keep README content to downloads, build status and source build instructions. Public release text is English.
- Use localization resources consistently. Language preference is independent of the workspace; changing language preserves drafts, references and tasks.
- App-authored log file text is English. Preserve user input, model output and third-party logs in their original form.
- Mac parity is pinned to `65acaee0c063200651629cb21df7c46711687e65`. INT8 and in-app updates are deferred. Do not upgrade inference pins as part of UI work.

- Retain the existing Windows four-step first-run wizard. The three compact settings cards and provided Mac desktop screenshots guide general settings and workspace visuals; do not replace the mature Windows preparation flow with the daily settings dialog.

- Inference source is fixed to official LightX2V 0.6.1 (`43c6a11bb65b7357f06e9603fa92249bc16b484e`) plus the reviewable common/Qwen Windows patch. Both backend profiles must share this source; never resolve a moving branch at build or runtime.
- No current preferences means first-time setup. Do not infer configuration from a developer directory or load workspace history before explicit workspace confirmation. Do not silently import legacy preferences or expose legacy import in the first-run wizard.
- The public ZIP has only LightX2V.exe, app and docs at its top level. Keep layout ownership centralized and maintain both legacy package verification and manifest-only uninstall.
