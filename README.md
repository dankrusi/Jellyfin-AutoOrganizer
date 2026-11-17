# Jellyfin Auto Organizer

A Jellyfin plugin for automatically filing completed movie and TV downloads. Designed for Jellyfin on a Synology NAS, including Container Manager installations. It uses Jellyfin's plugin/configuration/scheduled-task interfaces from the [official template](https://github.com/jellyfin/jellyfin-plugin-template); it runs inside Jellyfin, with no separate organizer service or metadata account.

This build targets **Jellyfin 10.11.5 / .NET 9**. Check your server version under Dashboard before installing. The DLLs are managed AnyCPU and have no native dependencies. They do not require a separate .NET installation on the NAS; Jellyfin supplies the runtime. Compatibility has been checked by compiling against Jellyfin 10.11.5; a live Synology/Jellyfin installation has not been tested here.

## What it does

Your incoming folder can contain loose media files or nested torrent release folders:

```text
incoming/
├── Succession (2018) Season 1-4 S01-S04 (1080p Mixed x265 HEVC 10bit EAC3 5.1 Silence)/
│   ├── Season 1/
│   │   ├── Succession.S01E01.1080p.WEB-DL.x265.mkv
│   │   └── Succession.S01E01.1080p.WEB-DL.x265.en.srt
│   └── Season 4/
│       └── S04E10.mkv
└── Dune.Part.Two.2024.1080p.BluRay.x265-GROUP.mkv
```

The result is:

```text
library/
├── Shows/
│   └── Succession/
│       ├── Season 01/
│       │   ├── Succession - S01E01.mkv
│       │   └── Succession - S01E01.en.srt
│       └── Season 04/
│           └── Succession - S04E10.mkv
└── Movies/
    └── Dune Part Two (2024)/
        └── Dune Part Two (2024).mkv
```

The structure follows Jellyfin's [TV](https://jellyfin.org/docs/general/server/media/shows/) and [movie](https://jellyfin.org/docs/general/server/media/movies/) naming guidance. Optionally include series years to distinguish remakes. Season 00 is supported for numbered specials.

## Install from Jellyfin's plugin catalog

1. Open **Dashboard → Plugins → Repositories → Add** in Jellyfin.
2. Name it **Auto Organizer** and enter this repository URL:

   ```text
   https://raw.githubusercontent.com/dankrusi/Jellyfin-AutoOrganizer/main/manifest.json
   ```

3. Save, open the **Catalog** tab, and install **Auto Organizer**.
4. Restart Jellyfin, then open **Dashboard → Plugins → My Plugins → Auto Organizer** to configure the incoming folder and library root. Enable monitoring, keep **Preview only** on for the first run, and save.

The catalog build targets Jellyfin **10.11.5**. Jellyfin filters out releases targeting a newer server version. The plugin cannot create container bind mounts or DSM permissions; follow the folder-mapping steps below before enabling imports. The repository supplies future tagged versions through Jellyfin's plugin update system.

Releases include a `-catalog.zip` whose DLLs are at the ZIP root for Jellyfin's installer, plus a manual-install ZIP containing a plugin folder. Use the manual ZIP only for the File Station procedure below.

## Manual install on Synology

No compilation is needed on the NAS. Use the ZIP in `artifacts/`, or a ZIP from the repository's Actions artifacts after running CI.

1. **Stop Jellyfin** in Container Manager.
2. Extract `AutoOrganizer-1.0.0-jellyfin-10.11.5.zip` on your computer or with File Station. It contains an `AutoOrganizer_1.0.0` folder with **both** DLLs.
3. Copy that whole folder into Jellyfin's persistent **plugins directory**. For the official `jellyfin/jellyfin` image with host `/volume1/docker/jellyfin/config` mapped to container `/config`, this is normally `/volume1/docker/jellyfin/config/plugins/AutoOrganizer_1.0.0/`. Locate the actual directory in your deployment; third-party packages/images can differ. Create `plugins` if necessary. Ensure Jellyfin's user can read both DLLs.
4. Map your completed downloads and media library into the Jellyfin container, if not already mapped. Example:

   | Synology host folder | Container folder | Access |
   | --- | --- | --- |
   | `/volume1/downloads/completed` | `/downloads/completed` | Read for Copy; read/write for Move |
   | `/volume1/media` | `/media` | Read/write |
   | `/volume1/docker/jellyfin/config` | `/config` | Read/write |

   Grant the container's user the corresponding DSM shared-folder/ACL permissions. These are examples; use your existing paths. Container paths and host paths are different. See Jellyfin's [container documentation](https://jellyfin.org/docs/general/installation/container/) for bind mounts.
5. Start Jellyfin. Open **Dashboard → Plugins → My Plugins → Auto Organizer**.
6. Configure incoming `/downloads/completed` and library root `/media`. Enable monitoring, keep **Preview only** enabled, and save.
7. The task polls every minute and waits for five minutes of observed file stability. Review `AutoOrganizer Preview` lines in **Dashboard → Logs**. Under **Scheduled Tasks → Auto Organizer → Organize incoming downloads**, you can start a scan manually or change the interval. Repeated manual scans still honor the stability delay.
8. Once previews look correct, turn preview off. **Copy** is the default and keeps the torrent's originals for seeding. Choose **Move** only if the torrent client no longer needs the originals.
9. Add a **Shows** library for `/media/Shows` and a **Movies** library for `/media/Movies` under Dashboard → Libraries. Enable real-time monitoring on these libraries, or schedule scans. The organizer does not run a full Jellyfin library scan on every poll.

If you prefer SSH after extracting the archive, the optional installer copies the two DLLs into an existing plugins directory:

```sh
sh scripts/install-synology.sh /volume1/downloads/AutoOrganizer_1.0.0 /volume1/docker/jellyfin/config/plugins
```

Stop Jellyfin first and start it afterwards. For updates, move the old plugin folder **outside** the plugins directory before copying a replacement. Keep only one Auto Organizer installation. To uninstall, stop Jellyfin, remove its plugin folder, and restart. Organized media is unaffected.

## Download completion and torrent seeding

**Point the plugin at your downloader's completed folder**, with incomplete downloads stored elsewhere. Polling alone cannot distinguish a paused, preallocated, or stalled torrent from a finished download. The stability delay is an additional check, not proof of completion. Common `.part`, `.!qB`, `.!ut`, `.aria2`, `.partial`, `.crdownload`, `.download` and `.tmp` files block their top-level download folder from import.

For stricter gating, set **Completion marker** to `.completed` and have the downloader's completion hook create that file inside each top-level release folder. The marker's existence is an explicit assertion that the folder is complete. In marker mode, loose files at the incoming root are skipped. Keep the stability delay enabled as well.

Copy mode preserves torrent filenames and folder locations but uses additional disk space. Move mode also copies to a temporary file at the destination before publishing, so it works across mounted volumes, but requires space for the destination copy. No hardlink mode is included.

## Supported names and scope

- Episodes: `Show.Name.S01E02`, `Show Name 1x02`, contiguous `S01E01E02` / `S01E01-E02` / `S01E01-02`, and `1x01-1x02` ranges. Episode-only `S01E01` filenames inherit the title from the enclosing release folder. Leading `01`, `E01`, or `Episode 01` filenames are supported inside an explicit `Season 01` or `S01` directory.
- Movies: a title and four-digit year in the filename or enclosing release folder, such as `Movie.Title.2024.1080p.BluRay.x265-GROUP/movie.mkv`. Yearless movies are skipped because guessing is unreliable. Titles are inferred offline, without correcting aliases against TMDb/TVDb.
- Video extensions: MKV, MP4, AVI, M4V, MOV, WMV, TS, M2TS, MPG, MPEG, WEBM. Music organization, RAR/ZIP extraction, disc folder structures, absolute-numbered anime, date-numbered episodes, and multipart movie merging are outside this version's scope.
- Matching subtitles and external audio in the **same folder** follow the video and retain suffixes such as `.en.forced.srt`. Supported: SRT, ASS, SSA, SUB/IDX, VTT, SUP, AAC, AC3, EAC3, DTS, MKA, FLAC. Separate `Subs` trees, artwork, and NFO files are left untouched. Late subtitles are imported on later scans in Copy mode; add them before import when using Move mode.
- Unknown or ambiguous names, samples, trailers, extras, hidden files, Synology metadata directories, and symbolic links/reparse points are skipped. Configured roots and existing destination ancestors must not be symbolic links.
- Existing destinations are **never overwritten**. Alternate encodes of the same movie/episode are reported as conflicts for manual review. Renaming removes release quality/group labels from the destination filename. It does not transcode, split, or modify the media content.

## Reliability and reports

Every file must have an unchanged size and modification time across polls for the configured delay. A restart starts that observation period again. Transfers stream one file at a time into a hidden temporary file in the destination folder, flush to disk, check source size/timestamp again, and publish by a rename without overwriting. Sidecars publish before the video. An error can leave already-published sidecars; later scans resume using the receipt ledger. Temporary files left by a process crash are not auto-deleted; inspect/remove `.autoorganizer-*.tmp` files with Jellyfin stopped if necessary.

Move mode deletes sources only after the group has import receipts and its sizes/timestamps still match. File stability checks are best-effort, especially on Linux where a separate writer can continue writing; a completed folder or completion marker remains essential. This plugin is intended for media folders you control, not folders being changed by untrusted users during import.

The state directory is `<Jellyfin data directory>/autoorganizer/`:

- `receipts.json`: durable source/destination size/timestamp receipts, preventing repeated copies across restarts. Keep this file. Changes to copied sources or destinations become conflicts; files are not compared by content hash. The ledger retains import history and may grow over time.
- `last-run.json`: latest scan summary and up to 500 file decisions; plugin logs also contain previews, imports, skips and errors. Waiting messages are at debug level.

If a crash occurs after publishing media but before saving its receipt, the existing destination is treated as a conflict, preserving the source for manual review. A corrupt receipt ledger fails the scan instead of guessing. Do not delete the ledger to resolve a conflict; review the source/destination pair first. Empty release directories and unrelated source files are retained.

## Build and verify

Install .NET SDK 9 or later on a development computer:

```powershell
dotnet build Jellyfin.AutoOrganizer.sln -c Release
dotnet test Jellyfin.AutoOrganizer.sln -c Release
pwsh -File scripts/package.ps1
```

The package includes only this plugin's two DLLs, its README, and license, not Jellyfin server assemblies. `scripts/package.ps1` creates a ZIP and SHA-256 checksum in `artifacts/`. CI builds and tests on Linux and Windows and uploads the ZIP. Build against a different **10.11.x** patch version when required:

```powershell
pwsh -File scripts/package.ps1 -JellyfinVersion 10.11.5
```

That parameter changes the Jellyfin NuGet references. A different Jellyfin major/minor release may also require code or target-framework changes. Do not assume compatibility from a successful DLL copy.

Tests cover torrent release parsing (including the Succession season pack), movies, numeric titles, subtitle naming, copy/move, repeat imports after restart, existing-file conflicts, incomplete files, completion markers, stability windows, cancellation, path overlap, and corrupt state. Two symlink tests run on Unix and are explicitly skipped on Windows, where creating symlinks requires additional privileges. No NAS is required to run the tests.

Licensed under GPL-3.0, consistent with the Jellyfin plugin template. See [LICENSE](LICENSE).

## Publishing updates

Commit and push changes, then create and push a version tag such as `v1.0.1`. The **Publish Jellyfin repository** workflow tests on Linux, builds both ZIP layouts, creates a GitHub Release, and updates `manifest.json` on the default branch using the exact catalog asset's MD5 checksum required by Jellyfin. Release assets also include SHA-256 checksum files. Published release assets are not replaced; use a new version tag for changes. Repository Actions must allow the workflow's `contents: write` permission, and branch protection must permit its catalog commit.
