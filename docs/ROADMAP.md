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
| — | Auto-Mix's Order list: for mixing, by title, artist, tempo, key or energy, or shuffled | `d540af9` |
| 7 | Editing hot cues, auto cue, moving a loop, gate and bit-crusher, master FX, a microphone with talkover | `fc52ec9` |
| 8 | Editing several songs' artist and album at once, a tempo range in the options | `6b3b0e6` |
| 9 | Live streaming to Icecast in Opus, editing a recording's tracklist, scaling the whole screen (with Cuedeck), a screenshot the app takes itself | `0b4f7c1` |

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

## Phase 7: Cues and decks (done)

| # | Item | Size | Notes |
|---|---|---|---|
| 4 | **Editing hot cues**: a pad's name, colour and loop from its right-click menu | M | Names, colours and hot loops only come from Rekordbox and Serato now. Kept as imported ones are (`HotcueDetail`), shown on the pads and overview, with undo through `EditHistory`. |
| 5 | **Auto cue**: a song loads cued to its first beat, or its first sound | S | Where it waits until a cue point's set by hand; an option, on to begin with. |
| 6 | **Moving a loop**: on by a beat or a bar, or back | S | Keys, buttons and controller controls; `BeatJump` moves a loop already, so this is mostly controls. |
| 7 | **More effects**: a beat-timed gate, a bit-crusher, and effects across the whole mix | M–L | The gate and crusher join echo, reverb and flanger in `DeckEffects`; a master effect needs a slot in the mix after the decks, before the limiter. |
| 8 | **Microphone with talkover** | M | An input mixed into the main mix with its own level, and talkover turning the music down while it's spoken into. Needs an input device chosen in the options, as the outputs are. |

## Phase 8: Library (done)

| # | Item | Size | Notes |
|---|---|---|---|
| 9 | **Editing several songs' details at once**: artist, album or genre for a selection | S–M | Builds on Edit details (`WindowsTags.Write`, `LibraryDatabase.Retag`); a blank field leaves each song's own. |
| 10 | **Tempo range preference**: analysed tempos folded into a range chosen in the options | S–M | 70–140 or 85–170, say, so drum & bass and hip-hop read as DJs expect. Applied when a grid's looked up, so nothing's analysed again; a grid fixed by hand is left as it is. |

## Phase 9: Sets out, and polish (done)

| # | Item | Size | Notes |
|---|---|---|---|
| 11 | **Live streaming** to an Icecast or Shoutcast server | L | The recorder's mix, encoded (MP3 or Ogg) and sent, with the song playing as the stream's title; a status in the title bar beside REC. Needs an encoder library. |
| 12 | **Editing a recording's tracklist** before it's uploaded | S–M | A song's start time and name, written back to its cue sheet and `.txt`. |
| 13 | **Scaling the whole screen**: a size setting in the options | M | Large text only enlarges the lists; this scales every element and font, for 4K screens and small laptops. Partly a cue-framework change, so Cuedeck can have it too. |
| 14 | **A new README screenshot**, made by a capture mode | S–M | The screenshot's from before Up next, the phase meter and the looks. Cuedeck's `--capture` (a scratch app folder with made-up songs) shows how to make it repeatable. |

## Phase 10: Safe sets

| # | Item | Size | Notes |
|---|---|---|---|
| 15 | **Recursive kept between runs** for the Music library | S | It's forgotten each run, so the library lists nothing until it's turned on again. Kept in the settings, as the folder chosen is; the scan starts recursive. |
| 16 | **A panic button**: everything back to neutral at once | S | Both decks' EQs, filters, trims and effects, the master effect, loops and rolls, key shift and the crossfader to the middle; playback left playing. A key and a learnable controller control, with Ctrl+Z putting it back. |
| 17 | **A self-test before a gig**: `--self-test` | M | Runs the Release build on the capture's scratch folder and made-up songs: loads, plays, syncs, records, edits the tracklist, streams to a pretend server, and quits saying what passed. Catches what the unit tests can't (startup, the audio device, the GPU). |
| 18 | **The main output coming back**: falling back when its device goes | M | If the main output's device is unplugged mid-set, carry on through the system default rather than going quiet, and go back to it when it's plugged in, as the booth output does already. |
| 19 | **A recording saved from a crash**: its WAV made whole next start | S | A crash leaves the header's lengths unwritten; on the next start, any recording found that way is mended from its file's size, with its cue sheet written from the history kept. |

## Phase 11: Rough edges

| # | Item | Size | Notes |
|---|---|---|---|
| 20 | **Genre**: read from tags, a column, a `genre:` search, and edited | M | Not kept now, so (9) left it out. Added to `Song` and the library database, read with the other tags (`WindowsTags`), shown and sorted as a column, searched and kept in smart playlists, and edited for one song or several. |
| 21 | **Status lines that stay true**: a pass over what's said | S | The library's "0 songs" stayed up after a rescan until (Phase 9) fixed it; anything else that changes what was said (a deck unloaded, a list changed) says so again, or the line clears. |
| 22 | **Streaming extras**: listeners, and MP3 | M | The listener count from Icecast's status page (`/status-json.xsl`), shown with LIVE; MP3 as a choice beside Opus, for Shoutcast and older players, which needs an encoder (LAME as a native library, LGPL). |

## Phase 12: During a set

| # | Item | Size | Notes |
|---|---|---|---|
| 23 | **Phrase-timed effects at a press**: an echo-out or filter sweep to the next phrase | M | One press starts an effect that builds to the next phrase boundary and stops there, as a transition would; Auto-Mix's phrase finding (`TransitionPlanner`) already knows where they fall. |
| 24 | **A vocal cut**: a mid-band preset on a deck | S | Not stem separation, which is too heavy to build in, but an EQ preset that takes the vocals' band down, on a button, for a quick acapella-ish blend. |
| 25 | **Sampling from a deck**, and a second bank of pads | M | The loop playing (or the last beats) captured onto a sampler pad, kept as a file in the app's folder; a second bank of eight, flipped between. |
| 26 | **A set timer**: an end time, with Auto-Mix pacing to it | M | How long's left of the set in the title bar; Auto-Mix shortens or lengthens its transitions, or leaves out songs, so the list ends on time. |

## Phase 13: Library

| # | Item | Size | Notes |
|---|---|---|---|
| 27 | **Suggested hot cues** at the drop, the breakdown and the outro | M | From the analysis's energy shape and intro and outro marks (found by `TransitionPlanner`), offered on a song's pads to keep or clear. |
| 28 | **Key-shift suggestions** in Suggested next | S | Songs that mix a semitone up or down, marked with the shift they need, which loading them applies (key shift's there already). |
| 29 | **Moving songs between drives**, their cues and notes with them | M | As Export to drive, but either way and without a playlist: songs copied or moved, their analysis, cues, notes and places in virtual folders following. |
| 30 | **Cleaning up duplicates**: keep one, recycle the rest | S–M | After Find duplicates, choose the copy to keep; the others go to the Recycle Bin, their cues, notes and places in virtual folders moved onto the one kept. |

## Phase 14: Hardware

| # | Item | Size | Notes |
|---|---|---|---|
| 31 | **Trying the DDJ-FLX4 and DDJ-400 mappings** on the controllers | S | See *Needs the hardware*; the self-test (17) first, so anything wrong is the mapping. |
| 32 | **MIDI clock out**: the tempo sent to drum machines and lighting desks | M | 24 pulses a beat from the deck leading the mix, with start and stop, on a MIDI output chosen in the options; Cuedeck could follow it as well as OS2L. |

## Order and dependencies

- **Next:** Phase 10, each small and each one less way for a set to go wrong: (15), (16), (17) then (18). Live streaming to a real Icecast server's still to be tried (it's tested against a pretend one).
- **Builds on:** (17) uses the capture mode's scratch folder and made-up songs; (20) lets (9) edit genre too; (22)'s MP3 needs an encoder library; (23) and (26) use Auto-Mix's phrase finding; (27) uses the intro and outro marks.
- **Shared:** (1) and (3) score songs the same way; (2) gives (1) history beyond this session.
- **Framework:** (13) goes into cue-framework, so build and test Cuedeck with it too.
- **Biggest:** live streaming (11), for its encoder and network handling; master effects (7) touch the mix itself.
