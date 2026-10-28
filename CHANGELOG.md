# Changelog

All notable changes to Quickening are documented here. Quickening updates itself
automatically, so you're normally on the latest version already.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and
Quickening follows [Semantic Versioning](https://semver.org/).

## [1.3.3] - 2026-10-27

### Added
- Results Sort menu: Largest first, Smallest first, or Scan order. Each group
  ranks by its largest file; applies to every results section and is
  remembered between sessions.
- Settings > Keep overrides: a ranked list of folders (add, remove, drag to
  reorder). The copy in the highest-ranked folder gets the KEEP badge and is
  left unticked by Select Recommended; the normal keep rule picks among copies
  in that folder, or decides alone when no copy is in any listed folder.

### Fixed
- Crash (c000027b in Microsoft.UI.Xaml.dll) while reviewing results: loading a
  video row's thumbnail made a blocking Windows file call during the list's
  layout pass. Thumbnail and comparison-viewer file loads now run in the
  background.

## [1.3.2] - 2026-10-14

### Added
- "Update ready" banner with Restart to update (Velopack apply-and-restart);
  a Windows notification with the same button when the window is hidden in
  the tray, once per version. Restart waits while a scan or removal runs.
- Update rechecks every 6 hours while the app is running.

### Changed
- A staged update the user didn't restart for now installs silently on exit
  (no installer window). Settings > Check for updates offers Restart to
  update once an update is ready.

## [1.3.1] - 2026-10-14

### Changed
- Filters (category, size, modified, path, extension, copies) apply to every
  results section, not only Duplicates; a group left with fewer than 2
  matching files is hidden. The Copies filter hides the Blurry list (not a
  group). The Extension list covers every section.
- Select All, Invert, By folder, Keep best, the selected-size stat and Remove
  act only on visible files: files hidden by a filter or a SHOW-hidden
  section keep their tick but are never removed.
- Group header checkbox on duplicate, similar-photo, similar-video and song
  groups: all, then all but the KEEP / best copy, then none.
- Larger 22px checkboxes with fixed colours.

### Fixed
- The app now always uses its dark theme; under Windows light mode, stock
  controls (checkboxes, dropdown lists) drew dark-on-dark.

## [1.3.0] - 2026-10-06

### Added
- Advanced Options: find similar photos (perceptual hash with a strictness
  slider), blurry photos (sharpness score with a strictness slider), duplicate
  songs (same title/artist/length across formats and bitrates), and similar
  videos (sampled-frame matching).
- Keep best for similar photos: resolution, then clearly sharper (15%), then
  original format (RAW > lossless > lossy), then file size; the keeper's badge
  names the deciding reason.
- Tidy up: empty folders and zero-byte files, sent to the Recycle Bin.
- Duplicate folders: whole-folder copies, verified against what's on disk.
- Ignore files and folders from results and future scans; ignored items are
  never selectable or deletable. Settings > Manage Ignored Files.
- Results page rebuilt as collapsible sections with a SHOW checklist.
- Modified time shown to the second; a keeper that only wins on fractions of a
  second is labelled plain "KEEP".
- Per-date clear buttons on the Modified filter.

### Fixed
- Crash shortly after Undo (Recycle Bin item IDs were freed twice).
- "Rescan at 50 MB" never ran and left every later scan refused.
- CSV export is written as UTF-8 with a BOM, so Excel shows non-ASCII text.
- Esc closes the compare viewer; similar-photo groups use their own wording.
- Accessible names for file rows, checkboxes, switches, and sliders.
- Category counts and the Large Files header exclude ignored files.
- Celebration screen and Recycle Bin pill update immediately after Undo; the
  Undo toast lasts 15 seconds.
- POWER USER badge was clipped out of view; window minimum is now 1024x700.
- An unreadable subfolder no longer makes its parent look empty in Tidy up.

## [1.2.0] - 2026-07-12

### Added
- Redesigned the results filters into a single left sidebar rail (category,
  size, modified date, path, copies, extension); the list header is now just
  Select actions plus a result count.
- Multi-select category filtering, shown as a checklist with live per-category
  file counts ("All" clears the filter).
- Filter by file extension, listing only extensions that appear in the results
  (and only ones that yield a group of 2+, so every choice returns results).
- Calendar date pickers for Modified, a copies dropdown (2–10+), and a size
  value + MB/GB unit input.

### Fixed
- Filter inputs now match the dark theme with centered placeholders and no
  leftover default WinUI field chrome.

## [1.1.0] - 2026-07-12

### Added
- Side-by-side compare preview for many more file types: images (including WebP
  and SVG), video (with thumbnails), audio (with a real waveform), PDF, syntax-
  highlighted code, Markdown, plain text, and ZIP/TAR archive contents.
- "Created together" safety signal: duplicates created in the same instant are
  flagged, left out of the recommended selection, and require an extra
  confirmation to delete.
- Real Windows file-type icons in the results list.
- "Open file" and "Open file location" in the results right-click menu; a
  confirmation prompt opens non-previewable files in their default app.

### Fixed
- App could stop responding when previewing some stylesheet files
  (.css / .less / .scss); the preview no longer hangs.
- Some videos reported an incorrect (far too long) duration; it now corrects
  itself.

## [1.0.3] - 2026-07-12

### Changed
- Removed the unused "Share anonymous usage stats" setting from Settings.

### Fixed
- History screen's empty state: the "Run your first scan" button is now centered.

## [1.0.2] - 2026-07-12

### Fixed
- Restored the app icon, the title-bar logo, and the bundled fonts, which a
  packaging gap had left out of the installed 1.0.1 build.

## [1.0.1] - 2026-07-12

### Fixed
- Setup now installs cleanly and registers an uninstaller — a conflict with the
  app's own data folder previously blocked installation on some PCs.

## [1.0.0] - 2026-07-11

### Added
- First public release: find duplicate files, large files, and look-alike
  photos on Windows 10 & 11, and remove them safely to the Recycle Bin.
- Silent automatic updates.

[1.2.0]: https://github.com/tvirelli/Quickening/releases/tag/v1.2.0
[1.1.0]: https://github.com/tvirelli/Quickening/releases/tag/v1.1.0
[1.0.3]: https://github.com/tvirelli/Quickening/releases/tag/v1.0.3
[1.0.2]: https://github.com/tvirelli/Quickening/releases/tag/v1.0.2
[1.0.1]: https://github.com/tvirelli/Quickening/releases/tag/v1.0.1
[1.0.0]: https://github.com/tvirelli/Quickening/releases/tag/v1.0.0
