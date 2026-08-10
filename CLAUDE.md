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
