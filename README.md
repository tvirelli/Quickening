<div align="center">

<img src="https://quickening.app/assets/og-image.jpg" alt="Quickening — free Windows duplicate & large-file cleaner" width="680">

# ⚡ Quickening

### Your disk called. *"We need to talk about your Downloads folder."*

**Find duplicate files, space hogs, and look-alike photos on Windows — and clear them safely.**
Everything goes to the Recycle Bin, so nothing is ever gone for good.

<br>

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?style=for-the-badge&logo=windows&logoColor=white)
![Free](https://img.shields.io/badge/Price-Free%20forever-5EE7B7?style=for-the-badge)
![Recycle Bin safe](https://img.shields.io/badge/Deletes%20to-Recycle%20Bin-FF7AB6?style=for-the-badge)
![Source available](https://img.shields.io/badge/Source-available-8C6EFF?style=for-the-badge)

<br>

[![⬇ Download for Windows](https://img.shields.io/badge/⬇%20%20Download%20for%20Windows-2E6BFF?style=for-the-badge&labelColor=2E6BFF)](https://quickening.app/download)
&nbsp;&nbsp;
[![Website](https://img.shields.io/badge/quickening.app-131B3C?style=for-the-badge)](https://quickening.app)

<br>

<img src="https://quickening.app/assets/shots/home.webp" alt="Quickening home screen — choose Find Duplicates or Find Large Files, pick folders, and scan" width="820">

</div>

---

## Why Quickening?

Every Windows PC quietly fills up with the same files, over and over. Browsers save `report (1).pdf` next to `report.pdf`. Phone imports re-copy whole albums. Messaging apps hoard every photo you view. A typical three-year-old machine hides **5–30 GB of exact duplicates** — plus a few forgotten giants and thousands of near-identical photos.

Windows has no built-in way to find any of that. Quickening does — and it's built for people who are (rightly) nervous about letting an app delete their files.

> **The whole pitch:** it's free, it's fast, and it's *safe*. Removals go to the Recycle Bin with one-click Undo, system folders are hard-blocked, and the source is right here for you to read. For an app that deletes things, you shouldn't have to take anyone's word for it.

---

## What it finds

<table>
<tr>
<td width="33%" valign="top">

### 🔁 Duplicates
Exact, **content-verified** copies — not just matching names. Identical files are grouped together with a recommended keeper (newest, oldest, or shortest path — your call). *There can be only one.*

</td>
<td width="33%" valign="top">

### 📦 Large files
The biggest space hogs above a size you pick with a slider — biggest first, even the one-of-a-kind ones. Sometimes a single forgotten video outweighs a thousand duplicates.

</td>
<td width="33%" valign="top">

### 🖼️ Look-alike photos
Photos that *look* alike but aren't identical — resized, re-compressed, or lightly edited — found by perceptual matching and shown with a match percentage. Never auto-selected; you decide.

</td>
</tr>
</table>

<div align="center">
<img src="https://quickening.app/assets/shots/summary.webp" alt="Scan summary with a category donut showing reclaimable space" width="49%">
<img src="https://quickening.app/assets/shots/results.webp" alt="Duplicate results grouped by file, newest copy marked KEEP" width="49%">
</div>

---

## Built for the cautious 🛡️

The safety story is the point, not a footnote:

- **Recycle Bin, always.** Nothing is permanently deleted in normal use — ever. Removals use the same Windows `IFileOperation` API Explorer uses, so Restore works exactly as you expect.
- **One-click Undo.** Right after any removal, one click puts everything back where it lived.
- **System folders are hard-blocked.** `C:\Windows`, `Program Files`, and `ProgramData` are never scanned and never deletable — enforced in code, not a setting you can flip off by accident.
- **Warns on risky files.** VM disks, databases, installers, and other easy-to-regret file types are flagged and need an explicit extra confirmation.
- **Re-checks at delete time.** Anything that changed since the scan is skipped, not removed by surprise.
- **Respects cloud & network files.** Online-only placeholders and network drives are handled with care.

<div align="center">
<img src="https://quickening.app/assets/shots/compare-real.webp" alt="Three copies of the same photo compared side by side" width="49%">
<img src="https://quickening.app/assets/shots/celebration.webp" alt="Celebration screen — Nice, 9.1 GB back!" width="49%">
</div>

---

## Get Quickening

> **[⬇ Download the latest installer →](https://quickening.app/download)**

- Free, Windows 10 & 11, installs just for your user (no admin rights needed), and updates itself automatically.
- Official downloads come **only** from [quickening.app](https://quickening.app) and this repo's [Releases](https://github.com/tvirelli/Quickening/releases). A copy from anywhere else may not be what it claims.

<details>
<summary><b>Heads-up: Windows will be cautious the first time</b></summary>

<br>

Because Quickening isn't code-signed yet, the first launch shows a blue **"Windows protected your PC — unknown publisher"** SmartScreen notice. That's Windows being careful about apps from small developers, not a sign anything's wrong. Click **More info**, then **Run anyway**. Quickening installs only for your user account and never needs administrator rights.

</details>

---

## Why "Quickening"? 🗡️

Yes, it's a [**Highlander**](https://en.wikipedia.org/wiki/Highlander_(film)) reference. In the film, *the Quickening* is the surge of power an immortal absorbs when only one remains — **"there can be only one."** Which is exactly what a duplicate finder does: among a pile of identical copies, one survives and the rest are retired. And "quicken" also just means *to bring back to life and speed up* — which is what clearing the clutter does to a sluggish drive.

The full story is [on the blog](https://quickening.app/blog/post/whats-up-with-the-name-quickening).

---

## Source-available

Quickening is **source-available** — the complete source is public so you can read it, audit it, and verify exactly what the app does with your files.

- **The app** (compiled, from quickening.app or Releases) is free for anyone, any purpose, including at work — governed by the [EULA](EULA.md). The two rules: don't redistribute it, and don't charge people for Quickening itself.
- **This source** is under the [PolyForm Strict 1.0.0](LICENSE) license: read it, study it, build it for personal noncommercial use — but no redistribution, no derivatives, no commercial use. Distribution stays official.
- The **Quickening** name and logo are trademarks and aren't covered by the source license.

Want to do something the license doesn't allow? Just ask: **support@quickening.app** — the answer is often yes.

See also: [PRIVACY.md](PRIVACY.md) · [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) · [SECURITY.md](SECURITY.md)

---

## Build from source

Prerequisites: **.NET 8 SDK**, the **Windows App SDK** workload, Windows 10/11, and Visual Studio 2022 (or the CLI). The app targets `win-x64`.

```bash
git clone https://github.com/tvirelli/Quickening.git
cd Quickening
dotnet build Quickening.sln
dotnet test          # the safety-critical logic is covered by the test suite
```

To run: open `Quickening.sln` in Visual Studio 2022, set `Quickening.App` as the startup project, and press F5 — or `dotnet run --project src/Quickening.App`.

**Under the hood:** C# / .NET 8 + WinUI 3 (native Fluent design, direct access to the Windows shell for Recycle Bin, tray, and Explorer integration), **BLAKE3** for fast content hashing (a match is as good as byte-for-byte), and **SQLite** (WAL mode) for the scan cache and history. The exact-duplicate engine is a four-stage funnel — group by size → partial hash → full BLAKE3 hash → optional byte-for-byte paranoid check — so most files are ruled out without ever being fully read.

```
src/
├── Quickening.Core/   # scanning, hashing, similarity, safety rules, SQLite, Recycle Bin — no UI, fully testable
└── Quickening.App/    # WinUI 3 app: scan setup, results grid, filters, charts, media viewer, tray
tests/                 # xUnit — the deletion & safety logic under test
```

---

## Security

Found something that could put a user's files at risk? Please report it privately — see [SECURITY.md](SECURITY.md). Do not open a public issue for vulnerabilities.

---

<div align="center">

**Free forever. Coffee optional.** ☕
If Quickening saved you some space (and a headache), a [tip](https://ko-fi.com/tvirelli) keeps it free and getting better.

<br>

<sub>Made by one person who also had 14,000 duplicate photos. · © 2026 Quickening · <a href="https://quickening.app">quickening.app</a></sub>

</div>
