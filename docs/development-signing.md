# Persistent development signing

The proposed signing workflow reuses one private development key for `main` APKs. It pins the public certificate in [`.github/noiser-development-certificate.sha256`](../.github/noiser-development-certificate.sha256), checks the restored key, and verifies the final APK with Android's `apksigner` before uploading `Noiser-apk`.

**Activation is pending:** the current GitHub integration cannot create repository Actions secrets (`403: Resource not accessible by integration`). The workflow change must not replace the working `main` workflow until the secret below is configured.

## One-time setup

A private key and a base64 upload payload have been generated in this workspace under `.signing/`. This directory is ignored by Git and must never be committed. Keep an offline backup of `noiser-development.jks`; losing it prevents future compatible updates. The key uses Android's standard development alias `androiddebugkey` and password `android`. Its private bytes, rather than that conventional password, are the signing credential.

1. Open [Noiser → Settings → Secrets and variables → Actions](https://github.com/miacadaver/Noiser/settings/secrets/actions).
2. Create a **repository secret** named `NOISER_DEBUG_KEYSTORE_BASE64`.
3. Use the contents of `.signing/noiser-development.base64` as its value. Keep this value out of issues, commits, chat, and build logs.
4. Activate the reviewed signing workflow on `main` and let the APK build complete.

Alternatively, from an owner-authenticated terminal in this workspace:

```sh
gh secret set NOISER_DEBUG_KEYSTORE_BASE64 --repo miacadaver/Noiser < .signing/noiser-development.base64
```

GitHub encrypts secrets and does not let you download their values later. Back up the original keystore separately. Do not regenerate a key for each build, rotate it casually, or overwrite an existing signing secret with a newly generated key.

## Update compatibility

The package remains `com.companyname.andriodapp1.rollback`. Android requires a compatible signing identity as well as an acceptable version code to install an update. Older CI APKs used disposable runner keys; this newly generated key **cannot** update those installations if their certificates differ. There is no recovered private key for the phone-confirmed rollback.

Before transitioning an existing installation, export client data through **Settings → Export Client Data** and retain the export outside app-private storage. An uninstall may be required for the first persistent-key APK and removes private app data; do not uninstall without preserving what you need. Subsequent APKs signed by this key can update one another, subject to version and Android installation requirements. Rebuilding unchanged source may keep the same version code; published version upgrades should increase the manifest version code.

## Pull requests and other branches

Only non-pull-request runs on `main` receive the persistent key. Pull requests and manual builds on other branches use a disposable key and publish **Noiser-validation-apk**, containing `Noiser-validation.apk`. These are validation artifacts, not compatible updates for an installed persistent-key build. The workflow uses `pull_request`, never `pull_request_target`.

Missing, malformed, or changed keys fail the `main` build before compilation; there is no silent fallback to a new random identity. The uploaded main artifact also contains `signing-certificate.sha256`. Tagged release signing remains separate and uses its existing release secrets; release APKs are not promised to update development APKs.

## Local builds with the same identity

Use the backed-up private key explicitly:

```sh
dotnet build Seeker/Seeker.csproj --configuration Debug \
  -p:AndroidKeyStore=true \
  -p:AndroidSigningKeyStore=/secure/path/noiser-development.jks \
  -p:AndroidSigningKeyAlias=androiddebugkey \
  -p:AndroidSigningKeyPass=android \
  -p:AndroidSigningStorePass=android \
  -p:EmbedAssembliesIntoApk=true
```

Ordinary local `AndroidKeyStore=false` builds still use the developer machine's default debug key and are not update-compatible unless that key is deliberately the same identity.

## Verification

```sh
python3 -m unittest discover -s .github/scripts -p 'test_development_signing.py'
python3 .github/scripts/development-signing.py verify-apk --apk /path/to/Noiser.apk
```

The tests use disposable fixture keys and cover identical certificates across fresh runner restores, missing/malformed secrets, certificate mismatch, private file permissions, overwrite refusal, and rejecting invalid or incorrectly signed APKs. Verifying a real APK additionally requires Android SDK build-tools (`apksigner`). A physical-device update retaining settings remains an acceptance check.

References: [GitHub Actions secrets](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-secrets), [Microsoft Android signing properties](https://learn.microsoft.com/en/dotnet/android/building-apps/build-properties#androidkeystore).
