# Cuepoint roadmap

What's been added since the phase meter, what was set aside, and what's planned next. Sizes are rough: **S** an
afternoon, **M** a day or two, **L** several days. Each phase is worth having on its own; the order within it is the
order to build in.

## Done

| Phase | What | Commits |
|---|---|---|
| — | Phase meter, beat counter | `0d298f1`, `e59b7c1`, `f986ffa` |
| 1 | Keyboard browsing and search, the `?` shortcuts list, a cue sheet and tracklist with each recording | `ecddcb5` |
| 2 | Up next, editing Auto-Mix's list as it plays, checking a list before a set, intro and outro marks | `0d8a620` |
| 3 | Master volume, censor, an eight-pad sampler, a booth output | `1db7678` |
| 4 | Energy ratings, editing songs' details, undo for list changes, finding similar songs, album art | `93485bc`, `4a17dd4` |
| 5 | Backing up settings, changeable keys, DDJ-FLX4 and DDJ-400 mappings, Light and High contrast looks | `c1f936f`, `55f95b0`, `7001291` |
| — | Sync lock, a brighter Dark label colour and fainter list stripes | `3f41228` |
| — | BEAT LOOP and KEY SHIFT pads, the DDJ-400's FX ON following CH SELECT, sync lock kept after a crash | `1eb46f7` |
| 6 | Suggested next, past sets kept with `played:` and `notplayed:` searches, ordering Auto-Mix's list for mixing | `33bb825` |
| — | Auto-Mix's Order list: for mixing, by title, artist, tempo, key or energy, or shuffled | this commit |

Set aside: an elapsed/remaining toggle (each deck shows both already) and choosing the sync leader (with two decks
the other is the only leader; sync lock made the unlocked deck lead).

## Needs the hardware

- **Try the DDJ-FLX4 and DDJ-400 mappings on the controllers.** They're from Pioneer's lists of MIDI messages but
  untried: that Windows names them exactly "DDJ-FLX4" and "DDJ-400", that their lights come on, that the jog wheels
  feel right, and that the BEAT LOOP and KEY SHIFT pads and the 400's FX ON do what's expected.

## Phase 6: Choosing what to play (done)

| # | Item | Size | Notes |
|---|---|---|---|
| 1 | **Next-song suggestions**: a list beside the song list of what mixes well with the song playing | M | Ranked by a key that mixes, a tempo in reach of the tempo fader (half and double time too), an energy a step up or down, and not played this session. Every input is there already (`SongQuery`'s compatible keys, `EnergyRating`, `SessionPlays`). Reuses the side panel's slot, or a tab in it beside Up next; Enter or a drag loads one. |
| 2 | **Play history across sessions**: every set kept in a dated list | M | `SessionPlays` keeps only this session's; each set's saved as it's played (a file per session in the app's folder), listed under Lists > History by date, each exportable as today's is. Adds `played:30d` and `notplayed:30d` searches, which suggestions (1) can use to avoid repeats across gigs. |
| 3 | **Smarter Auto-Mix ordering**: "Order for mixing" on a playlist | M | Orders by key and energy (a walk round the Camelot wheel, energy rising then easing) before Auto-Mix plays it. Shares (1)'s scoring. |

## Phase 7: Cues and decks

| # | Item | Size | Notes |
|---|---|---|---|
| 4 | **Editing hot cues**: a pad's name, colour and loop from its right-click menu | M | Names, colours and hot loops only come from Rekordbox and Serato now. Kept as imported ones are (`HotcueDetail`), shown on the pads and overview, with undo through `EditHistory`. |
| 5 | **Auto cue**: a song loads cued to its first beat, or its first sound | S | Where it waits until a cue point's set by hand; an option, on to begin with. |
| 6 | **Moving a loop**: on by a beat or a bar, or back | S | Keys, buttons and controller controls; `BeatJump` moves a loop already, so this is mostly controls. |
| 7 | **More effects**: a beat-timed gate, a bit-crusher, and effects across the whole mix | M–L | The gate and crusher join echo, reverb and flanger in `DeckEffects`; a master effect needs a slot in the mix after the decks, before the limiter. |
| 8 | **Microphone with talkover** | M | An input mixed into the main mix with its own level, and talkover turning the music down while it's spoken into. Needs an input device chosen in the options, as the outputs are. |

## Phase 8: Library

| # | Item | Size | Notes |
|---|---|---|---|
| 9 | **Editing several songs' details at once**: artist, album or genre for a selection | S–M | Builds on Edit details (`WindowsTags.Write`, `LibraryDatabase.Retag`); a blank field leaves each song's own. |
| 10 | **Tempo range preference**: analysed tempos folded into a range chosen in the options | S–M | 70–140 or 85–170, say, so drum & bass and hip-hop read as DJs expect. Applied when a grid's looked up, so nothing's analysed again; a grid fixed by hand is left as it is. |

## Phase 9: Sets out, and polish

| # | Item | Size | Notes |
|---|---|---|---|
| 11 | **Live streaming** to an Icecast or Shoutcast server | L | The recorder's mix, encoded (MP3 or Ogg) and sent, with the song playing as the stream's title; a status in the title bar beside REC. Needs an encoder library. |
| 12 | **Editing a recording's tracklist** before it's uploaded | S–M | A song's start time and name, written back to its cue sheet and `.txt`. |
| 13 | **Scaling the whole screen**: a size setting in the options | M | Large text only enlarges the lists; this scales every element and font, for 4K screens and small laptops. Partly a cue-framework change, so Cuedeck can have it too. |
| 14 | **A new README screenshot**, made by a capture mode | S–M | The screenshot's from before Up next, the phase meter and the looks. Cuedeck's `--capture` (a scratch app folder with made-up songs) shows how to make it repeatable. |

## Order and dependencies

- **Next:** Phase 7, (4) first: hot cues are what's touched most on the decks.
- **Shared:** (1) and (3) score songs the same way; (2) gives (1) history beyond this session.
- **Framework:** (13) goes into cue-framework, so build and test Cuedeck with it too.
- **Biggest:** live streaming (11), for its encoder and network handling; master effects (7) touch the mix itself.
