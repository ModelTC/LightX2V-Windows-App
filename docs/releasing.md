# Latest and Stable

Normal successful branch builds publish one non-expiring Windows x64 package at:

`https://github.com/ModelTC/LightX2V-Windows-App/releases/download/latest/LightX2V-APP-Windows-x64.zip`

Pull requests compile and validate packages without publishing. Both publishing and promotion share a concurrency lock. Package provenance requires a clean source checkout, exact source commit, complete per-file hashes, pinned runtime verification and a matching archive digest. New assets are uploaded and verified before replacing the current download. Older successful builds cannot overwrite a newer Latest run. Build artifacts are temporary publisher inputs; Release downloads do not expire.

Behavioral and interaction tests run locally with `scripts/run_local_checks.ps1`; use a fresh isolated report directory. GitHub CI only compiles, validates the complete package and pinned payloads, and publishes. It does not install test dependencies, run behavior tests or perform GPU acceptance.

Before pushing, run relevant regressions and review the diff. After pushing, wait for the corresponding build and Latest publication; verify the package's source commit, report the direct download and Actions result, and distinguish actual hardware evidence from fixtures.

## Promote Stable

There is no Windows Stable yet. Complete the Standard-model AMD and RTX scenarios in `testing.md`, resolve blocking issues, then manually run **Promote Latest to Stable**. The workflow verifies Latest's asset label, rolling tag, archive digest and internal file manifest, and publishes those exact bytes as Stable. It does not rebuild or modify Latest. Do not run promotion before hardware acceptance. The first promotion enables the Stable download in both READMEs.

After publishing a verified package, the publisher records the source commit and package upload time (UTC+8) in the Release notes and both README download tables. Stable retains the original Latest package date. The README rows are committed together on `main`; the ZIP includes the standalone documentation rather than a snapshot of the rolling download tables. If the README update fails, the verified package stays published and the workflow reports the error. Rerun publication to repair the tables.

Each channel retains one package. The repository uses the rolling `latest` tag and adds `stable` at the first promotion. Build and release history are recorded in Git commits and Release notes. Public release text is English. In-app update checking remains deferred.
