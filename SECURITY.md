# Security Policy

Quickening is a file-management utility that can move files to the Recycle Bin, so
I take its security and safety seriously. Thank you for helping keep it and its
users safe.

## Reporting a vulnerability

**Please do not open a public issue for security vulnerabilities.**

Report privately, in either of these ways:

- **GitHub private vulnerability reporting** — on this repository, go to the
  **Security** tab → **Report a vulnerability**. This opens a private advisory
  visible only to me and you.
- **Email** — support@quickening.app. Put "Security" in the subject line. If you
  want to encrypt, ask and I'll share a key.

Please include:

- A clear description of the issue and its impact
- Steps to reproduce (a proof of concept if you have one)
- The Quickening version and your Windows version
- Any relevant logs or file paths (redact anything private)

## What to expect

- **Acknowledgement** within a few days.
- An honest assessment of whether it's a vulnerability and its severity.
- Progress updates while a fix is developed, and credit in the release notes when
  the fix ships — unless you'd prefer to stay anonymous.

This is a free project maintained by one person, so timelines are best-effort, but
anything that could put a user's files at risk is treated as a priority.

## Scope

In scope:

- The Quickening desktop application (this repository) — especially anything that
  could cause **unintended file loss**, delete outside the Recycle Bin, bypass the
  hard-blocked system paths, escalate privileges, or execute untrusted code.
- The update mechanism.

Out of scope:

- The quickening.app website and its forms (report those the same way if needed,
  but they live outside this repository).
- Third-party dependencies — please report those upstream (see
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)); I'll help coordinate if it
  affects Quickening.
- Issues that require an already-compromised machine or physical access.

## Safe harbor

I will not pursue or support legal action against anyone who, in good faith,
finds and reports a vulnerability following this policy, avoids privacy violations
and data destruction, and gives me a reasonable chance to fix it before public
disclosure. There is no bug-bounty program — Quickening is free and
donation-supported — but responsible reports are genuinely appreciated and
credited.
