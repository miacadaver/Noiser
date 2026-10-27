# Folder bookmarks validation — 2026-10-27

**3.11.2-stream.14 (157)**, merged in [PR #5](https://github.com/miacadaver/Noiser/pull/5), commit `48b4d5a647ceab356148e05a5a4eacb75bab34fc`.

- [Main Android build and publication](https://github.com/miacadaver/Noiser/actions/runs/40478149635) passed. Its publication gate reported **784 host tests passed, 1 skipped**, including **20 bookmark cases**. Local host tests and the 45 XML/theme contrast checks also passed.
- APK signing certificate remains `d30cc162e830346498ad2f111a20bc29eede5a224ca1ab4423369e23ccf840ba`; package ID is unchanged.
- [Published release](https://github.com/miacadaver/Noiser/releases/tag/noiser-3.11.2-stream.14) includes the APK, update metadata, and public signing fingerprint.
- A temporary external console harness used the actual `Common.Updates.GithubUpdateClient`: installed code **156** detected **157**, downloaded **180,249,602 bytes**, and verified SHA-256 `ca95d35556e5a6dd3b0b26c03fd1002708be1d5939609125d7aa65fecd87b10f`. Installed code **157** correctly reported no newer update. No GitHub authentication was used for the check/download.

Device checks remain pending: review toolbar and list rendering; add a folder, restart, and reopen it; rename/remove saved entries; bookmark from search and a folder context menu; manage while offline; check an offline peer or a moved folder. The host smoke check does not verify Android's permission/installer flow. Existing playback and paused queue recovery code were not changed.
