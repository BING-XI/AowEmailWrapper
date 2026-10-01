# Changelog

Each versioned release is described here. The section for a version is also the text of its GitHub
release; CI publishes it when a `v<version>` tag is pushed (see "Releasing a version" in the README).

## Unreleased

- AoWx copies and their turns show AoWx's own grey dragon on the tray menu and in the Activity Log, as
  Ziggurat's do with its purple one.

### Fixed

- The tray menu shows an envelope next to a game again when a turn for it has arrived. Since version 2.0
  the envelope was drawn just past the menu's right edge, out of sight.

## 2.1.2

- Column widths you drag on the Activity Log and the account list are remembered, and come back the next
  time the Wrapper starts.
- Ziggurat copies and their turns show Ziggurat's own purple dragon on the tray menu and in the Activity
  Log. Thanks to BING-XI for the contribution.

### Fixed

- Dragging a column edge or resizing the window no longer makes the text in the lists flash.

Run `AowEmailWrapper-2.1.2-setup.exe` on Windows 10 or later, or let an installed Wrapper fetch it through
*Check for updates* on the Settings tab. Accounts and settings carry over.

## 2.1.1

- Choosing a game on the tray menu while it is already running brings it to the front, restoring it
  if it was minimized. Before, the click did nothing.
- Starting the Wrapper while it is already running, from the Start menu or a desktop shortcut, shows
  the running Wrapper. Before, the second start quietly did nothing.
- "Where is the turn?" on the Activity Log's right-click menu is now called "Who has the turn?", in every
  language.
- When nobody's Wrapper claims a turn, "Who has the turn?" works out who most probably has it: the
  newest send any answer reports (yours included) went to a player who has not answered, typically one
  without the Wrapper. The Status column then says "probably with" that player, and the answer's
  notification says why. The question email now asks who has the turn, too.

### Fixed

- *Show* on the tray icon brings the window back where you left it, at its full size. On some versions of
  Windows it could stay minimized so that nothing appeared, come back as a thin bar, or reappear in the
  middle of the screen.
- The Wrapper no longer closes at start without a word when the port the games hand their turns to is
  already taken, for example by another Windows user's Wrapper. It says which port is busy and which
  setting to change, and keeps running.
- Fixed a rare crash at start when the first mail check began before the window had finished loading.

Run `AowEmailWrapper-2.1.1-setup.exe` on Windows 10 or later, or let an installed Wrapper fetch it through
*Check for updates* on the Settings tab. Accounts and settings carry over.

## 2.1.0

- Clicking the "games waiting" notification starts the game when all the waiting turns belong to one
  copy of it; otherwise it opens the Activity Log.
- The Wrapper looks for a new version once a day while it runs, not only when it starts, so one that
  started offline or runs for days still finds updates. An automatic install waits while a game started
  from the tray is running, a turn is being sent, or settings are unsaved.
- Exit on the tray menu and installing an update offer to save unsaved changes on the Accounts and
  Settings tabs instead of dropping them.
- Every language now has every text. Until now 10 to 24 texts per language quietly appeared in English;
  among them the Where is the turn? feature, new-sender warnings and the Details button of error messages.
- The splash screen no longer stays on screen when the Wrapper loads faster than the splash appears.
- Started from the Start menu, the Wrapper goes straight to the tray instead of leaving an invisible
  minimized window behind that showed up in Alt+Tab.
- A turn that arrives while the Activity Log is being drawn or saved can no longer upset it.
- Switching autostart off removes only this installation's own autostart entry.

Run `AowEmailWrapper-2.1.0-setup.exe` on Windows 10 or later, or let an installed Wrapper fetch it through
*Check for updates* on the Settings tab. Accounts and settings carry over.

## 2.0.9

- A game whose executable came out of a downloaded zip, as a mod's does, starts from the tray menu without
  Windows asking "The publisher could not be verified". In 2.0.8 that question opened behind other windows
  while the Wrapper sat in the tray, so the game seemed not to start. The game is now started directly;
  only when Windows insists on elevation does the usual permission prompt appear.

Run `AowEmailWrapper-2.0.9-setup.exe` on Windows 10 or later, or let an installed Wrapper fetch it through
*Check for updates* on the Settings tab. Accounts and settings carry over.

## 2.0.8

- The games on the tray menu start again. Since version 2.0 the click failed with "The system cannot
  find the file specified", silently, every later click was ignored, and the tray menu itself could
  stop opening until the Wrapper was restarted. A start that fails now says so and the next click
  tries again, and no failure of a tray menu action can leave the menu in that state.
- A turn that arrives is announced with a notification saying how many games are waiting, not only by
  the tray icon turning into an envelope. Windows hides the icon of a newly installed program behind
  the taskbar's arrow, and version 2.0 counts as new even where version 1 ran, so the envelope was often
  out of sight; the manual and quick start say how to keep the icon in view.

Run `AowEmailWrapper-2.0.8-setup.exe` on Windows 10 or later, or let an installed Wrapper fetch it through
*Check for updates* on the Settings tab. Accounts and settings carry over.

## 2.0.7

- *Show* on the tray menu, and a double-click on the tray icon, bring the window back on Windows 11.
  Before, the window was recreated at the off-screen spot where Windows parks minimized windows, at the
  size of a minimized window, so nothing appeared and the Wrapper seemed stuck in the tray. A window that
  lies outside every screen is now moved to the middle of the primary screen.
- Games tab: several copies can be selected at once with Shift or Ctrl and removed together, with one
  question covering every copy the scan found. Ctrl+A selects them all and the Delete key removes the
  selection.
- Changes on the Games tab take effect at once, as *Rescan* already did, instead of waiting for *Save
  Settings*. Before, *Move to* on the Activity Log, the tray menu and the routing of turns kept offering
  a copy that had just been removed until the settings were saved.
- The *Report a bug*, *Set label*, update download, error and rename windows are laid out for the
  screen's scaling and the theme's font, so on a display set to 125% or more their text is no longer
  cut off. *Report a bug* can also be made larger for a long description.

Run `AowEmailWrapper-2.0.7-setup.exe` on Windows 10 or later, or let an installed Wrapper fetch it through
*Check for updates* on the Settings tab. Accounts and settings carry over.

## 2.0.6

- Ziggurat installed by its 2026 installer is recognised: the `Ziggurat` subfolder of the game is listed as
  its own copy, labelled Ziggurat, and the game folder it sits in stays Vanilla even when it carries
  Ziggurat's text tables. The copy is found from the mod's own registry key, and *Add folder...* on the
  game folder picks up both.
- The Wrapper writes its email settings under Ziggurat's own registry name (`Age of Wonders Z`), which
  `AoWz.exe` reads; before, a Ziggurat copy never saw them and could not send turns through the Wrapper.
- The columns of the Activity Log, Accounts and Games lists can be dragged to any width. A width you set
  is kept while the Wrapper runs; the other columns keep sizing themselves.

Run `AowEmailWrapper-2.0.6-setup.exe` on Windows 10 or later, or let an installed Wrapper fetch it through
*Check for updates* on the Settings tab. Accounts and settings carry over.

## 2.0.5

- Corrected the alternate Age of Wonders 1 executable name to `AoWz.exe`. Automatic scans and
  *Add folder...* now recognize copies containing that file; the erroneous `AoWz.com` name from
  version 2.0.4 is no longer accepted. `AoW.exe` remains preferred when both executables are present.
- Corrected the manual and all folder-picker messages to show `AoWz.exe`.

Run `AowEmailWrapper-2.0.5-setup.exe` on Windows 10 or later, or let an installed Wrapper fetch it through
*Check for updates* on the Settings tab. Accounts and settings carry over.

## 2.0.4

- Game detection and *Add folder...* now accept Age of Wonders 1 copies containing `AoWz.com`.
  These copies remain available after restarting the Wrapper. When both `AoW.exe` and `AoWz.com`
  are present, `AoW.exe` remains the preferred executable.
- Updated the manual and all folder-picker messages to include `AoWz.com`.

Run `AowEmailWrapper-2.0.4-setup.exe` on Windows 10 or later, or let an installed Wrapper fetch it through
*Check for updates* on the Settings tab. Accounts and settings carry over.

## 2.0.3

- Games tab: *Remove* works on every copy. A copy the scan found is ignored from then on instead of
  coming back on the next start; *Add folder...* on that folder brings it back.
- Games tab: the Default column sits next to Mod, and the Folder column is last and wide enough for its
  longest path, so the list scrolls sideways instead of cutting paths short.

Run `AowEmailWrapper-2.0.3-setup.exe` on Windows 10 or later, or let an installed Wrapper fetch it through
*Check for updates* on the Settings tab. Accounts and settings carry over.

## 2.0.2

- Classic Windows is the default theme; the Age of Wonders theme is a choice on the Settings tab. A theme
  you already chose is kept.
- Age of Wonders theme: no flicker when hovering over tabs, pages paint in one go instead of control by
  control, and the window is composited so switching tabs shows the finished page.
- Account and activity lists no longer cut off bold rows; columns are measured with the row font.

Run `AowEmailWrapper-2.0.2-setup.exe` on Windows 10 or later, or let an installed Wrapper fetch it through
*Check for updates* on the Settings tab. Accounts and settings carry over.

## 2.0.1

### New

- **Age of Wonders theme.** The window now dresses itself in parchment and dark leather with gold trim
  and a serif for headings. The *Theme* setting on the Settings tab switches between it and *Classic
  Windows*; the change applies at once and is saved with the other preferences.
- **Who sent it.** Every received turn records its sender and every sent turn its recipients. A turn
  from an address that has never taken part in your games is marked *new sender* in the Activity Log
  and announced once in a balloon. On the first connection after upgrading, each IMAP account's inbox
  and Sent folder are read by structure only to learn your existing opponents, so nobody you already
  play with is flagged.
- **Picking up on a new PC.** On a fresh install the same scan works out which games you still owe a
  turn on and downloads just those turns.
- **Where is the turn?** Right-click a sent turn to ask every other player's Wrapper who holds the game
  now. Answers appear on the activity row and in a balloon; the queries never reach a mail client.
- **Mods are recognised.** The Games tab labels each copy from what its folder contains: Ziggurat,
  AoWx, Dark Lord, Evolved, or Vanilla 1.36 for the stock game. Turns land in the right copy. The
  label dialog offers every mod label and moves one that another copy holds. Double-click a copy to
  open its folder; the actions are on the right-click menu as well.
- **Account wizard.** Shorter, plainer text on every page ("Look up the mail provider for this
  domain" instead of "Try Mx Lookup"), and the Chief Librarian looks up your settings by communing
  with the Thunderbird.

### Security

- Attachment file names from email are reduced to a plain name and every write is checked to stay
  inside its target folder. Previously a crafted name could choose its own destination.
- Account discovery only trusts encrypted sources and encrypted mail servers: no certificate bypass,
  no plain `http://` lookups, no fallback to an unencrypted probe.
- Decoded attachments and inflated saves are capped in size, so a small crafted file can no longer
  exhaust memory.

### Fixed

- A Games-tab rescan is applied and saved immediately.
- The build pipeline publishes a pre-release for every commit on master again.
- Dead code removed throughout, including an unused WMI helper and its package.

### Installing

Run `AowEmailWrapper-2.0.1-setup.exe` on Windows 10 or later. It installs for the current user only
and downloads the .NET 8 Desktop Runtime from Microsoft if it is missing. Existing accounts and
settings carry over. A Wrapper that is already installed offers this release through *Check for
updates* on the Settings tab, or installs it by itself when *Install updates automatically* is on.

## 2.0.0

Version 2.0 is a full rebuild of the 2013 Wrapper on .NET 8 so play-by-email works again with today's
email providers.

- Works with Gmail (App Passwords) and Outlook.com, Hotmail, Live and Microsoft 365 through Sign in
  with Microsoft.
- Test connection buttons on the Incoming and Outgoing tabs report exactly why a sign-in failed.
- Turns arrive immediately on IMAP accounts (IMAP IDLE) instead of every ten minutes, and only emails
  that carry a save game are downloaded.
- Mod support: label your Vanilla, AoWx and Ziggurat copies on the Games tab and turns land in the
  right copy automatically. The label travels with each turn you send.
- The Games tab finds every copy of AoW 1, AoW 2, Shadow Magic and MP Evolution on the PC, including
  Steam and GOG installs.
- Automatic updates from GitHub releases, a per-user installer with no administrator prompt, and an
  uninstaller that cleans up after itself.
- Settings tab with a Support section: Quick start guide, Manual, Check for updates, Open log folder
  and Report a bug.
- Passwords are stored encrypted with Windows (DPAPI). Existing settings are migrated on first start.
- New Quick Start Guide and manual, installed with the program and linked from the Start Menu.
- About tab with Discord servers for the PBEM community and a dedication to the fallen.

Original concept, development and testing by Bryan S. Carter and David N.T. Honess. Version 2.0 by
Eugene Wolff.
