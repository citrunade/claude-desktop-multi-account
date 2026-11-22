# Claude Accounts for Windows

Use several Claude accounts side by side on one PC.

- **One shortcut per account.** Each stays signed in on its own and never merges with the others.
- **Its own taskbar button and icon.** Pick the Claude icon in a colour, the normal Claude icon, a number, or your own picture.
- **Claude Tracker** (optional): a small always-on-top panel showing each account's plan limits and the Claude Code conversations waiting for you. Pin or finish conversations, see how much context each one uses, start a new chat on any account, or hand a conversation over to another account.

## Install

1. Install the Claude desktop app from <https://claude.ai/download>.
2. Download this repository (green **Code** button → **Download ZIP**) and unzip it.
3. Double-click **install.cmd**. Say how many accounts you want, and whether you want the tracker panel.
4. Open each new shortcut once and sign in to a different account.

To keep one on the taskbar, open it, right-click its taskbar button and choose **Pin to taskbar**.

Windows may warn that the file came from the internet: choose **More info → Run anyway**. Nothing is downloaded or precompiled. Everything is plain source (`install.ps1`, `src/`), built on your PC by the .NET compiler that ships with Windows.

## Adding accounts and changing icons later

Use the **gear button** on the tracker panel, or **Start menu → Claude Accounts → Manage Claude accounts**. From there you can:

- add an account (it opens straight away on Claude's sign-in screen)
- rename one
- choose its icon and colour
- remove one (it's closed first), keeping or deleting its sign-in
- choose how long a conversation waiting for you stays in the tracker panel (30 minutes to 1 week; pinned ones always stay)

## How it works

- Every account gets a small launcher in `%USERPROFILE%\Apps\Claude Account N` that gives its windows their own taskbar button and icon.
- **Account 1** is the normal Claude app and its usual sign-in.
- **Accounts 2 and up** start Claude with their own data folder (`%APPDATA%\Claude-N`), which is why the sign-ins stay separate.
- **Claude Tracker** lives in `%USERPROFILE%\Apps\Claude Tracker` and starts with Windows.
  - It reads Claude's own files on your PC; nothing is sent anywhere.
  - Plan-limit readings come from a tiny local Claude Code plugin it registers (`plugins/limits-beacon`). The plugin makes no model calls.

## Uninstall

Double-click **uninstall.cmd**. Your sign-ins are kept. To remove them too, run `uninstall.cmd -RemoveSignIns`.
