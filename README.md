# Lemur

**Lemur** (LLM Meeting Recorder, command line `llmmr`) records your meetings without you having to remember to,
and turns each one into notes: a transcript where **you are named** and everyone else is labelled, plus a summary of
**action items (with owners), decisions, open questions and concepts**. Each note is saved in `Documents\Meeting Notes`
as Markdown, as a web page and as an MP3.

The speech recognition and the summary run on **servers you run**: nothing goes to a cloud service.

> **Users:** start with the help pages in [`docs/help/`](docs/help/index.html): an overview, the
> [user manual](docs/help/manual.html) and the [self-hosting guide](docs/help/self-hosting.html). The installer puts
> them beside the app, and the tray menu's *Help* opens them.

- **Starts by itself** when a call starts — Teams in Edge or Chrome, the Teams app or Teams classic; Google Meet in
  Edge or Chrome, Zoom (the app, or in Edge or Chrome) and GoTo Meeting are built in but not yet tried on real calls —
  and stops when the call ends.
  Back-to-back meetings are split into separate recordings.
- **In-person meetings:** tray icon → *Start recording now*.
- **Two tracks:** your microphone and "everyone else" (a loopback copy of what your headset or speakers play) are
  recorded separately and kept aligned, which is how it knows which voice is you.
- **Follows the call's devices:** if the call uses a headset that isn't the Windows default, that headset is what gets
  recorded.
- **Finds meetings again:** the Recordings window searches every meeting's notes and transcript, filters by date, and
  shows a one-line description of each, written with the summary. A meeting the call app didn't name (Meet, Zoom, GoTo,
  a manual recording) takes the title its summary suggests; rename any meeting and its note's files follow.
- **Learns your words:** a vocabulary of names the transcription gets wrong ("Cooper Netties" → Kubernetes), fixed in
  every transcript before the summary is written; *Fix a word…* in the Recordings window fixes one in the notes already
  made too.
- **Server is a setting:** a [Scriberr](https://github.com/rishikanthc/Scriberr) instance for transcription, and any
  OpenAI-compatible gateway (LiteLLM, vLLM, llama.cpp, Ollama) for summaries. Nothing is hard-coded to one domain, so the
  whole thing can move.

> **Tell people you're recording.** Lemur reminds you each time a recording starts. Some places (e.g. Illinois,
> California) require everyone's consent, and your employer may have its own rules.

## Status

1.0 — the first packaged release: a per-user MSI installer (WiX, in `installer/`), a first-run setup window, a
Recordings window that searches your meetings and shows each one's steps, and help pages. Before 1.0 the project was called Teams Recorder;
Lemur moves Teams Recorder's settings, recordings and keys over on its first start. How each part works, and why, is in
[`docs/architecture.md`](docs/architecture.md).

## Requirements

- Windows 10 2004+ or Windows 11, x64
- .NET 10 SDK to build
- Servers: Scriberr for transcription (or an OpenAI-compatible endpoint with diarization,
  `response_format=diarized_json`) plus a chat model for summaries. The
  [self-hosting guide](docs/help/self-hosting.html) lists the servers, models and hardware, with setup steps.

## Build, test, run

```powershell
dotnet build Lemur.slnx
dotnet test Lemur.slnx
dotnet publish src/Lemur     -c Release -o publish
dotnet publish src/Lemur.Cli -c Release -o publish
.\publish\Lemur.exe          # tray app
.\publish\llmmr.exe --help   # command line (setup, diagnosis, testing)
```

The installer (`installer\out\Lemur-1.2.0.msi`, per-user, self-contained) needs the WiX 6 tool and its UI extension:

```powershell
dotnet tool install --global wix --version 6.0.1
wix extension add -g WixToolset.UI.wixext/6.0.1
.\installer\build.ps1
```

| Path | What |
|---|---|
| `src/Lemur.Core` | everything testable: settings, call detection, audio, transcripts, backends, the pipeline, speaker names |
| `src/Lemur` | the tray app (`Lemur.exe`) and its windows (WinForms) |
| `src/Lemur.Cli` | `llmmr`: setup, diagnosis and pipeline testing |
| `tests/Lemur.Tests` | xUnit; no audio hardware or network needed |
| `server/voiceprint` | the voiceprint service (ASP.NET + sherpa-onnx, Docker) |
| `docs/help` | the help pages shipped with the app (`help\` beside `Lemur.exe`) |
| `installer` | the WiX installer |

## Set up

The first start shows a setup window. Settings live in `%APPDATA%\Lemur\settings.json` (`llmmr settings` shows the path
and anything missing). API keys live in **Windows Credential Manager** as `Lemur:<name>` — never in the settings file:

```powershell
Get-Clipboard | .\publish\llmmr.exe set-secret scriberr-api-key   # or transcription-api-key / summary-api-key
.\publish\llmmr.exe settings                                      # "status: ready" when complete
```

### Scriberr backend

```jsonc
"backend": {
  "type": "scriberr",
  "scriberr": {
    "baseUrl": "https://scriberr.example.lan",
    "apiKeySecret": "scriberr-api-key",       // an API key from Scriberr → Settings → API keys
    "profileName": "Meeting (GPU, pyannote)", // empty = Scriberr's default profile
    "summaryModel": "meeting-summary",        // empty = Scriberr's default summary model
    "summaryTemplateName": null
  }
},
"summary": { "baseUrl": "https://llm.example.lan/v1", "apiKeySecret": "summary-api-key", "model": "meeting-summary" }
```

With Scriberr, Scriberr writes the summary; `summary.*` is used for the introductions check ("hi, it's Dana").

### OpenAI-compatible backend

```jsonc
"backend": {
  "type": "openai",
  "openAi": {
    "transcriptionBaseUrl": "https://gateway.example.lan/v1",
    "transcriptionApiKeySecret": "transcription-api-key",  // null if the endpoint needs no key
    "transcriptionModel": "whisper-1",
    "responseFormat": "diarized_json",
    "language": "en",
    "extraFields": {}                                       // e.g. { "chunking_strategy": "auto" }
  }
},
"summary": { "baseUrl": "https://gateway.example.lan/v1", "apiKeySecret": "summary-api-key", "model": "meeting-summary" }
```

### Other settings worth knowing

| Setting | Default | What it does |
|---|---|---|
| `displayName` | your Windows user name | how you appear in transcripts and action items |
| `notesFolder` | `Documents\Meeting Notes` | where notes are written |
| `keepAudioDays` | 14 | working audio of processed meetings is deleted after this (-1: never), except meetings marked Keep recording in the Recordings window (`llmmr keep`) |
| `noteMp3.deleteWithAudio` | false | delete each note's MP3 too, to the Recycle Bin, after `keepAudioDays`; the note and transcript stay, the page loses its player |
| `detection.startAfterSeconds` / `stopAfterSeconds` | 3 / 15 | how long the mic must be in use / released |
| `detection.minAutoRecordingSeconds` | 60 | shorter automatic recordings are discarded |
| `detection.joinScreenTitleContains` | `Meeting join` | Teams' join screen title, used to split back-to-back meetings |
| `detection.sources` | Teams, Google Meet, Zoom, GoTo | the call apps to watch; add your own rules |
| `audio.followCallDevices` | true | record the devices the call uses, not the Windows defaults |
| `audio.micDeviceContains` / `outputDeviceContains` | null | pin a device by (part of) its name |
| `summary.maxTokens` | 16384 | room for the answer, including a reasoning model's thinking (32768 for a local reasoning model) |
| `vocabulary` | [] | words the transcription gets wrong: `[{ "term": "Kubernetes", "heardAs": ["Cooper Netties"] }]`; a term with no `heardAs` is only spelled out for the summary model, in meetings that have it |
| `summary.promptFile` | null | your own summary prompt; `{name}` becomes your display name. Start the reply with `Title:` and `About:` lines (as the built-in prompt does) to keep titles and descriptions coming with the summary; without them Lemur asks the summary model separately |

### Speaker names (optional)

Names the other people in a meeting — by voice, and when they introduce themselves ("hi, it's Dana") — so notes say
"Dana Smith" instead of "Speaker B". How it works: "Speaker names" in `docs/architecture.md`.

- **Introductions** need only the summary model (`summary.baseUrl` + `summary.model`) and are on by default.
- **Voices** need a voiceprint model on your server, and are **off by default**: they keep voiceprints of other people,
  which several laws treat as biometric data. Run the service in [`server/voiceprint`](server/voiceprint/README.md)
  (Docker, CPU is enough) with NeMo TitaNet-L (`nemo_en_titanet_large`), the model with built-in matching numbers, and
  register it in LiteLLM as an embeddings model. Then:

  | Setting | Default | What it does |
  |---|---|---|
  | `speakers.enabled` | false | recognise voices |
  | `speakers.baseUrl` | "" (= `summary.baseUrl`) | OpenAI-style base URL of the voiceprint model |
  | `speakers.model` | `speaker-embed-local` | its model name |
  | `speakers.apiKeySecret` | `voiceprint-api-key` | its key; if missing and the server is the summary's, the summary key is used |
  | `speakers.peopleFolder` | `%APPDATA%\Lemur\people` | point two PCs at one OneDrive folder to share the people you've named |
  | `speakers.level` | 1 (Careful) | 1–5, Careful → Eager |
  | `speakers.askConsent` | true | ask "did they agree?" before remembering a voice |

  `llmmr voices calibrate` checks the model answers and that matching numbers exist for it (built in for TitaNet-L;
  another model needs `llmmr voices benchmark` and its output in `speakers.thresholds`).
- **Naming someone:** click *Name speakers* in a meeting's page, the notes notification, the Recordings window or the
  tray menu; or `llmmr people name <recording> "Speaker B" "Dana Smith" --remember --consent "<note>"`.

## Command line (`llmmr`)

| Command | Use |
|---|---|
| `llmmr settings` | paths and anything missing |
| `llmmr set-secret <name>` / `llmmr secret-status <name>` | store a key from stdin / check one exists |
| `llmmr devices` | microphones and outputs, with the Windows defaults |
| `llmmr detect [--seconds N]` | what call detection sees each second — run it during a call if recording doesn't start |
| `llmmr selftest [--mic name]` | plays 3 soft beeps while recording and checks both tracks line up |
| `llmmr record --seconds N [--play file.wav] [--process]` | a manual recording from the command line |
| `llmmr make-test-recording --mic a.wav --others b.wav,c.wav` | build a recording from existing audio (no speakers needed) |
| `llmmr process <folder> [--backend scriberr\|openai] [--from mixed] [--notes <folder>]` | (re)run the pipeline on a recording |
| `llmmr rename <recording> "<title>"` | retitle a meeting; its note's .md, .html and .mp3 are renamed to match |
| `llmmr describe <recording> [--show] [--apply]` | ask the summary model for a title and one-line description from the summary; `--show` prints them, `--apply` keeps them |
| `llmmr vocab list\|add\|remove` | the vocabulary: `llmmr vocab add "Kubernetes" "Cooper Netties"` |
| `llmmr vocab fix <recording> \| --all` | fix the vocabulary's words in meetings already made |
| `llmmr identify <recording>` | re-run speaker naming and show what it found (notes unchanged) |
| `llmmr people list\|name\|rename\|merge\|delete\|export\|import` | the people you've named |
| `llmmr voices calibrate` / `llmmr voices benchmark --ami … --annotations …` | check the voiceprint model / fit numbers for a new one |

Don't `llmmr process` a recording in Lemur's own recordings folder while the tray app runs: both would work on it. Use a
copy elsewhere, with `--notes <scratch folder>`.

## Where things are

| What | Where |
|---|---|
| Settings | `%APPDATA%\Lemur\settings.json` |
| Recordings (audio, transcript, state) | `%LOCALAPPDATA%\Lemur\recordings\<yyyyMMdd-HHmmss>\` |
| Logs | `%LOCALAPPDATA%\Lemur\logs\` |
| API keys | Credential Manager → Windows Credentials → `Lemur:<name>` |
| Notes | `notesFolder` |
| People you've named (voiceprints, reference clips) | `speakers.peopleFolder` |

## Known limitations

- **"Everyone else" is all PC audio on the call's output device.** A video or a notification sound on the same device
  during a call ends up in the transcript. Teams-in-Edge and YouTube-in-Edge are the same process, so per-app capture
  can't separate them; pause other audio during calls.
- **Browser tab titles:** a browser call is detected from window titles, which show each window's *current* tab. A call
  started from a background tab is picked up once you switch to it (or use *Start recording now*).
- **Scriberr's summary limit:** Scriberr waits 5 minutes for the summary model, so a slow model can't summarize long
  meetings.
- **Scriberr is single-account** — everyone's meetings share one library. For other people use their own Scriberr, or the
  OpenAI-compatible backend with their own key.

## Licence

MIT: see [`LICENSE`](LICENSE). The projects and models Lemur builds on keep their own licences, listed under
"Credits and licences" in [`docs/help/index.html`](docs/help/index.html).
