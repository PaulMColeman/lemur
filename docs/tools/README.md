# Retaking the manual's screenshots

The pictures in `docs/help/img` come from a separate demo copy of Lemur with made-up meetings, people and notes.
Nothing in them is real, and the demo never touches your own settings, recordings or keys. A demo copy runs with
`LEMUR_HOME` set:
- its settings and data live in that folder;
- its keys are `Lemur.<hash>:<name>` in Credential Manager, not `Lemur:<name>`;
- it never changes start-at-sign-in or the `lemur://` links;
- it can run beside your real Lemur.

Captures use `PrintWindow`, which takes only that window's pixels. Never use a screen capture: it picks up whatever
else is on screen.

The demo runs as `LemurDemo.exe` (a copy of the build, renamed) and every `ui.ps1` call passes `-Process LemurDemo`.
`ui.ps1` finds windows by process name and title, so with your real Lemur running, `-Title Recordings` under the default
`-Process Lemur` would capture *your* Recordings window, with your meetings in it. Running from a copy also keeps
`bin\` free for the next build.

| File | What |
|---|---|
| `demo/` | `dotnet run -- <home> working\|screens` writes the demo world into `<home>`: nine meetings (done, one titled from its summary, the design review marked Keep recording, the oldest without its audio or MP3, retrying, being summarized, waiting), five people, a vocabulary, and real notes made by `NoteWriter` (most of them say "Cooper Netties", for Fix a word). `working` points the backend at `stub.mjs`; `screens` points it at `example.lan`. |
| `stub.mjs` | A stand-in Scriberr on `127.0.0.1:18080`. It answers the connection test, holds every summary request open so a meeting stays "Summarizing…", and answers title requests ("Suggest a title") with a made-up title. It never logs headers, which carry the demo key. |
| `ui.ps1` | Drives and captures Lemur's windows: `list`, `shot`, `click`, `tab`, `select`, `settext`, `type`, `controls`, `close`. Clicks and tab changes are posted window messages, because a UI Automation Invoke on a button that opens a modal dialog never returns. A window opened while Lemur wasn't the active app (Settings, from a posted click) is owned by a hidden window and missing from UI Automation's tree, so windows are also found by handle. Four more actions refuse any process but `LemurDemo`, since they read or type over what a window holds: `combos` (each combo box's text and list), `retype -Index n -Value …` (empty combo box n and type into it, so its suggestions can fill in), `timing -Index n -Value …` (type at the end of combo box n and time how long the window takes to take the keys in), `dialogs` (print each message box's text and press its first button). |
| `note-shot.mjs` | The demo note page in headless Edge, with a transcript line playing. |
| `note-moment.mjs` | A note page opened at a moment (`page.html#t=<seconds>`, as `lemur://note/` links open it) in headless Edge, with real clicks and the browser's usual autoplay rule: the line said then is lit and in view, "Play from" plays it, a changed `#t=` moves, a summary item's time plays, and nothing is logged (a CSP violation would be). `node note-moment.mjs <page.html> [seconds] [other seconds]`; exits 1 on a failed check. |
| `page-shot.mjs` | A help page in headless Edge. It reports any request that isn't `file:` and any horizontal scrolling. |

## Steps (PowerShell, from the repo root)

```powershell
dotnet publish src\Lemur -c Release -o publish; dotnet publish src\Lemur.Cli -c Release -o publish
$demo = "$env:TEMP\lemur-demo-app"                # the app, copied and renamed
Remove-Item -Recurse $demo -ErrorAction SilentlyContinue
Copy-Item -Recurse publish $demo
Rename-Item "$demo\Lemur.exe" "LemurDemo.exe"
$app = "$demo\LemurDemo.exe"
$cli = "$demo\llmmr.exe"
$ui  = "docs\tools\ui.ps1"
$env:LEMUR_HOME = "C:\sandbox\lemur-demo"        # a path without your user name: it shows on the About tab

# 1. A meeting being summarized (recordings.png), a finished one (recordings-done.png), a search (recordings-search.png),
#    the rename window (rename.png) and Fix a word (fix-word.png).
node docs\tools\stub.mjs                         # in a second terminal; leave it running
dotnet run --project docs\tools\demo -- $env:LEMUR_HOME working
'demo-key-not-a-secret' | & $cli set-secret scriberr-api-key
Start-Process $app; Start-Sleep 4; Start-Process $app  # the second start opens the Recordings window
# Wait about a minute so the step shows real time.
powershell -File $ui -Process LemurDemo -Action select -Title Recordings -Name "Quarterly planning"
powershell -File $ui -Process LemurDemo -Action shot -Title Recordings -Out recordings.png
powershell -File $ui -Process LemurDemo -Action select -Title Recordings -Name "Design review"
powershell -File $ui -Process LemurDemo -Action shot -Title Recordings -Out recordings-done.png
powershell -File $ui -Process LemurDemo -Action click -Title Recordings -Name "Rename"
powershell -File $ui -Process LemurDemo -Action shot -Title "Rename meeting" -Out rename.png
powershell -File $ui -Process LemurDemo -Action click -Title "Rename meeting" -Name "Cancel"
powershell -File $ui -Process LemurDemo -Action settext -Title Recordings -Index 0 -Value calendar   # the search box
powershell -File $ui -Process LemurDemo -Action shot -Title Recordings -Out recordings-search.png
# Fix a word: the demo's meetings say "Cooper Netties", which isn't in its vocabulary yet. A UI Automation value doesn't
# raise a box's change event (the Fix button stays grey), so type into it with posted WM_CHAR messages instead.
powershell -File $ui -Process LemurDemo -Action settext -Title Recordings -Index 0 -Value "Cooper Netties"
powershell -File $ui -Process LemurDemo -Action click -Title Recordings -Name "Fix a word"
powershell -File $ui -Process LemurDemo -Action type -Title "Fix a word" -Index 1 -Value Kubernetes   # "It should say"
powershell -File $ui -Process LemurDemo -Action shot -Title "Fix a word" -Out fix-word.png
powershell -File $ui -Process LemurDemo -Action close -Title "Fix a word"

# 2. Settings and naming (example.lan addresses).
& $app --quit
dotnet run --project docs\tools\demo -- $env:LEMUR_HOME screens
Start-Process $app; Start-Sleep 4; Start-Process $app
powershell -File $ui -Process LemurDemo -Action select -Title Recordings -Name "Design review"
powershell -File $ui -Process LemurDemo -Action click -Title Recordings -Name "Name speakers"   # then shot -Title speaking, close -Title speaking
powershell -File $ui -Process LemurDemo -Action click -Title Recordings -Name "Settings"
$tabs = "account", "recording", "notes", "vocabulary", "people", "about"
0..5 | ForEach-Object { powershell -File $ui -Process LemurDemo -Action tab -Title settings -Index $_; powershell -File $ui -Process LemurDemo -Action shot -Title settings -Out "settings-$($tabs[$_]).png" }

# 3. The setup wizard: an empty LEMUR_HOME starts it.
& $app --quit
$env:LEMUR_HOME = "C:\sandbox\lemur-demo-new"
'demo-key-not-a-secret' | & $cli set-secret scriberr-api-key; Remove-Item -Recurse "$env:LEMUR_HOME\config"
Start-Process $app    # then shot -Title "Set up"; click -Name Next; settext -Index n -Value ...

# 4. The note page.
node docs\tools\note-shot.mjs "C:\sandbox\lemur-demo\Meeting Notes\<date> Design review - onboarding flow.html" note-page.png
```

When you're done:
- Trim the 8 px black frame `PrintWindow` leaves at the left, right and bottom of each window.
- Save the pictures into `docs/help/img` under the names the pages use.
- Quit the demo (`& $app --quit` with `LEMUR_HOME` still set) and stop the stub.
- Delete the demo keys: `cmdkey /list`, then `cmdkey /delete:Lemur.<hash>:scriberr-api-key`.
- Delete the demo folders and `$demo`.
