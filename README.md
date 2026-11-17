# Jellyfin Auto Organizer

An open-source Jellyfin plugin for automatically organizing completed movies and series. Designed for Jellyfin on a Synology NAS, including Container Manager installations. It uses Jellyfin's plugin/configuration/scheduled-task interfaces from the [official template](https://github.com/jellyfin/jellyfin-plugin-template); it runs inside Jellyfin, with no separate organizer service or metadata account. The plugin's source is available under GPL-3.0.

This build targets **Jellyfin 10.11.5 / .NET 9**. Check your server version under Dashboard before installing. The DLLs are managed AnyCPU and have no native dependencies. They do not require a separate .NET installation on the NAS; Jellyfin supplies the runtime. Compatibility has been checked by compiling against Jellyfin 10.11.5; a live Synology/Jellyfin installation has not been tested here.

## What it does

Your incoming folder can contain loose media files or nested torrent-style folders, including open movies and series distributed through torrents. These examples use Blender's open-movie projects [Big Buck Bunny](https://peach.blender.org/about/) and [Caminandes](https://www.caminandes.com/). The Caminandes shorts are grouped into an illustrative season to demonstrate series naming; the episode labels are examples, not an official TV season.

```text
incoming/
├── Caminandes (2013) Season 1 S01 (1080p x265)/
│   ├── Season 1/
│   │   ├── Caminandes.S01E01.1080p.x265.mkv
│   │   └── Caminandes.S01E01.1080p.x265.en.srt
│   └── S01E02.mkv
└── Big.Buck.Bunny.2008.1080p.x265.mkv
```

The result is:

```text
library/
├── Shows/
│   └── Caminandes/
│       └── Season 01/
│           ├── Caminandes - S01E01.mkv
│           ├── Caminandes - S01E01.en.srt
│           └── Caminandes - S01E02.mkv
└── Movies/
    └── Big Buck Bunny (2008)/
        └── Big Buck Bunny (2008).mkv
```

The structure follows Jellyfin's [TV](https://jellyfin.org/docs/general/server/media/shows/) and [movie](https://jellyfin.org/docs/general/server/media/movies/) naming guidance. Optionally include series years to distinguish remakes. Season 00 is supported for numbered specials.

## Install from Jellyfin's plugin catalog

1. Open **Dashboard → Plugins → Repositories → Add** in Jellyfin.
2. Name it **Auto Organizer** and enter this repository URL:

   ```text
   https://raw.githubusercontent.com/dankrusi/Jellyfin-AutoOrganizer/main/manifest.json
   ```

3. Save, open the **Catalog** tab, and install **Auto Organizer**.
4. Restart Jellyfin, then open **Dashboard → Plugins → My Plugins → Auto Organizer**. Use **Browse folders** for incoming downloads and choose your existing Shows and Movies libraries from the dropdowns. Enable monitoring, keep **Preview only** on for the first run, and save.

The catalog build targets Jellyfin **10.11.5**. Jellyfin filters out releases targeting a newer server version. The plugin cannot create container bind mounts or DSM permissions; follow the folder-mapping steps below before enabling imports. The repository supplies future tagged versions through Jellyfin's plugin update system.

Releases include a `-catalog.zip` whose DLLs are at the ZIP root for Jellyfin's installer, plus a manual-install ZIP containing a plugin folder. Use the manual ZIP only for the File Station procedure below.

## Manual install on Synology

No compilation is needed on the NAS. Use the ZIP in `artifacts/`, or a ZIP from the repository's Actions artifacts after running CI.

1. **Stop Jellyfin** in Container Manager.
2. Extract `AutoOrganizer-1.0.2-jellyfin-10.11.5.zip` on your computer or with File Station. It contains an `AutoOrganizer_1.0.2` folder with **both** DLLs.
3. Copy that whole folder into Jellyfin's persistent **plugins directory**. For the official `jellyfin/jellyfin` image with host `/volume1/docker/jellyfin/config` mapped to container `/config`, this is normally `/volume1/docker/jellyfin/config/plugins/AutoOrganizer_1.0.2/`. Locate the actual directory in your deployment; third-party packages/images can differ. Create `plugins` if necessary. Ensure Jellyfin's user can read both DLLs.
4. Map your completed downloads and media library into the Jellyfin container, if not already mapped. Example:

   | Synology host folder | Container folder | Access |
   | --- | --- | --- |
   | `/volume1/downloads/completed` | `/downloads/completed` | Read for Copy; read/write for Move |
   | `/volume1/media` | `/media` | Read/write |
   | `/volume1/docker/jellyfin/config` | `/config` | Read/write |

   Grant the container's user the corresponding DSM shared-folder/ACL permissions. These are examples; use your existing paths. Container paths and host paths are different. See Jellyfin's [container documentation](https://jellyfin.org/docs/general/installation/container/) for bind mounts.
5. Start Jellyfin. Open **Dashboard → Plugins → My Plugins → Auto Organizer**.
6. Use **Browse folders** to choose incoming `/downloads/completed`. Choose your existing Shows and Movies libraries from the dropdowns, or set shared library root `/media` to create `Shows` and `Movies` underneath. Enable monitoring, keep **Preview only** enabled, and save.
7. The task polls every minute and waits for five minutes of observed file stability. Review `AutoOrganizer Preview` lines in **Dashboard → Logs**. Under **Scheduled Tasks → Auto Organizer → Organize incoming downloads**, you can start a scan manually or change the interval. Repeated manual scans still honor the stability delay.
8. Once previews look correct, turn preview off. **Copy** is the default and keeps the torrent's originals for seeding. Choose **Move** only if the torrent client no longer needs the originals.
9. Add a **Shows** library for `/media/Shows` and a **Movies** library for `/media/Movies` under Dashboard → Libraries. Enable real-time monitoring on these libraries, or schedule scans. The organizer does not run a full Jellyfin library scan on every poll.

If you prefer SSH after extracting the archive, the optional installer copies the two DLLs into an existing plugins directory:

```sh
sh scripts/install-synology.sh /volume1/downloads/AutoOrganizer_1.0.2 /volume1/docker/jellyfin/config/plugins
```

Stop Jellyfin first and start it afterwards. For updates, move the old plugin folder **outside** the plugins directory before copying a replacement. Keep only one Auto Organizer installation. To uninstall, stop Jellyfin, remove its plugin folder, and restart. Organized media is unaffected.

## Choosing folders on Synology

Every folder field includes **Browse folders**, which lists server roots and their subfolders through Jellyfin's authenticated admin APIs. Open a folder, use **Parent folder** to go up, and choose **Use this folder**. You can also type a container path and select **Open path**. The browser selects existing server folders; it does not upload files, expose your computer's files, create folders, or add NAS bind mounts.

The **Existing Shows library** and **Existing Movies library** dropdowns read the libraries under **Dashboard → Libraries**. Each physical folder appears separately, filtered by library type; mixed libraries appear in both dropdowns. Selected folders receive media directly. For example, choosing Shows library `/media/TV` produces `/media/TV/Caminandes/Season 01`, without an extra `Shows` folder. Choosing Movies library `/films` produces `/films/Big Buck Bunny (2008)`. Jellyfin needs write permission for these destinations.

Both destinations have editable overrides and a browser. Alternatively, use **Shared library root** `/media` for `/media/Shows` and `/media/Movies`; each explicit destination overrides its part of the shared root. The shared root may be blank when both destinations are selected. Existing shared-root configurations continue to work after updating. Parent-folder suggestions are available in the shared-root input.

To find a container path, open **Container Manager → your Jellyfin container → Volume settings** and inspect the container side of the mapping. For example, Synology `/volume1/media` may map to container `/media`; the plugin uses `/media`. File Station displays the host side. If a folder is missing from the browser, mount it into the Jellyfin container and check DSM shared-folder/ACL permissions for the container user. Choose paths inside the container even when the downloader runs on the same NAS. Field descriptions include these instructions, preview/log locations, seeding behavior, the stability delay, and completion-marker setup.

## Download completion and torrent seeding

**Point the plugin at your downloader's completed folder**, with incomplete downloads stored elsewhere. Polling alone cannot distinguish a paused, preallocated, or stalled torrent from a finished download. The stability delay is an additional check, not proof of completion. Common `.part`, `.!qB`, `.!ut`, `.aria2`, `.partial`, `.crdownload`, `.download` and `.tmp` files block their top-level download folder from import.

For stricter gating, set **Completion marker** to `.completed` and have the downloader's completion hook create that file inside each top-level release folder. The marker's existence is an explicit assertion that the folder is complete. In marker mode, loose files at the incoming root are skipped. Keep the stability delay enabled as well.

Copy mode preserves torrent filenames and folder locations but uses additional disk space. Move mode also copies to a temporary file at the destination before publishing, so it works across mounted volumes, but requires space for the destination copy. No hardlink mode is included.

### Moving complete release folders

Move mode organizes recognized videos and matching subtitles/audio first. Once a top-level release folder has finished importing into a single movie or series, remaining files are preserved under `<organized title>/.download-extras/<original release folder>/`, keeping their original subfolder structure. This includes torrent notes, images, hidden/empty files, and recognized samples/extras. An empty `.ignore` file excludes the attachment tree from Jellyfin scans. After successful transfers, empty source subfolders and the release folder are removed; the incoming root is never removed. Copy mode leaves the original release untouched.

This also works between NAS volumes and can resume attachment transfers from saved import receipts after a restart. Unknown main videos, multiple destination titles within one release, partial/changing files, conflicts, symbolic links, locked files, or missing write/delete permission keep the source folder for review. Files are not discarded to force cleanup. Unmatched subtitles are preserved with attachments; automatic subtitles still require the video's matching filename stem as described below. Keep Move disabled for torrents you are still seeding. Torrent-index website prefixes followed by a separator are stripped from inferred media titles.

## Supported names and scope

- Episodes: `Show.Name.S01E02`, `Show Name 1x02`, contiguous `S01E01E02` / `S01E01-E02` / `S01E01-02`, and `1x01-1x02` ranges. Episode-only `S01E01` filenames inherit the title from the enclosing release folder. Leading `01`, `E01`, or `Episode 01` filenames are supported inside an explicit `Season 01` or `S01` directory.
- Movies: a title and four-digit year in the filename or enclosing release folder, such as `Movie.Title.2024.1080p.BluRay.x265-GROUP/movie.mkv`. Yearless movies are skipped because guessing is unreliable. Titles are inferred offline, without correcting aliases against TMDb/TVDb.
- Video extensions: MKV, MP4, AVI, M4V, MOV, WMV, TS, M2TS, MPG, MPEG, WEBM. Music organization, RAR/ZIP extraction, disc folder structures, absolute-numbered anime, date-numbered episodes, and multipart movie merging are outside this version's scope.
- Matching subtitles and external audio in the **same folder** follow the video and retain suffixes such as `.en.forced.srt`. Supported: SRT, ASS, SSA, SUB/IDX, VTT, SUP, AAC, AC3, EAC3, DTS, MKA, FLAC. Separate `Subs` trees, artwork, NFO files and other attachments are preserved under `.download-extras` when a Move release completes; Copy leaves these originals untouched. Late matching subtitles are imported on later scans in Copy mode; add them before import when using Move mode.
- Unknown or ambiguous names, samples, trailers, extras, hidden files, Synology metadata directories, and symbolic links/reparse points are skipped. Configured roots and existing destination ancestors must not be symbolic links.
- Existing destinations are **never overwritten**. Alternate encodes of the same movie/episode are reported as conflicts for manual review. Renaming removes release quality/group labels from the destination filename. It does not transcode, split, or modify the media content.

## Reliability and reports

Every file must have an unchanged size and modification time across polls for the configured delay. A restart starts that observation period again. Transfers stream one file at a time into a hidden temporary file in the destination folder, flush to disk, check source size/timestamp again, and publish by a rename without overwriting. Sidecars publish before the video. An error can leave already-published sidecars; later scans resume using the receipt ledger. Temporary files left by a process crash are not auto-deleted; inspect/remove `.autoorganizer-*.tmp` files with Jellyfin stopped if necessary.

Move mode deletes sources only after the group has import receipts and its sizes/timestamps still match. File stability checks are best-effort, especially on Linux where a separate writer can continue writing; a completed folder or completion marker remains essential. This plugin is intended for media folders you control, not folders being changed by untrusted users during import.

The state directory is `<Jellyfin data directory>/autoorganizer/`:

- `receipts.json`: durable source/destination size/timestamp receipts, preventing repeated copies across restarts. Keep this file. Changes to copied sources or destinations become conflicts; files are not compared by content hash. The ledger retains import history and may grow over time.
- `last-run.json`: latest scan summary and up to 500 file decisions; plugin logs also contain previews, imports, skips and errors. Waiting messages are at debug level.

If a crash occurs after publishing media but before saving its receipt, the existing destination is treated as a conflict, preserving the source for manual review. A corrupt receipt ledger fails the scan instead of guessing. Do not delete the ledger to resolve a conflict; review the source/destination pair first. Release cleanup removes only verified empty directories without recursive deletion, so new files and links keep their folder in place.

## Build and verify

Install .NET SDK 9 or later on a development computer:

```powershell
dotnet build Jellyfin.AutoOrganizer.sln -c Release
dotnet test Jellyfin.AutoOrganizer.sln -c Release
node --test tests/config-page.test.cjs
pwsh -File scripts/package.ps1
```

The package includes only this plugin's two DLLs, its README, and license, not Jellyfin server assemblies. `scripts/package.ps1` creates a ZIP and SHA-256 checksum in `artifacts/`. Before publication, `tools/PackageVerifier` loads both packaged assemblies to check their versions and compares the embedded settings resource byte-for-byte with release source, including library selectors and folder-browser controls. CI builds and tests on Linux and Windows and uploads the ZIP. Build against a different **10.11.x** patch version when required:

```powershell
pwsh -File scripts/package.ps1 -JellyfinVersion 10.11.5
```

That parameter changes the Jellyfin NuGet references. A different Jellyfin major/minor release may also require code or target-framework changes. Do not assume compatibility from a successful DLL copy.

Tests cover torrent release parsing (including multi-season packs), movies, numeric titles, subtitle naming, copy/move, complete-release preservation and cleanup, changing attachments, recovery after restart, existing-file conflicts, incomplete files, completion markers, stability windows, cancellation, destination overrides, path overlap, and corrupt state. The configuration tests cover library discovery/filtering, selected-path persistence, folder browsing/navigation, cancellation, stale responses, error handling, and upgrades from shared-root settings. The Node tests use the built-in test runner and require no npm dependencies (Node 24 is used in CI). Three symlink tests run on Unix and are explicitly skipped on Windows, where creating symlinks requires additional privileges. No NAS is required to run the tests.

Licensed under GPL-3.0, consistent with the Jellyfin plugin template. See [LICENSE](LICENSE).

If an upgrade still shows an older settings page, restart the Jellyfin container and reopen the plugin from Dashboard → Plugins instead of an old browser tab. Refresh the page or try a private window. The settings heading shows its build version, and settings-page URLs are versioned from 1.0.2 onward to avoid reusing the old page URL.

## Publishing updates

Commit and push changes, then create and push a version tag such as `v1.0.1`. The **Publish Jellyfin repository** workflow tests on Linux, builds both ZIP layouts, creates a GitHub Release, and updates `manifest.json` on the default branch using the exact catalog asset's MD5 checksum required by Jellyfin. Release assets also include SHA-256 checksum files. Published release assets are not replaced; use a new version tag for changes. Repository Actions must allow the workflow's `contents: write` permission, and branch protection must permit its catalog commit.
