# Changelog

## 0.5.4 — 2026-11-10

- Support WebDAV images and files through one streaming encrypted object, encrypted consent/ready manifests and receiver-side integrity validation.
- Default text polling to 2 seconds and attachment checks to 3 seconds; add saved 1–300 second settings on both clients.
- Fix Windows status-refresh flicker and clipped text; add rounded components, capsule switches and modern file dialogs.
- Use the Android Miuix theme for file sharing and consolidate transfer notifications.
- Add a Windows image auto-send switch and configurable global shortcut for uploading the current clipboard.
- Reduce Windows tray to send files, pause/resume and exit.
- Add Android status-notification channel control while preserving actionable transfer notifications.
- Open saved Android attachments from completion notifications; add Windows native open-file and show-in-folder buttons with locally authenticated links.
- Preserve existing pairing and local settings during upgrade. Internal 0.5.1–0.5.3 test builds were not published.

## 0.5.0 — Media and multiple configurations preview

- Add independent configuration switches and encrypted installation identities for target grouping.
- Send text/images once per confirmed device with alternative-route retry and persistent event deduplication.
- Add Android share-sheet images/files, Windows clipboard images, and receiver-approved multi-file transfer up to 100 MiB per file.
- Add encrypted 256 KiB chunks, authenticated manifests, streaming hashes, resumable route retries and ciphertext cleanup.
- Add Android image preview/save/discard notifications, Windows receive folder and optional Explorer send action.
- Migrate existing local configuration without requiring re-pairing; continue shipping a Windows installer.


## 0.4.0 — WebDAV preview

- Add WebDAV HTTPS/Basic transport to Windows and Android with encrypted per-device files.
- Add Windows WebDAV connection wizard and local QR configuration transfer.
- Preserve existing relay mode, content encryption, device names and replay protection.
- Document offline QR credentials, polling behavior and provider compatibility limits.

## 0.3.1 — First public preview

- Publish the initial Windows, Android and self-hosted relay source.
- Add Windows installer with bundled runtime, tray integration and startup settings.
- Provide end-to-end encrypted text synchronization and one-time QR pairing.
- Show device names, module/sync status and last synchronization time.
- Support local pairing removal and hiding the Android launcher icon.
- Replace deployment-specific text with public setup instructions and generic templates.

Initial-preview limitations applied to 0.3.1; see the current README for supported functionality.
