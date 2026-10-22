# How Lemur works (the parts that are not obvious)

Read the section for an area before changing it. Paths are under `src/Lemur.Core` unless they start with `Windows/`
(`src/Lemur`). Paul's own servers and PC (`docs/deployment-paul.md`), open work (`docs/roadmap.md`) and the dated design
docs (`docs/2026-…-design.md`) are kept in his own repository, not in the public copy.

## Recording

### Call detection
`Detection/CallDetector.cs`, a pure state machine, 1 tick/s: the mic-in-use flag from
`HKCU\...\CapabilityAccessManager\ConsentStore\microphone` (LastUsedTimeStop = 0), matched against `WatchRule`s.

- Browser rules also need their app's window (`… | Microsoft Teams`, or Meet's `Meet - abc-defg-hij` via
  `WindowTitleMatches`). Browsers share one mic entry, so Pending tries every rule in use and the first whose window
  shows wins.
- The browser must **also** appear in Windows' active capture sessions: a crash leaves the registry saying "in use"
  forever (this PC has a 2024 Discord entry like that). Entries started > 12 h ago are ignored.
- Start after 3 s, stop 15 s after release, auto-recordings < 60 s discarded (Teams' pre-join mic preview).
- Built-in rules: Teams (Edge/Chrome/app/classic), Google Meet (Edge/Chrome), Zoom (`#Zoom.exe`, and Edge/Chrome by a
  tab titled `Zoom` or `… - Zoom`), GoTo (`#GoTo.exe`, classic `#g2m`). Zoom in a browser had no rule until 2026-10-21:
  Paul's Zoom call in Edge wasn't recorded. The web client's join page (`app.zoom.us/wc/`) is titled `Zoom`; the
  in-call title isn't seen yet (`llmmr detect` on the next one). Rules added to the built-ins later are appended to older settings files on load (`SettingsStore`);
  users untick, not delete.
- Seen on real calls: Teams in Edge; **Google Meet in Edge recorded a real 31-min call** (2026-10-14 07:36; no title:
  Meet's window shows only its code). Zoom and the GoTo app are from docs, not yet seen on a real call.
- **GoTo in a browser has no rule**: a GoTo meeting in Edge (2026-10-14 09:18) was recorded as "Teams (Edge)" because a
  Teams tab was open in another Edge window. Run `llmmr detect` during a GoTo browser call to see its title before
  adding one.
- **Muting in Teams (Edge) does not release the mic** (verified 2026-10-14 with `llmmr detect`).

### Back-to-back meetings
`CallDetector.NextMeeting` → `NextCall`. Leaving a Teams meeting and joining the next took the mic back after **2 s**
(Paul's test, 2026-10-14), inside the 15 s stop delay, so it used to be one recording. Now a mic release + re-take (a
tick without it, or a new `LastUsedTimeStart`) followed within 120 s by the call window showing the join screen
(`detection.joinScreenTitleContains`, "Meeting join") or a different meeting name splits the recording. Rejoining the
same meeting splits too (it shows the join screen); a device change doesn't (same meeting). Teams web titles:
`Calendar | Meeting join | <name> | Microsoft Teams` before joining, `Calendar | <name> | …` in the call;
`MeetingTitle` drops Teams' place names. The tray menu also has "Start a new recording (next meeting)". Notes toasts
during a call are held until it ends.

### Device following
`Detection/CallDevicePicker.cs` records the mic the call app actually captures from and the output with the same
adapter name (`Microphone (Arctis 7+)` ↔ `Headphones (Arctis 7+)`), re-checked every 5 s. Paul's default is a **Jabra
SPEAK 410 speakerphone**; he switches Teams to an **Arctis 7+** for private calls without changing the Windows default.
**Manual recordings** follow whichever app has a mic open (`TrayApp.PickManualDevices`), else Windows defaults; loopback
captures any call app's audio. They re-check every 5 s too, following the apps that had a mic open at the start
(`CallDevicePicker.ManualRule`; the first to open one, if none had).

- Output order: the mic's card's output **where the call app plays**; else the one device the call app plays on; else
  the mic's card's only output; else the default. Pairing by card first went wrong on 2026-10-21: Windows' default mic
  was **Stereo Mix (Realtek)**, so Zoom in Edge used it (nobody heard Paul) while playing on the Jabra. Lemur paired
  Stereo Mix with the Realtek card's unused digital output, and kept both after Paul set Zoom to his Jabra (manual
  recordings didn't follow then): 33 minutes of silence on both tracks, 0 segments, a note made from nothing.

**Device names are read once** (`WasapiSessionSource.Names`, read in the background when Lemur starts) and forgotten
when Windows says an endpoint was renamed, removed or changed state (`NameWatch`, an `IMMNotificationClient`). Reading
a name opens the endpoint's property store: ~50 ms a name on Paul's PC (13 endpoints, 1.3 s for all), while listing
endpoints and sessions takes 3–9 ms. Read at every look, as before 2026-10-19, the names cost 0.2 s of every second of a
call (the capture-session check) and 1.8 s of every 5 (device following), all on the tray's UI thread: every window
was sluggish while recording (Paul: "Trying to edit a previous meeting while on a meeting its sluggish"; the UI thread
was at ~47% of a core). After: 4 ms and 12 ms.

### Alignment
`Audio/AnchoredTrackWriter.cs`: each buffer is placed by wall-clock arrival time — gaps padded, device-clock drift
trimmed (smoothed, 150 ms tolerance). A silent keep-alive stream on the output keeps loopback delivering during silence.
Verified live: beeps across a 3 s gap land within 3–31 ms.

### Ducking
Windows turns other apps down 80% when the default communications mic opens. The recorder opts out
(`Audio/AudioDucking.cs`, via a private NAudio 2.2.1 field — re-check on NAudio upgrades).

### Importing recordings made elsewhere
`Pipeline/RecordingImport.cs`, `Pipeline/ImportFolder.cs` (2026-10-15, Paul's ask: "capturing a meeting using an
iphone, exporting that file, and drop it somewhere for lemur to pick up"). Three ways in: the import folder
(`settings.import.folder`, Settings → Recording → "Recordings from elsewhere"), Recordings → Import… / dropping files
on that window, and the tray's "Import a recording…"; `llmmr import <file>` too.

- The sound is decoded by Media Foundation (`NAudio.Wave.MediaFoundationReader`; WAV directly), downmixed and resampled
  to 16 kHz mono as `mic.wav`; `others.wav` is a header-only WAV. With nothing on the call track the pipeline treats
  it as in person (`SpeakerLabeler` finds no comparable tracks; `ClipPlanner` uses the mic; "me" only by voiceprint;
  `LearnMyVoice` learns nothing from it). `meta.json`: `source: "Imported"`, `manual: true`, and `imported` (file name,
  bytes, SHA-256, where the start time came from).
- Decoded first to `<recordings>\.import-<guid>.part`, so a folder appears only with its audio whole (the folder's name
  needs the start time, which may need the exact length); leftovers older than 6 h are deleted on the next import.
- Start time (`StartSource`): typed in the Import window (User), the MP4/MOV/3GP movie header's creation time
  (Recording — phones write the recording's start, in UTC; 0 or implausible is ignored), a date and time in the name
  (FileName: "Voice 20261014_093000", "2026-10-14 0930 …"), else last write − length (Modified). The Recordings
  window's details say when it is FileName or Modified. Media Foundation's own AAC encoder writes a creation time
  too (the tests rely on it).
- Title: the file's name without its date and with `_` as spaces, "" when it says nothing ("New Recording 12",
  "Voice 003", "ZOOM0001", "IMG_1234"). From the folder or a multi-file import it is a stand-in, `TitleFromFile`
  (`MeetingAbout.NeedsTitle` is true for it), because iPhones name memos after the street; the summary's title
  replaces it, and any rename clears the flag (`MeetingRename`, `MeetingProcessor.DescribeAsync`). From the Import
  window, what's in the box is the user's (`TitleByUser`).
- The folder (`ImportFolder.Scan`, every 15 s from the tray's tick, on a worker, one pass at a time): only audio
  extensions, not hidden/system/temporary files or names starting `.`/`~`; a file is taken when a second pass sees
  the same size and write time, at least 10 s old, and it opens refusing writers (sync clients write in pieces).
  Then it moves to `Imported\` (" (2)" if taken). Same bytes as an earlier import (SHA-256 in meta.json) → not
  imported again, moved to `Imported\`. A permanent `ImportException` (Media Foundation can't read it, silent, under
  a second) → `Couldn't import\` and a toast (held during a call); a Win32 error from Media Foundation (0x8007xxxx:
  in use, a sync client not running) or an IO error → tried again in 5 min. Lemur never deletes the user's file.
- The import folder can't be the notes folder (its MP3s would be imported back) or the recordings folder
  (`RecordingImport.FolderProblem`, checked on Save and by the tray).
- Log lines never name the file (titles live in names): an extension, a size, the recording's folder name, an
  exception's type and HRESULT rather than its message.

## Processing

### Pipeline
`Pipeline/MeetingProcessor.cs`: Recorded → Mixed → Transcribed → Identified → Summarized → Done, state in `state.json`
per recording folder, so failures retry (1/5/15/60 min, then hourly forever) without redoing finished stages. The
summary prompt is client-owned (`SummaryPrompt.cs`) and fed a speaker-labelled transcript and the meeting's day
(`{date}`, "Wednesday 2026-10-14", so the model can write "Fri 2026-10-16" for "Friday"); it asks for action items as
`- [ ] <owner> - <task> - <due> [<hh:mm:ss>]`, the time being the transcript line where it was agreed (since
2026-10-15; see Notes → The meeting file). Scriberr transcription
(`IResumableTranscription`) saves the job id **as soon as the upload is done**; a retry picks that job up (still
running / finished meanwhile / failed → restarted) instead of uploading again. `llmmr process --from mixed|recorded`
clears the job id to transcribe afresh.

### Naming "me"
`Transcripts/SpeakerLabeler.cs`: **word by word from the two tracks** (since 2026-10-14; Paul: "shouldn't my audio be
self identified because its coming from a dedicated track?").

- A 50 ms moment counts when a track is talking (within 30 dB of its p95 and 10 dB above its p10) and is the mic's if
  mic − call > the meeting's cut (Otsu over those moments, floored at +3 dB).
- A word goes to the track that led in most of its moments; a line is split where ≥ 2 *decided* words in a row change
  track (`Runs`; undecided words go with a neighbour but don't count), and a split part takes its words' own engine
  speaker. Without word times (other engines, transcripts from before) a whole line goes to the track that led in most
  of its moments.
- Word times are Scriberr's `word_segments` (`TranscriptWords.Attach`: kept only when they spell the line; split only
  when they say it exactly, punctuation included — on six real calls every word did; Parakeet's times are good to
  ~0.1 s).
- `MeEngineSpeaker` = the mostly-me label with the most me-time (a 0.4 s "?" scrap won on share).
- Skipped for in-person recordings (no call audio). The Jabra's echo cancellation gates its mic to digital silence
  while others talk.
- Why it changed: the old rule, power-mean dB over the whole line, was ruled by its loudest moments. Teams' track is
  ~10 dB hotter than Paul's Jabra, so one murmur from the call took a line (his "Speaker B" at 5:10 of
  `20261014-140644`). On that day's calls it gave 6–9 min of his speech per long meeting to the letter of his own voice
  group, and he then named those letters as colleagues (their clips, cut from the call track, held the colleague's
  interjections) — so four notes credited his lines to colleagues; their saved voices are fine (0.89–0.90 to their own
  groups). Dry run of the new rule on the six calls: +1 to +9 min of him, ≤ 1 s the other way.
- Left under his group's letter: 7–49 short lines a meeting, over half with his mic silent (really someone else) — the
  rest double-talk, which goes to the louder track. A tie-break by the engine's voice group was simulated (+7–16 words a
  meeting) and not built.
- All six of 2026-10-14's calls were remade with it at Paul's request ("a clean baseline"; see Remaking).

### Speaker names
`Speakers/`, design doc §3: the **Identified** stage between Transcribed and Summarized.

- `ClipPlanner` picks clean clips per speaker (others.wav for remote voices, mic.wav for you; no overlap/crosstalk).
  `VoiceprintClient` posts them as `data:audio/wav;base64` strings to an OpenAI-style `/embeddings` (LiteLLM model
  `speaker-embed-local` → the `Voiceprint` container). `Matcher` scores against the **people folder** (`PeopleStore`:
  one JSON per person, sync-safe, OneDrive conflict copies merged, deletion stubs).
- Models are identified by their answer to a synthetic **calibration signal** (`Calibration`), never by name.
  Thresholds (`ModelThresholds`, five levels Careful→Eager) come from `llmmr voices benchmark` on AMI and are built in
  per model fingerprint — for NeMo TitaNet-L (`nemo_en_titanet_large`); the voiceprint compose file's default WeSpeaker
  ResNet34 has no built-in numbers.
- **Introductions** ("hi, it's Dana") come from the summary model and are only believed after `Introductions.Verify`.
- Nothing here may throw into the pipeline: each half records "done" or "skipped: why". `voiceprints.json` (biometric)
  is deleted with the audio.
- **A call you sat through muted** (mic digital silence): the labeler can't compare the tracks (`UsedTrackComparison`
  false, nobody is "me"), but remote voices are still on others.wav — `callAudio` is "others has sound", not "the tracks
  were compared". Before 2026-10-21 it was taken for in-person: Paul's 5-minute Teams call (`20261021-103350`) cut every
  clip from the silent mic, so ▶ in the naming window played nothing, all five voiceprints were one (cosine 0.98–1.00, a
  voiceprint of silence, ~0.17 to everyone), and naming saved two of them to people. `ClipPlanner` also never keeps a
  piece whose track is at or below `Mixer.SilentTrackDb` (−60 dB) over it.

### Naming afterwards
`SpeakerNamer` (naming window / `llmmr people name`) rewrites an untouched note, or swaps labels in place if the user
edited it (`NoteHash` in state.json).

- **One person, several voices** (Paul, 2026-10-15: "the same person may show up as 3 different speakers"): voices given
  one name share a label, so the naming window sends each change with the speaker's `Key`; by label alone a shared one
  is refused with the letters to use (`llmmr people name` takes "Speaker E").
- Consent is per person (design D3): a "They agreed" tick counts for every voice given that name in the same save, and
  someone with consent on record isn't asked again.
- In an edited note a label other lines keep (a shared name, or the user's own name for a voice marked as them) is
  swapped only in the lines that voice starts, found by their time (`SwapTurns`; a line it continued after another
  voice of the same name keeps the old name — the page is made again from the meeting), and never in the summary.
  `SwapLabels` swaps in one pass (Dana ↔ Sam stay two).
- What was written from the summary is swapped like it (`SwapInAbout`): meta's description and suggested title, and
  the title when Lemur chose it (`MeetingRename.Apply(byUser: false)`, so the note's files move; a failed rename keeps
  the old title and is only logged). Lemur 1.1 and earlier swapped only summary.md, so the Recordings window and the
  note's front matter kept "Paul and Speaker A" (Paul, 2026-10-15); `SpeakerNamer.CatchUp`, once per start from the
  queue's first pass, swaps a "Speaker X" still in a finished meeting's meta whose voice is now shown by a name.

### Titles and descriptions
`Pipeline/MeetingAbout.cs`, `MeetingDescriber.cs`, `MeetingRename.cs`.

- The summary prompt asks for `Title:` and `About:` lines first; `MeetingAbout.Split` takes off only the lines the reply
  *starts* with (bold, bullets, quotes tolerated; the first other line ends the search) before summary.md is written. A
  reply with nothing after them (cut off) fails the stage, so it's retried.
- meta.json gets `Description` (null = not asked; "" = asked, none), `SuggestedTitle` and `TitleByUser`. A suggested
  title becomes the meeting's only when `MeetingAbout.NeedsTitle` (no title or a generic one — "Meeting", "Zoom
  Meeting", Meet's `abc-defg-hij` — and the user didn't choose it). `ModelTitle` refuses non-answers ("None", "N/A",
  "[Meeting title]", no letters).
- Without the lines (a custom prompt), the processor asks `MeetingDescriber` (JSON, from the summary only, 3-min
  patience) via `ChatModel`; it never fails the stage.
- The queue's `DescribeOneAsync` backfills finished meetings without a description, newest first, one per pass,
  skipping meetings finished in the last 30 min (their toast points at the note) and summaries with nothing in them; it
  renames an untitled one only if its note is untouched (`NoteHash`) and the user didn't title it. A failure pauses the
  whole job 30 min (3 tries per meeting per run).
- Verified with QFN on AMI ES2004a: both lines came with the summary (52 s, no extra call); the describer answers in 3 s
  through LiteLLM.
- Renaming (`MeetingRename.Apply`) is all or nothing: it moves the note's .md/.html/.mp3 to the new name (" (2)" if
  taken), rewrites the title lines (front matter `title:`/`audio:`, the `# heading` only if it still shows the title
  Lemur gave it, the `[recording](<…>)` link, the page's `<title>`/`<h1>` and escaped MP3 references) by writing beside
  and swapping in, then saves meta and state; any failure undoes every step (a read-only or locked note is tested). An
  untouched note's `NoteHash` is recomputed; an edited one keeps its edits and stays "edited". A rename during note
  writing is caught up right after (`MeetingProcessor`). The meeting file (.json) follows only after that, outside the
  all or nothing: a sync tool or indexer holding it mustn't undo a rename. Moved, else deleted, else left under the old
  name (logged); the queue's pass writes it under the new name either way.
- Titles are YAML-escaped in front matter; `FrontMatter.Set` replaces block lists (Obsidian's) whole.
  `NoteWriter.FileName` turns ": " into " - ".
- Code review (2026-10-14) found the rollback, user-title and empty-summary holes; all fixed with tests.

### Locking
`RecordingFolder.Lock()` is a named mutex (`Local\Lemur.Notes`+suffix) held by rename, describe, `UpdateMeta`/
`UpdateState`, `SpeakerNamer.UpdateNote` and remake, so the tray's threads, a note page's naming window and `llmmr`
never change one meeting at once. JSON reads share for delete and saves retry briefly, so a reader can't fail a save.

### Vocabulary
`Transcripts/Vocabulary.cs`, `Pipeline/MeetingFixes.cs`, Settings → Vocabulary, Recordings → Fix a word…, `llmmr vocab`
(Paul's ask 2026-10-14: "Santavo" is really "Zentavo"). `settings.vocabulary` = `[{term, heardAs[]}]`.

- Every new transcript is fixed right after labelling, before identification, the summary and the note (whole words,
  any case, multi-word phrases, longest first, no chaining; an already-right word isn't counted). The summary prompt
  gets a glossary of the terms in the transcript it's given (`Vocabulary.Glossary(transcript)`,
  `SummaryPrompt.For(…, glossary)`), and the reply is fixed too. **Only those:** with every term, worded "these come up
  in these meetings … also where the transcript has them misheard", QFN put all three of Paul's terms under Concepts
  and terms in a meeting that had none of them, nor anything within two letters of them, each with a made-up meaning
  (2026-10-19; 7 of his 16 summaries had a term their transcript didn't). So the model no longer guesses at mishearings
  nobody listed: Fix a word does that, in the transcript too. The processor re-applies
  the vocabulary (labelled.json, summary.md, title and description) at the summary and note stages, for words added
  mid-way, and splits the reply before fixing it.
- Transcription hints: an OpenAI-compatible server gets the terms as `prompt` (unless `extraFields` has one; an empty
  `extraFields.prompt` sends no prompt); a Scriberr **Whisper** profile gets them as `initial_prompt`
  (`ScriberrBackend.WithHint`). **Scriberr v1.2.0's Parakeet/Canary adapters take no word list** (checked in its source;
  NeMo's GPU-PB phrase boosting exists but Scriberr doesn't use it), so for Paul the after-the-fact fixing is what counts.
- `MeetingFixes.Apply` fixes a finished meeting's labelled.json, summary.md, introduced names, description/suggested
  title and (unless `TitleByUser`) its title via `MeetingRename`, then rewrites an untouched note or swaps the words in
  place in an edited one. In an edited note only what was said and the description change: never the title line, the
  date line, link targets, web addresses or speaker labels, so a user's own title and the MP3 link stay intact, and
  `Needs` ignores a `TitleByUser` title. `NoteWriter.Rewrite` swaps the .md and .html in together or not at all.
- Settings → Save offers it for newly added words (`Vocabulary.Added`); Fix a word… (beside the search box, pre-filled
  from the search) fixes one word in the meetings that have it and adds it to the vocabulary (`TrayApp.RememberWord`,
  read from the file then saved). UI text uses "Cooper Netties" → Kubernetes as the example, not a real company.
- Every term is also matched and kept, so a misheard word inside a term is never touched ("Alice Cooper" survives
  Kubernetes ← "Cooper"); a lookup miss (Unicode case folding) keeps the text, never throws. A misheard spelling needs a
  letter and no comma or semicolon (`Vocabulary.Problems`, checked everywhere words are added). (From a 2026-10-14 code
  review.)
- Correcting a correction (what it wrote is itself a term: "Moravia" → another spelling) replaces that term, its
  misheard spellings and the old spelling moving to the new one (`Vocabulary.Add`). Fix a word asks `Vocabulary.WhyNot`
  before changing any meeting (2026-10-14 14:54: Paul's correction fixed 2 meetings, then the vocabulary refused it with
  an ArgumentException and the dialog still said "New meetings will say …").
- `RememberWord` changes only the vocabulary in memory, and `TrayApp.Apply` builds a new `CallDetector` only when
  detection settings changed: a new one forgets "you stopped recording this call". Settings are read again before the
  Settings window opens if settings.json changed since (`llmmr vocab add`).

### Remaking a finished meeting
`Pipeline/MeetingRemake.cs`, `llmmr remake <recording>...` (Paul, 2026-10-14: "a clean baseline" after word-by-word
"me").

- Fetches the finished Scriberr job's transcript again (now with word times; a lost job is transcribed again), labels
  with the old letters kept per engine speaker (`SpeakerLabeler.Label(…, letters)`), identifies with
  `SpeakerIdentifier.Learn = false` (nothing learned into the people folder twice), summarizes again, and rewrites the
  note in place (`NoteWriter.Rewrite`).
- Carries the user's decisions (Confirmed / NotAPerson / Me, by engine speaker) — except a name on the user's own voice
  group's letter (named under the old labelling from a clip of whoever murmured over them): left to the voices, and a
  voiceprint that naming saved from it is taken out of that person *before* the voices are matched (matched by
  `From.Recording` and cosine ≥ 0.999 to the meeting's old print for that letter; put back if the remake fails). The
  first run took it out afterwards, and that letter recognised itself at 0.93 against a runner-up of 0.29–0.42
  (cross-meeting matches run ~0.6–0.8).
- Title kept (an untitled one takes the suggestion); description from the new summary. Steps, `completedAt` and
  attempts stay as they were (estimates and the audio's age don't move).
- Untouched notes only; all or nothing (voiceprints.json put back on failure); backup of every replaced file in
  `<recording>\before-remake-<time>\`. The meeting stays Done throughout, so it's safe while the tray runs (writes under
  `RecordingFolder.Lock`). Trial on a copy of `20261014-143453`: 169 s (summary 63 s).

### Clean-up and Keep recording
`Pipeline/AudioCleanUp.cs`, run after each queue pass; Recordings → Keep recording, Settings → Notes → "Delete each
note's MP3 too", `llmmr keep` (Paul, 2026-10-15: "Many meetings I dont need to keep the file for, so the 14 day
retention is perfect … bypass that 14 day retention value for important meeting recordings"; he chose "MP3s expire too").

- `keepAudioDays` (14; -1 forever) after `state.completedAt`, a finished meeting's working audio goes
  (`RecordingFolder.DeleteAudio`: the two tracks, the mix, voiceprints.json) → `state.audioDeleted`. With
  `settings.noteMp3.deleteWithAudio` (off by default; a null section reads as off) the MP3 beside its note goes too →
  `state.mp3Deleted`, also set when there was none (no encoder, the user deleted it), so it's never looked for again.
  The audio's deletion is saved before the MP3 is tried, so an MP3 that can't go only is tried again.
- **Keep recording** is `meta.keepRecording`: the user's choice, kept with the title. A kept meeting is skipped whole.
  Each meeting is cleaned under `RecordingFolder.Lock` with its meta read again there, so a tick a moment before wins;
  meta is read before the lock too, so kept meetings past the days don't take it every minute. Naming, Fix a word, rename
  and remake all reload meta under the lock, so the flag survives them (tests). Ticked while recording, it waits in
  `RecordingSession.KeepRecording` (there's no meta.json yet) and goes into meta.json at Stop.
- The MP3 goes to the Recycle Bin (`AudioCleanUp.Recycle`: `SHFileOperation` with `FOF_ALLOWUNDO` and no UI, from the
  queue's thread-pool worker: verified 2026-10-16, no window, MTA is fine). Only a path of 260 characters or more, which
  the shell can't take, is deleted with `File.Delete`; any other failure (open in a player) throws and is tried at the
  next pass, so the meeting's last audio is never lost for good by a fallback. On a drive without a bin (a network
  folder), or with it turned off, Windows deletes it. Tests pass their own deleter, so `dotnet test` never fills this
  PC's bin (the two that call `Recycle` use a path the shell refuses and a file held open).
- Only the meeting's own MP3: if a note is at `state.notePath`, its front matter must say `recording: <this one>`. A
  note deleted by hand frees its name, and a later meeting's note can take it (same minute and title); without the check
  that meeting's MP3 and note would have gone with this one's clean-up (code review, 2026-10-16). With no note there,
  the MP3 is this meeting's: `NoteWriter.Write` never takes a name an MP3 holds. `mp3Deleted` is saved as soon as the
  MP3 is gone, before the note is touched.
- Then the note stops pointing at it: `NoteWriter.DropAudio` takes out the front matter's `audio:` and
  ` · [recording](<…>)` exactly as Lemur writes them, in an edited note too (what they point at is gone; the rest of the
  user's edits stay). An untouched note's `NoteHash` is taken again: it then equals `Render` without audio. The page is
  made again from the meeting's files (`NotePages.Remake`: no player, no script, no ▶ links). A page that can't be made
  again keeps its player markup, whose script shows "The recording (…) isn't beside this page any more" (headless Edge,
  2026-10-16: only the file-not-found load logged, no CSP violation). The meeting file's stamp has whether the MP3
  exists, so the queue's pass writes it again with `notes.audio: null`.
- Settings asks when saving would delete MP3s that weren't going to go (`AudioCleanUp.MoreMp3sGo`: turned on, "forever"
  turned off while on, fewer days, another recordings folder; `NotesPage.ConfirmMp3s`, from the Settings window and the
  wizard): `AudioCleanUp.Mp3sToDelete` counts the meetings past the days, not kept, with their own MP3; No saves nothing
  and shows the Notes tab. The box is disabled while "forever" is ticked.
- Log lines name the recording, the days, and "to the Recycle Bin" or "for good"; a failure is `Log.TypeAndCode`, since
  the MP3's path is the meeting's title.
- Fixed on the way (2026-10-16): the clean-up and `ProcessingQueue.RunOnceAsync` read each state.json outside any try,
  so one that couldn't be read failed the whole queue pass every minute (no notes, no clean-up, no pages or meeting
  files). Now that meeting is skipped, and the queue says so once in the log (the clean-up and the page refresh skip
  it quietly).

## Notes
`Pipeline/NoteWriter.cs`, `NoteHtml.cs`: `<date> <title>.md` (front matter, for Obsidian/tools), `<date> <title>.html`
(for people; the tray opens it) and `<date> <title>.mp3` (the mix, 16 kHz mono 32 kbps via Windows' Media Foundation
encoder, `Audio/Mp3Export.cs`: ~14 MB/hour, 4 s to encode 40 min).

- The MP3 is part of the note — **kept until the user deletes it** (Paul's choice, 2026-10-13); `keepAudioDays` only
  covers the recording folder, unless `noteMp3.deleteWithAudio` sends the MP3 with it (2026-10-16; see Processing →
  Clean-up and Keep recording). No MP3 (encoder missing, e.g. Windows N) → note without a player, logged. A name
  collision with any of the three files moves all to `… (2)`.
- The page has no external resources, and its only script is the player (`NoteHtml.PlayerScript`, present only with an
  MP3): click a transcript line to play from 0.5 s before it, the line being said is lit. The CSP allows that script by
  SHA-256 hash only (LF line endings — browsers normalise before hashing) and media from `'self' file:`; Markdig runs
  with raw HTML disabled, since the summary and transcript are untrusted text. Verified in headless Edge from disk
  (`file://`): no CSP violations, a changed script is refused, a missing MP3 hides the player.
- **Attendees** (Paul's ask, 2026-10-14): the page's header lists everyone heard (`NoteHtml.Attendees`, from the
  segments): you first, then by talk time, chips in their transcript colours, unnamed voices ("Speaker E", "Dana?")
  dimmed, "Other audio" left out, the Name speakers link under it; the Markdown keeps `people:` (named only).
- Pages carry their layout (`NoteHtml.PageVersion`, now 3; `state.pageVersion`); the queue's pass re-makes older pages
  from the meeting's files (`NotePages.RefreshAll`, 20 a pass, the page only, never the .md).
- **Opening at a moment** (page version 3, 2026-10-15): `#t=<seconds>`, on load and on `hashchange`, lights the last
  transcript line starting at or before it (times rounded down to the second, so `t=154` takes a line starting at
  154.6), jumps to it (an instant jump: a scripted smooth scroll stalled after the first in headless Edge) and shows
  "▶ Play from 12:34", which plays that line from 0.5 s before it (sound waits for a click). Before the first line,
  the first is lit; `#t=12abc` or no hash hides the button. A summary list item's trailing time within the meeting's
  length — read by `ActionItems.TrailingTime`, as the meeting file reads an item's `at`, so the two always agree —
  becomes a link into the MP3 (`file.mp3#t=`), built on Markdig's tree so no raw HTML gets through;
  the script makes it play in the page. Checked in headless Edge with `docs/tools/note-moment.mjs` (a real MP3,
  `file://`, a `#` in the file name): no console messages or CSP violations, a changed script refused.

### The meeting file
`Pipeline/MeetingFile.cs`, `Pipeline/ActionItems.cs` (2026-10-15; design: `docs/2026-10-15-meeting-export-design.md`
§3.1–3.2): `<date> <title>.json` beside the note, format `lemur.meeting/1`, described for programs by
`docs/help/meeting-file.schema.json` (shipped as `help\`).

- Made from Lemur's own files (meta, summary.md, labelled.json with names applied, identified.json, state), never the
  .md, so an edited note still gets the meeting as Lemur knows it. Written with relaxed escaping ("I'll", not
  "I\u0027ll"). `revision` = a hash of the content without revision/revisedAt; `revisedAt` changes only with it.
  A file already exactly right isn't rewritten (whatever watches the folder sees nothing).
- **The transcript can be left out** (`settings.meetingFile.includeTranscript`, Settings → Notes → Meeting file, on by
  default; 2026-10-15, for programs whose model is on the internet). Then `transcript` is missing (not `[]`) and
  `"leftOut": ["transcript"]` says why; the schema requires one or the other. labelled.json is still read: people and
  owners come from it. Every writer gets the choice as `transcript:` from its settings (the parameters default to
  true, so a new caller must pass it). It's in the stamp only when off, so files from before the choice weren't made
  again. While it's off the queue's pass has no 20 limit: a file still holding a transcript shouldn't wait (the pass
  looks only at file times for the rest). `llmmr export` follows it, or `--transcript`/`--no-transcript` with `--out`;
  beside the note it refuses to go against the setting, which the pass would undo. The note's .md and page are
  unchanged.
- Written after the note (`MeetingProcessor`, also after the rename catch-up), by `SpeakerNamer`, `MeetingFixes`,
  `MeetingRemake` (which backs it up with the rest), `llmmr rename`/`describe --apply`/`export`, each passing the
  vocabulary (settings', or the words just fixed) for matching item ids. `MeetingRename` moves it after the note's
  files, outside their all or nothing (above), and has no settings to rewrite it, so a rename from the Recordings
  window is caught by the queue's pass. `NoteWriter.Write` counts a leftover .json as taking a name. `llmmr export`
  without `--out` refuses a meeting whose note is gone, rather than write a .json beside nothing (`Write` too).
- `TryWrite` never throws: a failure is logged (recording name and exception type only: paths hold titles, a parser's
  message the text) and what happens next depends on it. An IO problem (open elsewhere, read-only) is retried after
  15 min. Anything else would fail the same way next time, so the meeting is marked (`none:<stamp>`) until its files
  change: a missing or unreadable summary/transcript (a warning) or a bug (an error with the HResult; a full-width
  digit in a due date crashed `int.Parse` and retried every 15 min forever, 2026-10-15). The queue's pass
  (`MeetingFile.RefreshAll`, 20 a pass, after the pages) makes or remakes files whose stamp is out of date: a hash of
  four files' write times (meta, summary, labelled, identified), the note's path, whether the MP3 exists, the display
  name and culture (they change `recordedBy`, `ownerIsMe` and dates), whether the transcript is left out, and a format
  version. Not the vocabulary: it only matches ids, and an unchanged summary gives the same file.
- Action items (`ActionItems.Read`): the `## Action items` section (any case or level, a trailing colon, a bold line);
  a bullet indented further than the section's first is a note on the item above, not an item (a list that starts
  indented is still the list). The trailing time comes off first
  (`ActionItems.TrailingTime`: `[00:12:34]`, `[12:34]`, `(00:12:34)`, a full stop after, `[75:30]`; dropped past the
  meeting's end), the owner is before the first " - " (en and em dashes count), the due after the last " - " only if
  all of it is a date; with only one " - " and a date after it, the line is task and date, owner null ("Book the venue
  - Fri 2026-10-16"). `- [x]` is done. "None." is found, with no items; a section with no line in that form is
  `actionItemsFound: false` (don't trust owners). Numbers are `[0-9]`, never `\d` (other scripts' digits).
- `ownerIsMe` (`ActionItems.IsMe`): equal to the display name (or a name the user had before); or a shared first word
  where one side is only that word and no other speaker has that first name; "me"/"I"/"myself". Someone else in the
  meeting with exactly the owner's name makes the item nobody's.
- Due dates (`ActionItems.DueDate`), from the meeting's local day: a weekday is the next one after it (a week on if
  it's that day), "next Friday" the one in the following week (by the culture's first day), "end of week" the first
  Friday on or after the day, "end of next week" the one after, end of month, tomorrow; explicit dates, `10/20` in the
  short date pattern's order, a date without a year the first on or after the meeting. A weekday that contradicts
  its date ("Thu 2026-10-16") gives no date; "none"/"n/a" none. Words kept as `dueText` either way.
- Item ids `<recording>/<number>`: `state.actionItemNumbers` keeps each numbered item's text as the summary had it
  (lower case, spaces made one, names and "Speaker X" kept) → number; `state.nextActionItem` is never reused. To match,
  the kept text and the new one are both made comparable with what the meeting has *now*: the vocabulary's fixes
  made, "Speaker X" labels and the current people's names (full and first) taken out, everything but letters and
  digits dropped ("Zen Tavo" fixed into "Zentavo" still matches). Key and occurrence (two "send the deck" are 1 and 2,
  in number order) must be equal; there's no loose match — a "one word different" rule gave a reworded item, or a
  new one much like a dropped one, the old id (2026-10-15). Comparing at match time is what makes naming work: stored
  without names, "call Dana" (Dana only mentioned) and "call" (Dana now a speaker) never met. A word the summary has
  fixed is fixed in the kept text on a match, so a later write without that word (Fix a word, not remembered) still
  matches. Left open: two items differing only by an unnamed label, one dropped by a remake, can swap ids once it's
  named. A table that can't be read is empty, not the whole state (`ActionItemNumbersConverter`); an old one (names
  already taken out) still matches. **state.json holds those texts**: meeting content, so print counts only (the
  `inspect` skill).

### Links to a meeting
`lemur://note/<recording>[?t=<seconds>]` (`Pipeline/NoteLinks.cs`; design §3.6): the meeting file's `link`s, for task
boards and chat. Only that shape parses (`[0-9]`, not `\d`: other scripts' digits crashed `int.Parse`; `t` up to
999999; a trailing slash allowed). `Windows/NoteLink.cs` handles it like the name link (no tray, no mutex, doesn't
create settings.json on a PC never set up): `NoteLinks.FindNote` takes the state's `NotePath` (a `.md` that exists),
else the notes folder's `.md` whose front matter says `recording: <name>` (quoted too) — first those named with the
recording's date and minute, then the rest, skipping OneDrive's online-only files; a name that extends another
match's with "-" is OneDrive's conflict copy and loses, otherwise the newest wins. Opens the `.html` beside it (or
the `.md`) at `#t=` through `Windows/Browser.cs`; nothing found → "That meeting's note isn't on this PC." A failure to
open it is logged as `Log.TypeAndCode` (type and Win32 code): .NET 10 puts the file, i.e. the title, in a failed
`Process.Start`'s message. Not yet tried
end to end in the installed app (unit tests and the page in headless Edge only).

## Windows

### Recordings window
`Windows/RecordingsForm.cs`, rows from `Pipeline/RecordingList.cs`. Opened by double-clicking the tray icon, the menu's
bold "Recordings…", or starting Lemur.exe again (a named event, `TrayApp.ShowRecordingsEvent`). Title bar "Recordings —
Lemur (LLM Meeting Recorder)" (Paul asked for the tag).

- Lists the live recording, the one being processed and its stage (`ProcessingQueue.Current`/`StageStarted`), waiting,
  retrying (error + next try), done and **interrupted** ones (audio but no meta.json — the app died mid-recording;
  `RecordingFolder.Interrupted` skips folders written to in the last 2 min).
- Actions: Retry now (`ProcessingQueue.RetryNow`), Open note, Name speakers, Show files, Make notes anyway
  (`RecordingFolder.Recover`: `WavRepair.FixSizes` — a killed WaveFileWriter leaves RIFF/data sizes at 0 — then
  meta/state), Delete, Rename… (`RenameDialog` → `MeetingRename`), plus Import… (`ImportDialog`, see Importing),
  Open notes folder, Settings… and Help; the same on a right-click menu; keys Ctrl+F (search), F2, Enter, Delete,
  Left (close the selected meeting's heading).
- **Keep recording** (2026-10-16; how it works: Processing → Clean-up and Keep recording): a check box under the
  details, in a second row of the lower table (the steps span both rows), with a grey line under it saying what it does
  for that meeting (`KeepNote`), and on the right-click menu. `AutoCheck` is off: the click decides, so an untick past
  the days can ask first and a refresh setting it isn't taken for the user. Enabled while recording (through the tray's
  `KeepLive`) and while notes are made; disabled for an interrupted recording (no meta.json) and when nothing is left
  (`RecordingRow.CanKeep`: working audio or the note's MP3). The list selects one meeting at a time, so there's no mixed
  state. Status: "recording kept", "audio deleted · MP3 kept", "audio and MP3 deleted".
- **Finding meetings** (2026-10-14, Paul's ask): a search box and a date filter (`DateRanges`: today … this year,
  between dates) over the list. `MeetingSearch` matches the row's title/description/people/source, then the note's `.md`
  as the user sees it (front matter, headings and the date line left out, so "meeting"/"scriberr" don't match every
  note; wrapped paragraphs joined) or, without a note, summary.md + labelled.json; every term must appear, `"quoted"`
  terms stay whole; texts cached by file time. The detail panel (a RichTextBox) shows title, date, the description,
  "With …", the lines the search found (transcript lines keep time and speaker) and the old details.
- The list is owner-drawn (`Windows/MeetingListView.cs`): two lines per row — title (grey for an untitled stand-in like
  "Google Meet (Edge) call") and the description, or "Notes expected around…" while in progress. Row height comes from a
  1-px-wide `SmallImageList` of the right height; icons are drawn from `StepIcons.ForRows`.
- **Headings** (2026-10-15, Paul's ask: "collapse/expand each Day/week/Month… after months of recordings"; he picked
  the mail-program layout and asked for total hours on each): `Pipeline/RowGroups.cs` puts rows under Today, Yesterday,
  Earlier this week, Last week, "Earlier in <month>", then one per month (`RowGroup.Summary`: "6 meetings · 3 h 40 min");
  weeks start on the culture's `FirstDayOfWeek`. They are native `ListViewGroup`s with `CollapsedState` (drawn by
  Windows; the rows stay owner-drawn). Keys carry their days (`today:2026-10-15`), so a window left open overnight
  starts the new day's groups fresh. Only Today and Yesterday start open; the user's opening and closing is kept in a
  static dictionary for the process's life, and in a second one while searching or filtering by date (everything found
  shows open; cleared when the filter changes, so clearing the search restores the user's headings). The `When`
  column drops what the heading says ("09:02" under Today, "Tue 09:02" in a week, "Tue 6 Oct 09:02" in a month).
- Heading gotchas, from the WinForms source (`ListView.UpdateGroupCollapse`): WinForms toggles a group on button-up
  over the chevron, hit-testing at **`Cursor.Position`**, not the message's point, so posted clicks (the demo tools)
  don't toggle it — and Windows itself may toggle the native group anyway, leaving WinForms' cached state wrong (it
  then springs back the next time the group's header is set). A double-click on a header toggles it *and* raises
  `DoubleClick` (which opened the selected meeting's note until it checked for an item under the pointer); keyboard
  toggles are synced by WinForms only for the selected item's group. So `MeetingListView.TakeHeadingsFromWindows`
  reads every group's real state at each refresh and after Left/Right with `LVM_GETGROUPSTATE` by the group's
  **internal `ID`** (reflection; WinForms' own `GetNativeCollapsedState` does the same). Reading it by index with
  `LVM_GETGROUPINFOBYINDEX` instead gave every group the first one's state — every 2 s all headings snapped to
  Today's (Paul: "I can close 1, but only 1"). Left on a selected meeting closes its heading; right-click on a heading
  or empty space offers Expand all / Collapse all.
- The selected recording's **steps** show as a drawn timeline (`Windows/StepsView.cs`, icons in `StepIcons`, also the
  list's row icons): green check / spinning ring / orange "!" / grey circle, how long each step took, and rough times for
  the rest with "Notes expected around HH:mm" (waiting ones add the meetings queued ahead). Step times are saved in
  `state.json` (`steps`: start, end, tries per `Step`, written by `MeetingProcessor.Begin`/`Advance`).
- Estimates (`StepEstimates`): built-in fixed + per-minute times measured 2026-10-13/14 (48-min meeting: transcribe
  208 s, names 63 s, summary 114 s, note 5 s; 6½ min from call end to note), scaled per step by the median
  actual÷measured of this PC's own meetings (first-try steps only).
- Toasts ("Recording", "Making meeting notes — should take about N min", "Couldn't make notes yet") open this window on
  their recording.

### Naming window
`Windows/NamingForm.cs` ("Who's speaking?").

- Every control gets an explicit grid cell. "They agreed" is hidden for a voice that can't be remembered (too little
  audio), and a hidden control takes no cell in a TableLayoutPanel's flow, so every later row slid one column (Paul's
  2026-10-14 GoTo meeting, three too-little voices: play button in the left column, the name box squeezed to nothing;
  reproduced on the demo, fixed).
- It's modeless (beside Recordings, or alone from a note page's `lemur://` link), so Save/Cancel call `Close()` —
  setting `DialogResult` doesn't close a modeless form. The link's own process closes on the quit event too (now
  manual-reset; the tray resets it at start).
- Each name box lists the people folder **and every name typed in the window**, updated at each key in the other rows
  (the box being typed in catches up when another row changes), item by item: `Items.Clear()` on a box with
  `AutoCompleteSource.ListItems` makes WinForms create its window again, 0.2–0.4 s a key with four other rows (~6 ms
  now, measured on the demo by posting keys).
- After changing a list, `SetItems` selects the item by the box's text again. Before a box has its window, WinForms'
  `Items.Insert` doesn't move the selected index along, and the window, once made, shows whichever item then sits at that
  index: on opening, a name from a later row sorting before a row's own showed that row as someone else ("Sam" for "Sam
  Okafor" on the demo), and Save stored it, the text having "changed" (Paul, 2026-10-21: "not saving the speakers
  correctly, showing the wrong name in the app, but the HTML is correct").

### Help
`Windows/HelpLink.cs` opens `help\<page>.html#<section>` beside the exe through the user's default browser
(`Windows/Browser.cs`, shared with note links: its `https` UserChoice command with a `file://` address — Windows drops
a `#fragment` when it opens a file, and `.html` may belong to an editor). Reached from the tray menu, every window's
Help link/button, About, and the Start menu's "Lemur Help" shortcut (`Lemur.exe --help` — which 1.0 never handled: the
shortcut started Lemur or showed Recordings; fixed 2026-10-15). The help pages load nothing from the network (checked
in headless Edge).

## Install and process

### Installer hooks in `Program.cs`
`--quit` sets the `Local\Lemur.Quit` event (the tray finishes any recording and exits; the MSI runs it before replacing
or removing files), `--unregister` removes start-at-sign-in (on uninstall), `--help` opens the manual, and a
`lemur://name/` or `lemur://note/` argument is a link's own short-lived process (see Links to a meeting). The MSI owns the
`lemur://`/`teamsrecorder://` keys and the PATH entry; uninstalling keeps settings, recordings, notes and keys (verified
2026-10-14: clean uninstall, then reinstalled). The MSI is ~39 MB (self-contained .NET runtime, one folder for both
exes, no PDBs).

### Start at sign-in
`Windows/Autostart.cs`, HKCU Run value `Lemur`: asked on the wizard's last step, and Settings → Recording → "Start Lemur
when I sign in to Windows" (applied on Save).

### `LEMUR_HOME`
`AppPaths.Home`: a separate copy for demos, screenshots and tests — settings and data in that folder, keys under
`Lemur.<hash>:` (never the real `Lemur:` ones), its own mutex/event names, and no autostart, link registration or
upgrade. Added after a demo wizard would otherwise have saved a test key under Paul's real name.

### Renamed from Teams Recorder
`LegacyMigration` moves an old install's folders, keys and start-at-sign-in entry on first start (done on Paul's PC
2026-10-14). Old notes' `teamsrecorder://` links and `.trecsetup` files still work.

## Measurements

- Speeds (2026-10-13, Scriberr on an RTX 3090): transcription ≈ 13× real time + ~30 s; speaker names ≈ 30 s (the
  introductions LLM call; voiceprints are a few seconds on CPU); summary 25 s (2 min call) to 70–120 s (17 min).
- **Summary model comparison** (2026-10-14, AMI, blind-scored by an agent vs AMI's human summaries): QFN
  (`qwen3.8-flash-next-local`) 15.6/20, 1.2 factual errors/meeting, ~90 s; Qwen3.6-35B-A3B 13.0/20, 3.4 errors/meeting
  (turns suggestions into decisions, wrong owners), ~60 s; Qwen3.8-27B 18.7/20, 0 errors, but 6–8 min per summary —
  dropped for speed by Paul. QFN stays. QFN's introductions check thought for up to 13.5k tokens → `summary.maxTokens`
  default 16384. The harness was a session scratchpad (`bakeoff/`, not kept).
