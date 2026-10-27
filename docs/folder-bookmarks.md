# Folder bookmarks

Available in **3.11.2-stream.14 (157)**.

In Browse, tap the **star** to bookmark the current folder. A filled star means it is saved; tap again to remove it. **Folder bookmarks**, the list/ribbon icon beside it, opens the saved list and is also available before you browse a peer. On narrower screens it may be in the overflow menu.

Tap a bookmark to fetch that peer's current shared folders and navigate to the saved path through the existing Browse flow. Existing login, reconnect, offline-peer, and missing-folder messages apply. Bookmarks do not cache folder contents or start playback. If a peer renames or stops sharing the folder, the saved address remains until you remove it; normal Browse may fall back to that peer's root and report that the location was not found.

Use **Manage bookmarks** or long-press a bookmark to open, rename, or remove it. Rename changes its display label only. The list shows the peer and path to distinguish matching folder names. You can also use **Bookmark this folder** in a folder's long-press menu in Browse, or in the menu at the top of a search-result folder.

Bookmarks can be created from an already-visible folder and managed without a network connection. They remain on this installation across restarts and account changes. Opening one requires login and a responsive peer. A folder whose current path cannot be resolved (such as an empty listing) has its toolbar star disabled; bookmarking that directory from its parent folder's menu still works.

## Persistence

- Versioned `files/folder-bookmarks-v1.json`, with an atomic temporary write and the previous committed `.bak` as a recovery copy. Partial `.tmp` data is never loaded.
- Exact peer/path retained for requests. Duplicate detection compares peer and path case-sensitively, treating slash direction and trailing separators as equivalent. Adding an existing address preserves its label and ID.
- Newest bookmarks appear first. Stable IDs identify rename/remove operations; callers receive copies rather than mutable store entries.
- Maximum 500 bookmarks and a conservative 512 KiB encoded-size budget; names are 1–200 characters. Invalid replacements do not change the committed list.
- Missing/corrupt/unsupported/oversized data is ignored; a valid backup is used if available. No credentials, file contents, or playback state are stored.

Host tests cover persistence, Unicode, identity, duplicates, root addresses, renaming/removal, invalid data, storage limits, interrupted writes, backup recovery, and isolation from caller mutations. Android compilation/resources and signing are checked in CI. Device checks: review toolbar state, save a folder, reopen the app, rename/remove it, bookmark from search, and exercise an offline or renamed peer folder.
