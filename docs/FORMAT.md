# Cuepoint file formats

This document describes the files Cuepoint keeps about a music library: the **library files** (`library.json`), which hold each song's analysis, hotcues and hand-fixed beat grid, and the **virtual folders** (`.m3u8` playlists). It's written for implementing a reader. Every rule here is taken from the code that writes the files: `Brutality/Music/LibraryFile.cs`, `LibraryDatabase.cs` and `VirtualFolders.cs`.

All files are UTF-8 without a byte order mark. Paths use Windows separators (`\`).

---

## 1. Library files (`library.json`)

### 1.1 Where they are

What's known about a song is kept in **one** library file, chosen by the drive the song is on:

| Song on… | File | Song keys in that file |
|---|---|---|
| The system drive (the drive Windows is on, usually `C:\`) | `%LOCALAPPDATA%\Cuepoint\library.json` | Absolute paths, e.g. `C:\Music\Artist\Song.mp3` |
| Any other drive, e.g. `F:\` | `F:\Cuepoint\library.json` (a `Cuepoint` folder at that drive's root) | Paths **relative to the drive's root**, e.g. `Music\Artist\Song.m4a` for `F:\Music\Artist\Song.m4a` |

Notes:

- A drive's file exists only once something has been learnt about a song on it.
- **Songs from other drives can end up in the system file.** These are songs on drives that can't be written to, or that weren't plugged in when the app last ran, and their keys are absolute. So a reader should accept absolute keys in any file.
- **To find a song on another drive:** combine the drive's current root with the relative key. Because the keys don't include the drive letter, a removable drive's songs are found whatever letter it gets.
- **To read everything:** read the system file, then `X:\Cuepoint\library.json` on each drive present. Relative keys resolve against the drive the file is on. If a song somehow appears in both, the drive's file wins.

**Matching keys:** compare paths **case-insensitively** (ordinal, ignoring case), as Windows does.

### 1.2 Top level

```json
{
  "version": 1,
  "tracks":  { "<song path>": <Track>, ... },
  "hotcues": { "<song path>": [ <seconds or null>, ... ], ... },
  "grids":   { "<song path>": <Grid>, ... }
}
```

| Property | Type | Notes |
|---|---|---|
| `version` | integer | The file format's version. **Always `1`.** A file with any other version is ignored by the app. |
| `tracks` | object | The analysis of each song, or why it couldn't be analysed (see 1.3). May be empty `{}`. |
| `hotcues` | object | Each song's hotcues (see 1.5). May be empty `{}`. Songs with no hotcues are left out. |
| `grids` | object | Beat grids fixed by hand (see 1.6). **Left out entirely** when there are none. |

Property names are camelCase. A property whose value would be `null` is left out rather than written as `null`; that applies to every object below. Readers should treat any missing property as its default, and ignore properties they don't know.

The three maps are independent. A song can have hotcues or a fixed grid without an analysis, and the other way round.

### 1.3 `tracks`: a song's analysis

A successful analysis:

```json
"Music\\5 Seconds of Summer\\Teeth - Single\\01 Teeth.m4a": {
  "size": 8723391,
  "modified": "2019-10-02T13:16:10Z",
  "analysisVersion": 4,
  "bpm": 139,
  "duration": 204.939,
  "grid": [ { "time": 0.3414, "bpm": 139 } ],
  "downbeat": 0,
  "loudness": -6.68,
  "peak": 0.953057,
  "key": "Eb minor"
}
```

A song that couldn't be analysed:

```json
"C:\\Music\\broken.mp3": {
  "size": 1234,
  "modified": "2021-03-04T05:06:07.1234567Z",
  "analysisVersion": 4,
  "bpm": 0,
  "duration": 0,
  "downbeat": 0,
  "loudness": 0,
  "peak": 0,
  "error": "Couldn't decode broken.mp3. ..."
}
```

| Property | Type | Meaning |
|---|---|---|
| `size` | integer | The audio file's size in bytes when it was analysed. |
| `modified` | string | The file's last-write time **in UTC** when it was analysed. ISO 8601 with a `Z` suffix and up to 7 fractional-second digits (`2019-10-19T13:46:00Z`, `2021-03-04T05:06:07.1234567Z`). |
| `analysisVersion` | integer | The version of the analysis that produced it. Currently **`4`**. |
| `bpm` | number | The song's main tempo, for display only: the grid is what counts (see 1.4). `0` for an error. |
| `duration` | number | The song's length in seconds, to 3 decimal places. `0` for an error. |
| `grid` | array | The analysed beat grid's markers (see 1.4). **Missing** for an error. |
| `downbeat` | integer, 0–3 | Which beat starts a bar (see 1.4). |
| `loudness` | number | Integrated loudness in LUFS (EBU R128), to 2 decimal places. |
| `peak` | number | The largest sample's magnitude, where `1.0` is full scale. It can be over 1 for files that clip. |
| `key` | string | The musical key (see 1.7). **Missing** for an error. |
| `error` | string | Present **only** when analysis failed, saying why; the other analysis fields are then meaningless. |

**Is the analysis still valid?** The app only uses a record if all of these hold:

- `size` equals the file's current size in bytes
- `modified` equals the file's current last-write time in UTC, exactly
- `analysisVersion` equals `4`

Otherwise it analyses the song again. A reader that only wants to show what's there can skip these checks.

**Is it usable?** The app needs `key` to parse and `grid` to have at least one marker, with marker times strictly increasing. Records that fail this are treated as not analysed.

**Automatic gain**, if a reader wants to reproduce the app's loudness matching:

```
gainDb = clamp(-14 - loudness, -24, +12)
if gainDb > 0 and peak > 0: gainDb = max(0, min(gainDb, -20 * log10(peak)))   // never boost into clipping
factor = 10 ^ (gainDb / 20)
```

### 1.4 Beat grids

A beat grid is a list of **markers** in time order. Each marker is `{ "time": seconds, "bpm": beatsPerMinute }`: there's a beat at `time`, then one every `60 / bpm` seconds until the next marker.

- **One marker** means a steady tempo, as nearly all dance music has.
- **Several** mean the tempo changes. The time from each marker to the next is (very nearly) a whole number of that marker's beats, so the next marker falls on a beat.
- **Before the first marker**, the grid runs backwards at the first marker's tempo. **After the last**, it runs on at the last marker's tempo.
- **Precision:** analysed grids store `time` to 4 decimal places and `bpm` as calculated. Hand-fixed grids store `time` to 5 places and `bpm` to 4.

**Beat numbering.** Beats are numbered from the first marker, which is beat 0; beats before it are negative. To convert between time and beat number:

```
beatNumberAt(t):
    beats = 0
    for i, m in enumerate(markers):
        if i is the last index or t < markers[i+1].time:
            return beats + (t - m.time) / (60 / m.bpm)
        beats += round((markers[i+1].time - m.time) / (60 / m.bpm))

timeOfBeat(b):
    start = 0
    for i, m in enumerate(markers):
        n = round((markers[i+1].time - m.time) / (60 / m.bpm)) if i is not the last index else infinity
        if b < start + n or i is the last index:
            return m.time + (b - start) * (60 / m.bpm)
        start += n
```

**Bars and downbeats.** Bars are 4 beats. `downbeat` (0–3) says which beats start a bar: beat number `b` (an integer) is the first beat of a bar when `((b - downbeat) mod 4 + 4) mod 4 == 0`. Its position in the bar, 0 to 3, is that same expression.

**Main tempo** (the `bpm` shown for a song):
- For one marker, it's that marker's `bpm`.
- For several, give each marker a span from its `time` to the next marker's (from 0 for the first, and to `duration` for the last). The main tempo is the weighted median `bpm` over those spans: sort them by `bpm` and take the first whose running total of seconds reaches half the total.

### 1.5 `hotcues`

```json
"hotcues": {
  "Music\\Some Artist\\Song.m4a": [12.346, null, null, 100.0, null, null, null, null]
}
```

- **The array:** each entry is a hotcue's position in seconds from the start of the song (rounded to 3 decimal places), or `null` where that hotcue isn't set. The entries are hotcues 1, 2, 3 and so on in order. The app has **8 hotcues** and writes 8 entries, but a reader should accept any length; missing entries mean not set.
- **Songs with no hotcues set** are left out of the map.

### 1.6 `grids`: beat grids fixed by hand

```json
"grids": {
  "Music\\Bobby McFerrin\\Don't Worry Be Happy.m4a": {
    "markers": [ { "time": 0.41203, "bpm": 120.0934 }, { "time": 50.1, "bpm": 122 } ],
    "downbeat": 3,
    "pins": [ 0.41203, 50.1, 52.07 ]
  }
}
```

| Property | Type | Meaning |
|---|---|---|
| `markers` | array | The grid as fixed by hand, in the same form as `tracks[…].grid` (see 1.4). |
| `downbeat` | integer, 0–3 | As in 1.4, for this grid's numbering. |
| `pins` | array of numbers | The times, in seconds and in order, at which beats were pinned to the music by hand. It may be empty or missing. These are informational for a reader, since the grid itself is `markers`. |

**Where a song has an entry in `grids`, that grid replaces the analysed one** (`tracks[…].grid` and `downbeat`) everywhere: for tempo, sync, beats, bars and display. Its length comes from the analysis's `duration`. An entry whose markers are empty, or not in increasing time order, is ignored.

Fixed grids survive re-analysis, since they're kept apart from `tracks`. A song can be in `grids` without being in `tracks`.

### 1.7 Keys

`key` is `"<tonic> <mode>"`, with:
- **Tonic:** one of `C C# D Eb E F F# G Ab A Bb B`, exactly as written: sharps for C#/F#, flats for Eb/Ab/Bb.
- **Mode:** `major` or `minor`.

Examples: `"F major"`, `"Eb minor"`, `"C# minor"`. Anything else isn't a valid key.

The app shows keys on the **Camelot wheel**. With `tonic` as an index into the list above (C = 0 … B = 11):

```
number = (tonic * 7 + (minor ? 5 : 8)) mod 12, with 0 meaning 12
camelot = number + (minor ? "A" : "B")          // e.g. A minor = "8A", C major = "8B"
```

Its short name is the tonic, plus `m` for minor (`"Ebm"`, `"F"`).

### 1.8 Writing

If a reader also writes the file, it should follow these rules so the app reads it back:

- Write to `library.json.tmp` in the same folder, then rename it over `library.json`.
- In a drive's file, write songs on that drive with keys relative to its root. A drive's file the app finds with absolute keys under its own root is rewritten relative on the next save.
- The JSON is written indented, without escaping `&`, `'` or non-ASCII characters. Readers must accept any valid JSON regardless.
- While Cuepoint is running it owns these files and saves them on its own schedule, overwriting outside changes. Write only while it's closed.

---

## 2. Virtual folders (`.m3u8`)

A virtual folder is a playlist: songs put together by hand, in order. Each is one `.m3u8` file, and **its name is the file's name without the extension.** For example, `Warm up.m3u8` is the virtual folder "Warm up".

### 2.1 Where they are

The folder is `Cuepoint\VirtualFolders\` in both places:

| Kept on… | Folder |
|---|---|
| The system drive | `%LOCALAPPDATA%\Cuepoint\VirtualFolders\` |
| Any other drive, e.g. `F:\` | `F:\Cuepoint\VirtualFolders\` |

Notes:

- Every `*.m3u8` file directly in those folders is a virtual folder; subfolders aren't read.
- Two folders can have the same name if they're on different drives. The app tells them apart by showing the drive, as in "Warm up (F:)".
- Names can't contain `\ / : * ? " < > |` or end in a full stop, since they're file names.

### 2.2 Contents

```
#EXTM3U
C:\Music\Artist\First song.mp3
..\..\Music\Other Artist\Second song.m4a
```

**What the app writes:**
- The first line is `#EXTM3U`.
- Then one song per line, in the folder's order.
- Lines end with `\n` (LF) only, including the last. The file is UTF-8 without a BOM.
- **Paths:**
  - For a folder kept on the system drive, every path is absolute.
  - For a folder kept on another drive, songs on **that same drive** are written **relative to the playlist's own folder**. So `F:\Music\x.m4a`, in a playlist in `F:\Cuepoint\VirtualFolders\`, is written as `..\..\Music\x.m4a`, which keeps working if the drive's letter changes. Songs on other drives are still absolute.

**How the app reads one** (and how a reader should, since playlists from other programs are accepted too):
1. Read it as UTF-8, splitting on `\n` or `\r\n`.
2. Trim each line's surrounding whitespace, and a leading BOM (`U+FEFF`) if present.
3. Skip blank lines, and any line starting with `#` (`#EXTM3U`, `#EXTINF:…` and other comments).
4. Every other line is a path. A relative path is relative to **the folder the `.m3u8` file is in**. Resolve it and normalise it, collapsing `..` and `.`.
5. Keep only the first occurrence of each path, compared case-insensitively. A song appears in a virtual folder once.
6. Keep the order.

**Missing songs:** a path whose file doesn't exist stays in the playlist and keeps its place, though the app doesn't list it. When the app saves the playlist after an edit, it keeps such paths in their positions.

### 2.3 Writing

The app writes to `<name>.m3u8.tmp`, then renames it over `<name>.m3u8`. As with the library files, edit playlists only while Cuepoint is closed. It reads them at startup, and rescans drives every few seconds as they're plugged in and removed.
