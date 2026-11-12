# Latest and Stable

Normal successful branch/tag builds publish one non-expiring Windows x64 package at:

`https://github.com/ModelTC/LightX2V-Windows-App/releases/download/latest/LightX2V-APP-Windows-x64.zip`

Pull requests compile and validate packages without publishing. Both publishing and promotion share a concurrency lock. Package provenance requires a clean source checkout, exact source commit, complete per-file hashes, pinned runtime verification and a matching archive digest. New assets are uploaded and verified before replacing the current download. Older successful builds cannot overwrite a newer Latest run. Build artifacts are temporary publisher inputs; Release downloads do not expire.

Behavioral and interaction tests run locally with `scripts/run_local_checks.ps1`; use a fresh isolated report directory. GitHub CI only compiles, validates the complete package and pinned payloads, and publishes. It does not install test dependencies, run behavior tests or perform GPU acceptance.

Before pushing, run relevant regressions and review the diff. After pushing, wait for the corresponding build and Latest publication; verify the package's source commit, report the direct download and Actions result, and distinguish actual hardware evidence from fixtures.

## Promote Stable

There is no Windows Stable yet. Complete the Standard-model AMD and RTX scenarios in `testing.md`, resolve blocking issues, then manually run **Promote Latest to Stable**. The workflow verifies Latest's asset label, rolling tag, archive digest and internal file manifest, and publishes those exact bytes as Stable. It does not rebuild or modify Latest. Do not run promotion before hardware acceptance. Update the README's Stable availability when publishing the first Stable.

Each channel retains one package. Source history and old versioned beta releases remain intact during migration. The former version-tag publishing workflow is retired; ordinary version tags now build Latest. Public release text is English. In-app update checking remains deferred.
