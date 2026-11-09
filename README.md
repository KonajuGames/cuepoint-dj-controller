# Cuepoint

Cuepoint is a two-deck DJ app for Windows, built for mixing from a library of music files with a keyboard, a mouse or a DJ controller. It analyses every song's tempo, beat grid, key and loudness, keeps the decks in sync, and works with Pioneer's DDJ-SB3 out of the box, including its FX FADE and PAD SCRATCH effects.

![Cuepoint with a song on each deck: the scrolling waveforms across the top, the decks either side of the mixer, and the folder list and song list below](docs/screenshot.png)

It's written in C# on .NET 10. It draws its own interface with Vulkan through [BRUTAL](https://www.nuget.org/packages?q=Ahwoo.BRUTAL) (GLFW, Vulkan, FreeType) and mixes audio with SDL3_mixer.

## Features

### Decks

- **Two decks:** play, cue (with a preview while held), sync and key lock on each.
- **Tempo fader:** ±8%, ±16% or ±50%.
- **Key shift:** a deck's **-** and **+** (beside its key) move the key down or up a semitone, as far as an octave, without changing the tempo, to bring a song into a key that mixes with the other deck. The key shown moves with it. Right-click either to put the key back. A new song starts in its own key.
- **Key match:** a deck's **=** (between **-** and **+**) shifts its key as little as it takes to mix with the other deck's: to the same key, or its relative major or minor, the same number on the Camelot wheel. Key lock goes on with it, so the tempo fader doesn't pull the key away again. **=** is lit while the two decks' keys match.
- **Effects:** each deck has an echo, a reverb and a flanger, one at a time, chosen from the list beside its times. **FX** switches it on and off, the slider sets how much, and the arrows set its length in beats (1/8 to 16): how far apart the echoes are, or how long the flanger takes to sweep, in time with the deck's tempo. Switched off, the echo and reverb ring on and fade, as a DJ mixer's do. They can be learned to a controller's buttons and knobs too.
- **Slip:** with a deck's **SLIP** lit (Shift + VINYL on the DDJ-SB3), a scratch, a backspin, a hotcue held down or a loop lasts only while it's held or looping. The song carries on underneath, shown as a dashed line on the overview, and when you let go the deck drops back in where it would have got to, so the mix stays in time.
- **Hotcues:** eight per deck, kept with each song.
- **Loops:** from 1/16 to 32 beats, fitted to the beat grid, with halve and double.
- **Loops set by hand:** **IN** marks where a loop starts and **OUT** loops back to it from there, on the beat with quantise on. Pressed while it loops, OUT moves its end. Halve and double work on it too, and reloop goes back to the loop's start, or plays the last loop again. They work without a beat grid as well.
- **Pad modes:** the list beside SYNC sets what the pads do: HOT CUE, ROLL, JUMP or GRID.
- **Loop rolls:** on the pads in ROLL mode. A roll loops for as long as its pad is held, then drops back in where the song would have got to.
- **Beat jumps:** on the pads in JUMP mode (Shift + HOT CUE on the DDJ-SB3), back or on by 1, 4, 16 or 32 beats, staying in time. A loop moves with the jump.
- **Quantise:** with a deck's **Q** lit, hotcues and the cue point go on the nearest beat, and jumping to a hotcue while playing lands in time. It's on to begin with.
- **Scratching and backspins:** from a controller's jog wheel. The record follows the wheel, backwards too.
- **Waveforms:** a scrolling waveform for each deck, coloured by bass, mids and treble. When synced, both decks scroll at the same rate. There's also an overview of the whole song with its hotcues, loop and beat grid, drawn as the top half of its waveform to save room, in three bands too when the scrolling waveforms are.
- **Waveform options:** the mouse wheel over the scrolling waveforms zooms both together, as the options' slider does. In the options, they can be coloured in the deck's colour or in three bands (bass red, mids green, treble blue), shown without the beat grid, or made taller to read from further away. All of it is kept between runs.
- **Narrow windows:** when a deck is too narrow for its buttons, they get shorter ("4" for "LOOP 4") and lose their key letters. On a window narrower still, IN, OUT and Q move to a row of their own above, and the decks get a little taller.
- **End-of-song warning:** in a song's last 30 seconds, the remaining time and the end of its overview flash red.

### Analysis

Each song is analysed in the background as it comes into view in the list. The analysis finds:

- its beat grid, including songs whose tempo drifts
- the downbeats
- its key, shown as a Camelot code
- its loudness, for automatic gain

**Analysing a whole list:** right-click a song and choose **Analyse All** to queue every song in the list that isn't analysed yet. A bar above the list shows how far it's got, and the cross beside it stops it. Songs scrolled into view still go first.

**Analysing again:** right-click songs and choose **Re-analyse** to analyse them again from scratch, after a file's been edited, say. A beat grid fixed by hand stays as it was.

**Fixing a beat grid by hand:** in a deck's **GRID** mode, the pads become tools.

- **Pinning beats:** pin a beat to the music, set the downbeat, nudge, halve or double the tempo, make it steady, or reset it.
- **Dragging:** any beat line on the scrolling waveform can be dragged onto the music.
- **Several pins:** each pin sets the tempo between it and the next, so songs that speed up or slow down can be fixed too.

### Mixer

- **Each channel:** a trim, a three-band EQ with kills, a filter, a level meter, a fader, and headphone cue (PFL).
- **Knobs:** the trim, EQ and filter are knobs, as on a mixer. Drag up to turn one up and down to turn it down; Shift or Ctrl turns it finely, the mouse wheel nudges it, and a double-click puts it back to the top. A band turned all the way down is killed and lights red.
- **Crossfader**, and automatic gain for each song (in the options).
- **Headphones:** the headphones button in the title bar, lit while a deck is cued, drops down what they hear (the cued decks, the main mix, or a blend) and their volume.
- **Master limiter:** keeps the mix under 1 dB below full scale when the decks are loud together, turning it down smoothly just before each peak rather than clipping. LIMIT by the master meter lights while it's working. It can be turned off in the options.
- **Recording the mix:** the record button in the title bar records the main mix, as the room hears it, to a 24-bit WAV file in `Music\Cuepoint Recordings`, showing how long it's been going. A set of over four hours carries on into a second file.
- **Outputs:** the main mix and the headphones can go to separate sound cards, or the headphones can use channels 3 and 4 of a four-channel controller's sound card.

### Library

- **Folder list:** the music library, every drive's folders, virtual folders, History, Recently added, and live filters such as songs compatible in key and tempo with each deck.
- **Recursive folders:** a folder, drive or the music library lists only the songs directly in it. Right-click it and choose **Recursive** to list every song in its subfolders too, until another folder is chosen.
- **Song list:**
  - searchable by title, artist and album, and by tempo and key: `120-126` finds songs from 120 to 126 BPM and `124bpm` those at 124, `8A` finds songs in that key and `~8A` those that mix with it, and they go together, as in `disco 120-126 ~8A`. Songs not analysed yet can't be found by tempo or key; the list says how many there are, and **Analyse All** sees to them
  - sortable by any column
  - resizable columns, which can be shown or hidden
  - marks the songs loaded on each deck and those played this session
- **Export to a drive:** right-click a virtual folder, or a Rekordbox playlist or Serato crate, and choose **Export to drive** to copy it to a removable drive that can be written to, USB hard drives and SSDs included. Its songs go in a folder of its name under `Music` on the drive, its playlist shows as a virtual folder there, and the drive's `Cuepoint\library.json` gets each song's analysis, hot cues, memory cues and fixed grid. It runs in the background, with a progress bar and what it's doing under the song list, and a cross to stop it. Songs already copied aren't copied again, so exporting again carries on where it stopped.
- **Virtual folders:** playlists you put together by dragging songs onto them, saved as `.m3u8` files. They can be stored on any drive. One kept on a removable drive goes with it, and its songs are found again under whatever drive letter it gets.
- **Rekordbox and Serato:** a Rekordbox collection exported as XML (in Documents, Music, Desktop, Downloads or the library's folder, or chosen with **Import > Rekordbox XML** on the Music library's right-click menu) shows under **Rekordbox** in the folder list, with its playlists in their folders, and Serato's crates show under **Serato**. Their hot cues come in too, for songs without any here: Rekordbox's from the XML when it's found, and again whenever it's exported afresh, even while the app's running, and Serato's from the song's own tags (MP3, M4A or FLAC) the first time it's loaded, with Serato's beat grid if it's locked there (BPM lock), which takes the analysed one's place as one fixed here would. Serato's unlocked grids are its own analysis, which is often a little off the beat, so they're left out. Hot cues cleared here don't come back. To bring in Serato's hot cues and grids for many songs at once, right-click a folder or drive (every song in it and its folders), a list or crate in the folder list, or songs in the song list, and choose **Import Serato cues and grids**; it's under **Import** on the Music library's menu too. The files are looked through in the background, and songs keep their own hot cues here, and a grid fixed here.
- **Rekordbox's memory cues and beat grids:** they come in from the XML too, each for songs without their own here. A song's first memory cue is its cue point, where it waits once it's loaded, and all of them show as red marks on the deck's overview; a memory loop comes in as a memory cue at its start. A Rekordbox beat grid, often fixed by hand there, takes the analysed one's place, as one fixed here would, with its tempo changes and where its bars start.
- **Play history export:** **Export > Play history** on the Music library's right-click menu saves the songs played this session (each played out for ten seconds or more, in order, every time) as an M3U playlist or as a plain-text set list with the time each started, whichever type is chosen in the Save dialog.
- **Looked through in the background:** the library's folder is scanned on a thread of its own, at startup, when another folder's made the library and when it changes, so the window and the decks never wait for a big library. The folder list works the same way: a folder's or drive's songs (recursive ones too), the folders under one you open out, the songs in virtual folders and imported playlists, and the local drives are all looked for in the background, the song list saying "Looking through the folder..." until they're in, so a big folder or a slow drive never holds the screen up.
- **Watched for changes:** songs added to the library's folder, changed (their tags edited, say) or taken away show up a second after they stop changing, without starting again. A changed song is read and analysed afresh.
- **Drag and drop:** drag songs onto a deck to load them, from the list or from outside the app.
- **Large text:** turned on in the options, the folder list and song list use bigger text, and the folder list bigger icons, to read in the dark or without glasses.
- **Where analyses, hotcues and fixed grids are kept:** with the songs themselves.
  - **On the system drive:** in the app's own `library.json`.
  - **On any other drive:** in a `Cuepoint\library.json` at that drive's root.

### Controllers and lighting

- **MIDI controllers:** they're found as they're plugged in. Any control can be learned to any function, and lit buttons follow what the decks are doing. A controller mapped out of the box keeps what you've learned or unbound on top of its built-in mapping, so controls added to the built-in mapping in later versions still arrive.
- **Pioneer DDJ-SB3:** mapped out of the box. That includes slip (Shift + VINYL), its loop rolls (Shift + FX FADE), beat jumps (Shift + HOT CUE), loop in and out (Shift + 1/2X and 2X), reloop (Shift + AUTO LOOP), the FX FADE transitions (filter sweeps, loop fade out, loop chop and backspins) and the PAD SCRATCH patterns.
- **Lighting:** what each deck is doing, and every beat, is sent to lighting software over [OS2L](https://os2l.org/), including the SoundSwitch extensions. The software is found on the network by itself. While OS2L is on, the title bar shows **OS2L** in green once it's connected, and pulsing orange while it's still looking.

### While a set is running

- **Keeps going when something goes wrong:** an error in the app's own code is written to `errors.log` and mentioned on the status line, and the app carries on. The decks play on their own regardless, and the keyboard, a controller and the lighting keep working even if the screen can't be drawn.
- **Copes with a sound card coming unplugged:** if the main output's sound card goes, or stops working, the mix moves to the system's default output within a second or two, so the music plays on, and the title bar says what's missing. When it's plugged back in, the mix goes back to it, with the headphones on its channels 3 and 4 if they were there. Headphones on a sound card of their own come back the same way. A card that wasn't plugged in when the app started is picked up as soon as it is.
- **Picks up after a crash:** where each deck is gets written down every couple of seconds. If the app didn't close properly, the next start puts each deck's song back with its tempo, key lock, key shift and slip. A deck that was playing moments before carries on from where it would have got to.
- **Keeps the computer awake:** while the window is visible, the computer won't sleep and the screen won't switch off.
- **Remembered between runs:** the audio devices, the music library's folder, the song list's layout and the last folder or virtual folder chosen.

## Keyboard

Each deck has its own side of the keyboard.

| | Deck A | Deck B |
|---|---|---|
| Hotcues 1–8 | Q W E R A S D F | U I O P J K L ; |
| Cue, play, sync, loop | Z X C V | N M , . |
| Halve / double loop | 3 / 4 | 9 / 0 |
| Loop in / out, reloop | Shift + 3 / 4, Shift + V | Shift + 9 / 0, Shift + . |
| Tempo down / up (Shift: volume) | 5 / 6 | 7 / 8 |
| Headphone cue, key lock | T, G | Y, H |
| Slip | Shift + G | Shift + H |
| Key shift down / up, put back | Alt + 5 / 6, Alt + G | Alt + 7 / 8, Alt + H |
| Key match | Ctrl + G | Ctrl + H |
| Kill low / mid / high, reset EQ | F1 F2 F3, F4 | F9 F10 F11, F12 |
| Low-pass / high-pass filter | F5 / F6 | F7 / F8 |

- **Hotcues:** Shift with a hotcue key clears it. In GRID, ROLL or JUMP mode, the hotcue keys work the pads instead.
- **Faders:** they move while their keys are held, a quarter as fast with Ctrl.
- **Crossfader:** `[` and `]` slide it, and `B` centres it.
- **Loading songs:** `1` loads the selected song onto deck A and `2` onto deck B.

## Building and running

You'll need:

- Windows 10 or later
- the .NET 10 SDK
- a graphics card with Vulkan support

```
dotnet run --project Brutality -c Release
```

The music library starts out as your Windows Music folder. To use another folder, right-click it in the folder list and choose **Use as music library**.

Settings, the library's analyses, and `errors.log` for anything that went wrong are kept in `%LOCALAPPDATA%\Cuepoint`.

The app's images are drawn by [Assets/draw_assets.py](Brutality/Assets/draw_assets.py), which needs only Python 3. Run it again after changing them.

## Tests

The tests in [Brutality.Tests](Brutality.Tests) cover what can go wrong without a sound card or a window:

- **Audio:** the master limiter, mix recording, automatic gain, the headphone mix, and what to do as sound cards come and go
- **Analysis:** beat grids, fixing them by hand, keys, quantise and beat jumps
- **Library:** the library files on each drive, scanning and watching folders, Rekordbox and Serato imports, virtual folders, the folder list, the analysis queue and the settings
- **Crash recovery:** what's written down to pick up after a crash, and the error log

They use temporary folders in place of drives, so they never touch your music or the app's own files.

```
dotnet test
```

## License

[MIT](LICENSE)
