# Claude Desktop: log into multiple accounts at once

**See every account's usage and active conversations in one small taskbar widget.**

Bonus: track each conversation's context, pin conversations, open any conversation in one click, start new ones on any account, hand a conversation to another account automatically when it hits its limit, and give each account its own icon.

For Windows. Nothing to download but this source: it builds on your PC with the compiler that ships with Windows.

<p align="center">
  <img src="docs/panel.png" width="380" alt="The Claude Tracker panel: three accounts with their 5-hour and weekly usage, and their conversations grouped as pinned, working, new, waiting and cut by the limit">
  &nbsp;&nbsp;
  <img src="docs/settings.png" width="420" alt="The accounts window: name and icon for each account, plus settings for hiding conversations and handing off at the limit">
</p>
<p align="center">
  <img src="docs/bar.png" width="216" alt="The bar on the taskbar: 34% of the 5-hour limit used, 2 new, 2 busy, 1 cut">
</p>
<p align="center"><sub>Sample accounts and conversations. Above: the panel and the settings window. Below: the bar that sits on the taskbar.</sub></p>

## What it does

### Several accounts, signed in at the same time
- **One shortcut per account.** Each opens its own Claude window and stays signed in on its own; the accounts never merge or sign each other out.
- **Its own taskbar button and icon.** Pick the Claude icon in any colour, the normal Claude icon, a number, or your own picture, so you can tell the windows apart at a glance.
- **Add an account any time.** It opens straight on Claude's sign-in screen. Rename, recolour or remove accounts later.

### The taskbar widget (Claude Tracker)
A small always-on-top panel, opened from its icon by the clock. The icon shows how much of your 5-hour limit you've used.

- **A bar on the taskbar** shows your 5-hour use and how many conversations are new, busy or cut. Drag it left or right to any free spot on the taskbar.
- **The panel opens above the bar.** Choose separately for when a Claude window is in front and for everything else: it **stays open**, or it **opens when the mouse rests on the bar** and fades away when the mouse leaves. Switch with the two small toggles at the bottom of the panel.
- **Switching windows is instant:** the panel appears or disappears the moment you go to or from Claude. When Claude is minimised or brought back, it fades along with it.
- **The conversation open in each account's window is highlighted**, a little stronger for the window in front.

- **Usage per account:** 5-hour and weekly limits with how long until each resets.
- **Active Claude Code conversations per account**, grouped:
  - **Working:** Claude is replying right now.
  - **New, not seen:** finished while you were away.
  - **Seen, waiting for you.**
  - **Cut by the 5-hour limit**, with when you can resume.
- **Context used** by each conversation (for example "132k context").
- **One click** opens a conversation in the right account's window.
- **+ New** starts a new Claude Code chat on that account.
- **Pin** a conversation to keep it at the top; **finish** it to clear it away (finishing unpins it).
- **Hand off** any conversation to another account: a new chat opens there with a summary of the conversation already typed in.

### Automatic hand-off at the limit
When a conversation hits its usage limit, the tracker can move it to the account with the most room left. Choose to:
- do nothing,
- get a notification you click to move it,
- move it and press Enter yourself, or
- **move and send it by itself.** It brings the new chat to the front, holds your mouse and keyboard still for about half a second so the key can't land anywhere else, and presses Enter. If that window isn't in front, it doesn't send and tells you instead.

You choose:
- **how to pick the account:** most weekly limit left, most 5-hour limit left, or most left on both. Room counts for more when a limit resets soon, because what's left is spread over the time until the reset.
- **when an account is too full to take it:** for example once it has used 90% of its weekly or 5-hour limit.

## Install

1. Install the Claude desktop app from <https://claude.ai/download>.
2. Download this repository (green **Code** button → **Download ZIP**) and unzip it.
3. Double-click **install.cmd**. Say how many accounts you want, and whether you want the widget.
4. Open each new shortcut once and sign in to a different account.

To keep an account on the taskbar, open it, right-click its taskbar button and choose **Pin to taskbar**.

Windows may warn that the file came from the internet: choose **More info → Run anyway**. Nothing is precompiled. Everything is plain source (`install.ps1`, `src/`), built on your PC by the .NET compiler that ships with Windows.

## Settings

Use the **gear button** on the widget, or **Start menu → Claude Accounts → Manage Claude accounts**:

- add, rename, recolour or remove accounts (removing closes the account first, and you choose whether to keep its sign-in)
- how long a waiting conversation stays in the widget (30 minutes to 1 week; pinned ones always stay)
- what happens when a conversation hits its limit, how the receiving account is picked, and when an account is too full

## How it works

- Every account gets a small launcher in `%USERPROFILE%\Apps\Claude Account N` that gives its windows their own taskbar button and icon.
- **Account 1** is the normal Claude app and its usual sign-in.
- **Accounts 2 and up** start Claude with their own data folder (`%APPDATA%\Claude-N`), which is why the sign-ins stay separate.
- **Claude Tracker** lives in `%USERPROFILE%\Apps\Claude Tracker` and starts with Windows.
  - It reads Claude's own files on your PC. Nothing is sent anywhere.
  - Usage readings come from a tiny local Claude Code plugin it registers (`plugins/limits-beacon`). The plugin makes no model calls and uses no tokens.

## Uninstall

Double-click **uninstall.cmd**. Your sign-ins are kept. To remove them too, run `uninstall.cmd -RemoveSignIns`.

## License

MIT
