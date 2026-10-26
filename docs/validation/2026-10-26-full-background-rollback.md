# Full background-playback rollback — October 26, 2026

`3.11.2-stream.8-rollback` (version code 151) restores the tracked repository to
`3a1aea9` (Withhold playback metadata until tag reading completes), apart from this
report and the packaging identity described below. No Git history is rewritten.

This is the baseline immediately before the four background-playback changes:

- Buffering/retry presentation (`cd94f91`).
- Bounded automatic reconnect recovery (`c372bd5`).
- Saved queue restoration (`fcd3a8c`).
- Saved playback positions (`63cda61`).

The later battery optimization and subsequent startup compatibility/error-screen
changes are also absent. The earlier queue controls, bounded seeking, parsed tag
metadata gating and dependency alignment remain exactly as they were at `3a1aea9`.

The downloaded `stream.6` and `stream.7` APKs have different signing certificate
fingerprints. A same-package update between them cannot succeed; a rejected update
could leave an earlier installed version in place. This is an installation issue,
not a confirmed explanation of the crash.

To permit testing without uninstalling the original app, this rollback copy uses
package ID `com.companyname.andriodapp1.rollback` and application/launcher label
`Noiser rollback`. Version name/code, manifest package/application label and the
MainActivity label attribute are the only app-source differences from `3a1aea9`.

This separate app starts with its own fresh settings. The original app and its
private data remain in their existing package; this copy cannot read them. It
adds no data clearing, import or migration. It retains the baseline's normal
transient-cache cleanup inside its own package.

This is a controlled baseline test, not a confirmed diagnosis of the reported
Samsung Galaxy A07 / Android 16 crash. Earlier emulator success did not reproduce
the phone's failure.

To verify the source baseline:

```sh
git diff 3a1aea9 -- . ':!Seeker/Properties/AndroidManifest.xml' \
  ':!Seeker/Main/MainActivity.cs' \
  ':!docs/validation/2026-10-26-full-background-rollback.md'
```

That diff must be empty. The MainActivity change must contain only its display
label, and the manifest change only the packaging metadata listed above.

Validation before packaging:

- Baseline diff is empty outside the declared metadata/report exceptions.
- Normalizing the MainActivity display label gives a byte-identical baseline file;
  normalizing the manifest identity/version attributes gives an identical XML tree.
- Release host suite (`TestCategory!=RealData`): **716 passed, 1 skipped, 0 failed**.
- Generated Media3 callback contracts and mediaPlayback service manifest checks pass.
- Generated provider authorities use the new rollback package, avoiding conflicts
  with the original app's providers.
