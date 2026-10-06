# Changelog

All notable changes to Quickening are documented here. Quickening updates itself
automatically, so you're normally on the latest version already.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and
Quickening follows [Semantic Versioning](https://semver.org/).

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
