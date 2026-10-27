# Archive.org Collection Sync

Download and sync collections from archive.org to a local folder. Files download in parallel, large files are split
across several connections, and interrupted downloads resume. Every file is checked against the collection's
checksums.

> **Fork notice:** this project is a fork of
> [jasonunbrokensoftware/ArchiveOrgCollectionSync](https://github.com/jasonunbrokensoftware/ArchiveOrgCollectionSync)
> by Jason / Unbroken Software, LLC. Version 1.3 adds the parallel, resumable download engine and the TeraCopy-style
> file list, sessions and log described below. All credit for the original app goes to the upstream author.

![Archive.org Collection Sync 1.3 syncing a collection](docs/screenshot.png)

## What's new in 1.3

- **Faster downloads**
  - Several files download at once ("Files in parallel").
  - Files of 32 MB or more are split into HTTP `Range` segments over several connections ("Connections per file").
- **Resumable downloads.** Data goes into `<name>.part`, with a `<name>.part.json` segment map. Pause, a crash or
  closing the app keeps the partial file, and the next run continues where it stopped.
- **TeraCopy-style file list.** Every file from the collection appears in a grid with:
  - status, size, a per-file progress bar showing its connection count, speed and ETA;
  - filters with live counts (All / Active / Queued / Done / Skipped / Failed) and a search box;
  - a right-click menu: open folder, copy name or URL, retry selected.
- **Overall progress.** The bottom bar shows files and bytes done, speed, ETA and open connections, with Start,
  Pause/Resume, Stop and Retry Failed buttons. Progress also appears on the taskbar and in the window title.
- **Sessions tab.** A history of every run, with Open, Resume, Retry Failed, Re-run, Export CSV, Open Log and Delete.
  Closing the app during a sync pauses and saves the session.
- **Log tab.** Level toggles (Debug/Info/Warn/Error), search, auto-scroll, copy and save. Each session also writes its
  own log file.
- **Remembered settings.** The last URL, folder and options are kept between runs.
- **Fixes:**
  - thread-safe counters;
  - an O(n) delete scan;
  - case-insensitive MD5 comparison;
  - proper URL parsing;
  - `<collection>_files.xml` (which has no size in the file list) now downloads correctly.

## Usage

1. Paste a collection URL, either `https://archive.org/details/<collection>` or
   `https://archive.org/download/<collection>`.
2. Pick a destination folder.
3. Choose the options and press **Start**.

Files already on disk with the right size and checksum are skipped. Files with the wrong size or checksum are
downloaded again and only replace the old copy once the new one verifies.

### Options

| Option | Default | Notes |
|---|---|---|
| Files in parallel | 4 | 1–16. How many files download at the same time. |
| Connections per file | 4 | 1–16. Ranged connections for files of 32 MB or more; smaller files use one. |
| Verify | MD5 checksum | **Size only** is much faster when re-checking a large existing folder. |
| Originals only | on | Skips derivatives that archive.org generates (thumbnails, torrents, `_meta.xml`…). |
| Delete files not in the collection | off | Deletes other files under the destination; in-progress `.part` files are kept. |

**Picking the parallelism.** The total number of connections is files × connections (4 × 4 = 16 by default).
archive.org publishes no hard limit, but it throttles heavy clients with HTTP 429 or 503. Past a few connections per
file your own line is usually the bottleneck. In testing, 4 connections beat both 1 and 8.

### Where data is stored

| Path | Contents |
|---|---|
| `%LocalAppData%\ArchiveOrgCollectionSync\settings.json` | Last URL, folder and options |
| `%LocalAppData%\ArchiveOrgCollectionSync\sessions\<yyyyMMdd_HHmmss>_<collection>.json` | Session history (per-file status) |
| `%LocalAppData%\ArchiveOrgCollectionSync\sessions\<…>.log` | Session log (tab-separated) |
| `<destination>\<file>.part`, `.part.json` | In-progress download and its resume map |

## How it works

### Scan

The app fetches `https://archive.org/download/<collection>/<collection>_files.xml` and reads each file's `name`,
`source`, `size` and `md5`.

### Download engine (`DownloadEngine.cs`)

- **Shared client.** One `HttpClient` (`SocketsHttpHandler`, pooled connections) with a 60-second idle timeout per
  read and no overall timeout, so multi-GB files never time out.
- **Workers.** A channel of files feeds *N* async workers.
- **Per file:**
  1. Follow the `archive.org/download/...` → `iaNNNNNN.us.archive.org` redirect once.
  2. Split the file into *K* segments.
  3. Each segment streams into its own offset of the preallocated `.part` file (`RandomAccess.WriteAsync`).
  4. Save the segment map every 2 seconds.
- **Retries.** Up to 3 retries with exponential backoff on IO errors, timeouts and HTTP 408/429/5xx, honouring
  `Retry-After`. If a server ignores `Range`, the file falls back to a single stream.
- **Verification.** At most 2 MD5 checks run at once, so downloads keep the line busy. The `.part` file then
  atomically replaces the destination file.

### UI

The UI never blocks the workers. They update thread-safe counters, and a 250 ms timer publishes those to the grid,
the totals and the log.

## Requirements and building

- Windows with the [.NET 6 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/6.0) or newer.
- Build with the .NET SDK 6 or newer:

```
dotnet build ArchiveOrgCollectionSync/ArchiveOrgCollectionSync.csproj -c Release
dotnet publish ArchiveOrgCollectionSync/ArchiveOrgCollectionSync.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

The About tab shows the version plus the git commit it was built from, e.g. `1.3.0 (fe76d11)`.

## Credits

- **Original app:** [Archive.org Collection Sync](https://github.com/jasonunbrokensoftware/ArchiveOrgCollectionSync)
  by Jason / Unbroken Software, LLC. That covers the collection sync concept, the checksum comparison, nested folders,
  the delete-extra-files option and simultaneous downloads.
- **1.3 fork:** [dsync89](https://github.com/dsync89) added the parallel/segmented/resumable engine, the file list,
  sessions and the log.
- Folder picker: [WindowsAPICodePack](https://www.nuget.org/packages/WindowsAPICodePack).

## License

MIT License, unchanged from upstream. See [LICENSE](LICENSE). Copyright (c) Unbroken Software, LLC.
