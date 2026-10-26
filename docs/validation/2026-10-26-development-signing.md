# Persistent development signing — October 26, 2026

The repository owner configured `NOISER_DEBUG_KEYSTORE_BASE64`. PR #1 was merged, activating a pinned development certificate for main-branch APKs. The app package remains `com.companyname.andriodapp1.rollback`; no playback or transfer logic changed.

## Verified APK

[APK build 40381777342](https://github.com/miacadaver/Noiser/actions/runs/40381777342) passed for commit `945a9ee04e2c10bb9681c5cb54e812c97981572c`. It published **Noiser-apk**, containing **Noiser.apk** and **signing-certificate.sha256**.

Both the restored keystore and the final APK matched this public certificate SHA-256:

```text
d30cc162e830346498ad2f111a20bc29eede5a224ca1ab4423369e23ccf840ba
```

The matching [host-test run](https://github.com/miacadaver/Noiser/actions/runs/40381777377) passed. All 15 signing-helper tests passed locally and in the APK workflow. These cover repeated independent key restoration, private file permissions, missing/malformed secrets, overwrite prevention, changed certificates, and APK publication rejection.

## Verification correction

The initial main build compiled but was withheld because the certificate parser recognized only older `Signer #1` output. Current Android build-tools use additional signature-scheme and SDK-range labels. The parser was extended to recognize those labels and repeated occurrences of the same certificate while rejecting a different app certificate in any reported scheme. Source-stamp certificates and public-key digests do not substitute for the app certificate. The successful final APK verification above confirms the corrected path.

## Remaining acceptance checks

- Confirm the same APK certificate on a second fresh CI runner.
- Install successive same-certificate builds on the physical phone and verify settings remain intact.

Older disposable-key APKs may need a one-time uninstall before adopting this identity. Export client data and preserve it outside app-private storage before uninstalling. See [signing setup, backup, and transition guidance](../development-signing.md).

The integration can push changes but still cannot list/create repository secrets or dispatch workflows directly. Secret management remains an owner operation. Pull-request APKs deliberately use disposable validation keys and are not offered as compatible main-branch updates.
