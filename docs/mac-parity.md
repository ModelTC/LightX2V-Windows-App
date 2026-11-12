# Windows / Mac parity

Fixed reference: ModelTC/LightX2V-Mac-App `65acaee0c063200651629cb21df7c46711687e65`.
Windows baseline: `88333e1` (v0.3.12). Mac source remains unchanged.

| Area | Reference / baseline evidence | Target and acceptance |
| --- | --- | --- |
| Initial setup | Mac GeneralSettingsView: three settings cards; Windows FirstRunWindow: four pages | Retain the mature Windows four-step first-run wizard following user feedback. General settings uses three compact cards. Preserve driver/environment/model preparation, cancellation and restart; no sample generation gate. |
| Workspace / welcome | Mac Views: 232 / flexible / 320 columns; Windows MainWindow already uses three columns | Match spacing, hierarchy, controls and disabled states; retain Windows captions. Review both languages and 100/150/200% display scaling. |
| Model selection | Mac ModelSelector: family then variant; Windows fixed text | Family / Standard choice. INT8 intentionally absent. Existing workspaces use Standard. |
| Image import | Mac InputImages maximumCount=8, 100 MB / 40 MP, batch rollback; Windows maximum=3, file picker only | One policy used by C# and Python. Picker, drop, paste and result-as-reference share atomic import. Test 1/3/8, ninth rejection and damaged mixed batch. |
| Composer | Mac PromptEditor: Enter send, Shift-Enter newline, IME handling; Windows Ctrl-Enter only | Match Enter semantics without submitting an active composition. Preserve ordinary text paste. |
| Creation parameters | Mac CreationSettings / WorkspaceStorage; Windows only saves generation requests | Persist per-creation settings and active creation; history switches restore settings. New creation uses Mac defaults. Preserve old histories. |
| Adaptive dimensions | Mac GenerationSnapshot / bridge; Windows MainViewModel.Images and request construction | Audit request semantics against native backend before adjusting; acceptance includes adaptive T2I/I2I with enhancement on/off. |
| Enhancement / results | Both have thinking, rewritten prompt, image actions and task logs | Match expand/collapse, copy, export, reveal, reuse and reference actions. Real PE-I2I multi-reference acceptance required. |
| History / logs / scrolling | Mac ConversationScrollAnchor; Windows unconditionally scrolls on content change | Preserve reading position during streaming; new submission and deliberate history selection scroll to the appropriate end. Language changes preserve draft and references. |
| Downloads / recovery | Existing Windows model cards, StateStore and setup services | Retain repair, resume, cancellation and diagnostics. Avoid GPU probing on every launch and weight hashing on ordinary startup. |
| Uninstall | Mac AppMaintenanceView / AppUninstaller; Windows absent | Same three groups. Build an exact plan, confirm, execute after exit with a helper. Protect shared/external/link targets and leave unchecked paths intact. |
| Release management | Mac rolling Latest and manually promoted Stable; Windows version tags | Verified Latest package and manual promotion without rebuilding. No Stable until hardware acceptance. CI compiles/verifies/publishes; behavioral tests run locally. |

## Evidence rules

Code comparison is not UI or GPU acceptance. Offscreen renders are not interactive desktop checks.
Use isolated directories for regressions and uninstall fixtures. Preserve original models, environments and creations.
AMD real generation and RTX user-assisted results must record version, commit, hardware and scenario.
No INT8 support or in-app update UI is included. Existing inference pins and resource planning stay unchanged unless a reproduced defect requires a separately verified fix.

## Visual references

The user's five Mac desktop screenshots supplement the pinned source baseline: four workspace states (both inspector sections expanded, both collapsed, only generation settings expanded, only model preparation expanded) and the compact general-settings dialog. Windows keeps its native caption controls. Match the four primary result actions, compact model cards with PE-T2I/PE-I2I labels, muted idle enhancement cards when enhancement is off, and configured/status badges in the three settings cards. Download guides share the Mac repository/command/link/copy layout; automatic source routing remains an additional Windows capability in a single-row source selector.

First-run refinement: after reviewing the Mac startup settings and environment installer, retain Windows' four-step initial setup. A configured workspace opens immediately; only genuinely missing environment/model resources return to setup. Finishing the base-model preparation selects Standard. The general-settings dialog remains separate, with environment discovery in its own window and no setup-assistant shortcut.

The v0.4.2 behavior and acceptance scope are recorded in [its release notes](releases/v0.4.2.md).
