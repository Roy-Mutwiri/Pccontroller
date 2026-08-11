# Project instructions for Claude Code

## Git — auto-push policy (explicit user instruction, 2026-08-10)

This project is pushed to **https://github.com/Roy-Mutwiri/Pccontroller** (remote `origin`,
branch `main`).

The user has explicitly authorized committing and pushing to this remote **automatically, for
every change, without asking first** — this overrides the general default of confirming before
`git push`. This authorization is scoped to this repository only.

In practice: after making any code/doc change in this project, stage it, commit with a concise
message describing the change, and push to `origin main` — do this as a normal part of finishing
the change, not as a separate step the user has to request.

Still exercise judgment on what NOT to push automatically without flagging it first:
- Anything that looks like a secret/credential/API key, even in a file that seems unrelated.
- Force-pushes, history rewrites, or anything that would overwrite someone else's commits.
- Destructive git operations (reset --hard, branch deletion, etc.) — none of this blanket
  authorization covers those; it's specifically about routine add/commit/push of ordinary work.

## Distributable release — keep-updated policy (explicit user instruction, 2026-08-11)

Roy installs TradeFix Broadcast on other PCs (Render Nodes) from a GitHub Release, not a live
download link — see the README's "Install (end users)" section. The standing release is:

**https://github.com/Roy-Mutwiri/Pccontroller/releases/tag/dist-2026-08-11**

with two assets: `TradeFixBroadcast-recommended.zip` (`installer\` + `publish\`, the currently
verified `.bat`-based install path) and `TradeFixBroadcast-setup-exe.zip` (`dist\`, the newer
single-file `TradeFix.Setup.exe` installer).

Roy wants this release kept current automatically as a normal part of finishing a change, the same
way `git push` is — not a separate step he has to request each time. In practice, after a round of
changes that affects what actually ships (anything under `src/`, `installer/`, or shared
libraries — not doc-only edits):

1. Run `installer\Build-Distributable.ps1` to rebuild `publish\` and `dist\` from the latest commit.
2. Confirm the build actually succeeded (check for build errors — never publish a build that
   failed or is known-broken).
3. Re-zip both packages and upload them to the **same** release/tag with `--clobber` (overwrite in
   place), e.g.:
   `gh release upload dist-2026-08-11 <zip> --repo Roy-Mutwiri/Pccontroller --clobber`
   Keep the tag/link stable — the point is Roy always uses the same URL on PC 2/3 and gets the
   latest build, not a new link every time.

The repo is public, so this release (and the installer binaries in it) is publicly visible/
downloadable by anyone who finds it — Roy has already acknowledged and accepted this.
