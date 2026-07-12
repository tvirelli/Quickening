# Changelog

All notable changes to Quickening are documented here. Quickening updates itself
automatically, so you're normally on the latest version already.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and
Quickening follows [Semantic Versioning](https://semver.org/).

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

[1.1.0]: https://github.com/tvirelli/Quickening/releases/tag/v1.1.0
[1.0.3]: https://github.com/tvirelli/Quickening/releases/tag/v1.0.3
[1.0.2]: https://github.com/tvirelli/Quickening/releases/tag/v1.0.2
[1.0.1]: https://github.com/tvirelli/Quickening/releases/tag/v1.0.1
[1.0.0]: https://github.com/tvirelli/Quickening/releases/tag/v1.0.0
