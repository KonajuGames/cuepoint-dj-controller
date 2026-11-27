"""
Drafts a Cuepoint controller mapping from a Mixxx one (a .midi.xml file), as a table to review against the
controller's own list of MIDI messages before anything goes in ControllerPresets.cs.

    python tools\\mixxx_mapping.py Pioneer-DDJ-SB.midi.xml
    python tools\\mixxx_mapping.py Pioneer-DDJ-SB.midi.xml --csharp
    python tools\\mixxx_mapping.py Pioneer-DDJ-SB.midi.xml --json "%LOCALAPPDATA%\\Cuepoint\\controllers\\DDJ-SB.json"

Each binding in the Mixxx file is one row: its message, the Cuepoint control it most likely is, and how sure that is.
"exact" rows are Mixxx controls that mean the same as Cuepoint's (play, hotcue_1_activate, filterLow). "guess" rows
are bound to a script function in Mixxx, so what they do is in its JavaScript; the guess goes by the function's name
(jogTouch, shiftButton) and needs checking. "none" rows have nothing to match in Cuepoint, or only Mixxx knows what
they do. Mixxx keeps Shift in its scripts when the controller sends the same message either way, so shifted actions
never come out of the XML: once the Shift button's found (the "shift" control), they're for adding by hand with
Shifted set, or learning with Shift held.

--csharp prints the exact and guessed bindings as lines for a preset in ControllerPresets.cs; --json writes the exact
ones (and the guesses too, with --guesses) as a mapping Cuepoint reads from its controllers folder, named after the
controller as Windows or macOS names it (--device, if that isn't the name in the Mixxx file), to try on the hardware.

Mixxx's mappings are GPL, and Cuepoint is MIT: this reads which messages a controller sends, which are facts about
the hardware, and none of the Mixxx file or its scripts is copied. Prefer the maker's own list of MIDI messages where
there is one, and use the Mixxx mapping to check it or fill its gaps.
"""

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET

# As Cuepoint's ControlCatalog.BeatLoopSizes, RollPads.Sizes and JumpPads.Sizes (pad numbers count from 1).
BEAT_LOOP_SIZES = [0.25, 0.5, 1, 2, 4, 8, 16, 32]
ROLL_SIZES = [1 / 16, 1 / 8, 1 / 4, 1 / 2, 1, 2, 4, 8]
JUMP_SIZES = [-4, 4, -8, 8, -16, 16, -32, 32]
HOTCUES = 8
SAMPLER_SLOTS = 16

DECKS = {"[Channel1]": "A", "[Channel2]": "B"}

# Mixxx's deck controls that are the same as Cuepoint's, by name.
DECK_CONTROLS = {
    "play": "play",
    "cue_default": "cue",
    "cue_cdj": "cue",
    "beatsync": "sync",
    "sync_enabled": "sync",
    "pfl": "pfl",
    "keylock": "keylock",
    "slip_enabled": "slip",
    "LoadSelectedTrack": "load",
    "eject": "unload",
    "reverseroll": "censor",
    "rate": "tempo",
    "rate_set_default": "tempo.reset",
    "reset_key": "key.reset",
    "pitch_adjust_set_default": "key.reset",
    "pitch_up": "key.up",
    "pitch_down": "key.down",
    "sync_key": "key.match",
    "volume": "volume",
    "pregain": "trim",
    "filterHigh": "eq.high",
    "filterMid": "eq.mid",
    "filterLow": "eq.low",
    "loop_in": "loop.in",
    "loop_out": "loop.out",
    "loop_halve": "loop.halve",
    "loop_double": "loop.double",
    "beatloop_activate": "loop",
    "loop_move_1_backward": "loop.back",
    "loop_move_1_forward": "loop.on",
    "loop_move_4_backward": "loop.backbar",
    "loop_move_4_forward": "loop.onbar",
    "jog": "jog",
    "reverse": "reverse",
    "rate_temp_up": "bend.up",
    "rate_temp_down": "bend.down",
    "quantize": "quantize",
    "playposition": "position",
    "filterLowKill": "eq.low.kill",
    "filterMidKill": "eq.mid.kill",
    "filterHighKill": "eq.high.kill",
}

# Mixxx's deck controls with no exact match, and the nearest Cuepoint control.
DECK_GUESSES = {
    "reloop_exit": ("reloop", "toggles: out of a loop, or back into the last one (perhaps loop.exit)"),
    "reloop_toggle": ("reloop", "toggles: out of a loop, or back into the last one (perhaps loop.exit)"),
}

# Mixxx's controls for the whole mix and the library.
GLOBAL_CONTROLS = {
    ("[Master]", "crossfader"): "crossfader",
    ("[Mixer]", "crossfader"): "crossfader",
    ("[Master]", "gain"): "master",
    ("[Master]", "headGain"): "headphones.volume",
    ("[Master]", "volume"): "master",
    ("[Master]", "headMix"): "headphones.mix",
    ("[Master]", "headVolume"): "headphones.volume",
    ("[Mixer]", "headMix"): "headphones.mix",
    ("[Mixer]", "headVolume"): "headphones.volume",
    ("[Playlist]", "SelectTrackKnob"): "browse",
    ("[Library]", "MoveVertical"): "browse",
    ("[Playlist]", "SelectPlaylist"): "browse.folders",
    ("[Library]", "MoveHorizontal"): "browse.folders",
    ("[Playlist]", "SelectNextTrack"): "browse.next",
    ("[Playlist]", "SelectPrevTrack"): "browse.prev",
    ("[Library]", "MoveDown"): "browse.next",
    ("[Library]", "MoveUp"): "browse.prev",
    ("[Playlist]", "SelectNextPlaylist"): "browse.folders.next",
    ("[Playlist]", "SelectPrevPlaylist"): "browse.folders.prev",
    ("[Playlist]", "ToggleSelectedSidebarItem"): "browse.press",
    ("[Library]", "GoToItem"): "browse.press",
}

# A script function's name, lowercased, and the control it's likely to be: the first whose words are all in it.
SCRIPT_GUESSES = [
    # Touching a wheel that only nudges or searches (vinyl mode off) mustn't hold the record.
    (("seek", "touch"), None),
    (("search", "touch"), None),
    (("jog", "touch"), "jog.touch"),
    (("wheel", "touch"), "jog.touch"),
    (("scratch", "touch"), "jog.touch"),
    (("scratch",), "scratch"),
    (("search",), "search"),
    (("seek",), "search"),
    (("jog",), "jog"),
    (("wheel",), "jog"),
    (("platter",), "jog"),
    (("browse", "press"), "browse.press"),
    (("browse", "click"), "browse.press"),
    (("selector", "click"), "browse.press"),
    (("browse",), "browse"),
    (("selector",), "browse"),
    (("tempo",), "tempo"),
    (("pitch",), "tempo"),
    (("rate",), "tempo"),
    (("crossfader",), "crossfader"),
    (("hotcue",), "hotcues"),
    (("roll",), "roll"),
    (("sampler",), "sampler"),
    (("sync",), "sync"),
    (("play",), "play"),
    (("cue",), "cue"),
    (("load",), "load"),
    (("keylock",), "keylock"),
    (("slip",), "slip"),
    (("vinyl",), None),
]

GLOBAL = {"shift", "crossfader", "master", "master.fx.on", "browse", "browse.folders", "browse.press", "headphones.mix", "headphones.volume", "sampler.stop"}

# Mixxx's effect units' controls, and the nearest Cuepoint deck control: Cuepoint has one effect a deck, so unit 1 is
# taken as deck A's and unit 2 as deck B's.
EFFECT_UNIT_GUESSES = {
    "enabled": "fx.on",
    "mix": "fx.level",
    "super1": "fx.level",
    "next_chain": "fx.next",
    "chain_selector": "fx.next",
}
EFFECT_GUESSES = {
    "enabled": "fx.on",
    "next_effect": "fx.next",
    "effect_selector": "fx.next",
}
EFFECT_UNIT_DECKS = {"1": "A", "2": "B"}
ENCODERS = {"jog", "scratch", "search", "browse", "browse.folders"}


def size_index(text, sizes):
    """The pad, from 1, for a size written as Mixxx writes it ("0.5", "4"), or None."""
    try:
        value = float(text)
    except ValueError:
        return None
    for i, size in enumerate(sizes):
        if abs(size - value) < 1e-9:
            return i + 1
    return None


def deck_control(key):
    """The Cuepoint deck control (without its deck) for a Mixxx deck control, or None."""
    if key in DECK_CONTROLS:
        return DECK_CONTROLS[key]
    if m := re.fullmatch(r"hotcue_(\d+)_(activate|clear)", key):
        n = int(m.group(1))
        if 1 <= n <= HOTCUES:
            return f"hotcue{n}" + (".delete" if m.group(2) == "clear" else "")
        return None
    if m := re.fullmatch(r"beatloop_([\d.]+)_(toggle|activate)", key):
        n = size_index(m.group(1), BEAT_LOOP_SIZES)
        return f"beatloop{n}" if n else None
    if m := re.fullmatch(r"beatlooproll_([\d.]+)_activate", key):
        n = size_index(m.group(1), ROLL_SIZES)
        return f"roll{n}" if n else None
    if m := re.fullmatch(r"beatjump_([\d.]+)_(forward|backward)", key):
        beats = float(m.group(1)) * (1 if m.group(2) == "forward" else -1)
        n = size_index(str(beats), JUMP_SIZES)
        return f"jump{n}" if n else None
    return None


def control_for(group, key, script):
    """(Cuepoint control, how sure, why) for a Mixxx group and key."""
    # Decks 3 and 4, in any of Mixxx's forms: [Channel3], [EqualizerRack1_[Channel3]_Effect1], group_[Channel3]_enable.
    if re.search(r"\[Channel[3-9]\]", group + key):
        return None, "none", "Cuepoint has two decks"
    deck = DECKS.get(group)
    if m := re.fullmatch(r"\[EqualizerRack1_(\[Channel\d\])_Effect1\]", group):
        deck = DECKS.get(m.group(1))
        part = {
            "parameter1": "eq.low",
            "parameter2": "eq.mid",
            "parameter3": "eq.high",
            "button_parameter1": "eq.low.kill",
            "button_parameter2": "eq.mid.kill",
            "button_parameter3": "eq.high.kill",
        }.get(key)
        if deck and part:
            return f"{deck}.{part}", "exact", ""
    if m := re.fullmatch(r"\[QuickEffectRack1_(\[Channel\d\])\]", group):
        deck = DECKS.get(m.group(1))
        if deck and key == "super1":
            return f"{deck}.filter", "exact", "Mixxx's quick effect, usually its filter"
    # Mixxx's samplers are small decks of their own; Cuepoint's sampler pads play from the start, as Mixxx's
    # start_play and cue_gotoandplay do.
    if m := re.fullmatch(r"\[Sampler(\d+)\]", group):
        slot = int(m.group(1))
        if slot > SAMPLER_SLOTS:
            return None, "none", f"Cuepoint has {SAMPLER_SLOTS} sampler pads"
        if key in ("start_play", "cue_gotoandplay"):
            return f"sampler{slot}", "exact", ""
        if key == "start_stop":
            return f"sampler{slot}", "guess", "plays, or stops if it's playing: Cuepoint's pad plays it again"
        if key == "stop":
            return "sampler.stop", "guess", "stops every sample, not just this one"
        if key == "LoadSelectedTrack":
            return None, "none", "put songs on sampler pads with Put on sampler pad"
    if m := re.fullmatch(r"group_(\[Channel\d\])_enable", key):
        if group.startswith("[EffectRack1_EffectUnit") and (target := DECKS.get(m.group(1))):
            return f"{target}.fx.on", "guess", "an effect unit switched on for this deck"
    if key == "group_[Master]_enable" and group.startswith("[EffectRack1_EffectUnit"):
        return "master.fx.on", "guess", "an effect unit switched on for the whole mix"
    if m := re.fullmatch(r"\[EffectRack1_EffectUnit(\d+)(_Effect(\d+))?\]", group):
        unit, effect = m.group(1), m.group(3)
        guesses = EFFECT_GUESSES if effect else EFFECT_UNIT_GUESSES
        if key in guesses:
            if unit not in EFFECT_UNIT_DECKS:
                return None, "none", f"Mixxx's effect unit {unit}: Cuepoint has one effect a deck, for two decks"
            # A unit's first effect stands for the deck's one effect; its others have nothing to be.
            if effect and effect != "1":
                return None, "none", f"effect {effect} of Mixxx's effect unit {unit}: Cuepoint has one effect a deck"
            target = EFFECT_UNIT_DECKS[unit]
            where = f"effect {effect} of Mixxx's effect unit {unit}" if effect else f"Mixxx's effect unit {unit}"
            return f"{target}.{guesses[key]}", "guess", f"{where}, taken as deck {target}'s effect"

    if script:
        name = key.lower()
        function = name.rsplit(".", 1)[-1]
        # The Shift button itself (shift, shiftButton, onShiftPressed), rather than something done with Shift held
        # (beatFxSelectShiftPressed), which is for adding by hand as a shifted binding.
        if re.fullmatch(r"(on)?shift(button|key|pressed|_?press)?", function):
            return "shift", "guess", f"script {key}"
        if "shift" in function:
            return None, "none", f"script {key}: done with Shift held, so a shifted binding to add by hand"
        for words, control in SCRIPT_GUESSES:
            if all(word in name for word in words):
                if control is None:
                    break
                if control in GLOBAL:
                    return control, "guess", f"script {key}"
                if deck:
                    return f"{deck}.{control}", "guess", f"script {key}"
                break
        return None, "none", f"script {key}: see the mapping's JavaScript"

    if (group, key) in GLOBAL_CONTROLS:
        return GLOBAL_CONTROLS[(group, key)], "exact", ""
    if deck and (control := deck_control(key)):
        return f"{deck}.{control}", "exact", ""
    if deck and key in DECK_GUESSES:
        control, why = DECK_GUESSES[key]
        return f"{deck}.{control}", "guess", why
    return None, "none", "nothing to match in Cuepoint"


def options_of(control):
    options = control.find("options")
    return {child.tag.lower() for child in options} if options is not None else set()


def read_mapping(path):
    """The Mixxx mapping's name and its bindings, as dicts, one per message and control."""
    root = ET.parse(path).getroot()
    name = (root.findtext("info/name") or "").strip()
    rows = {}
    fine = []
    for control in root.iter("control"):
        group = (control.findtext("group") or "").strip()
        key = (control.findtext("key") or "").strip()
        try:
            status = int((control.findtext("status") or "").strip(), 16)
            number = int((control.findtext("midino") or "").strip(), 16)
        except ValueError:
            continue
        kind = {0x8: "note", 0x9: "note", 0xB: "controlChange"}.get(status >> 4)
        if kind is None:
            continue
        options = options_of(control)
        if "fourteen-bit-lsb" in options:
            fine.append((group, key, kind, status & 0x0F, number))
            continue
        # A script function's named Object.function; Mixxx's own controls only have dots in sizes (beatloop_0.5_toggle).
        script = "script-binding" in options or re.fullmatch(r"[A-Za-z$][\w$]*\.[A-Za-z$][\w$.]*", key) is not None
        target, sureness, why = control_for(group, key, script)
        row = rows.setdefault(
            (kind, status & 0x0F, number, group, key),
            {
                "group": group,
                "key": key,
                "kind": kind,
                "channel": status & 0x0F,
                "number": number,
                "fine": None,
                "invert": "invert" in options,
                # Mixxx's selectknob (and hercjog) count back from 127; diff, rot64 and spread64 from 64.
                "steps": "twosComplement" if options & {"selectknob", "hercjog"} else "offset64",
                "relative": bool(options & {"selectknob", "hercjog", "diff", "rot64", "spread64", "spread64inv"}),
                "control": target,
                "sure": sureness,
                "why": why,
            },
        )
        row["invert"] = row["invert"] or "invert" in options
    for group, key, kind, channel, number in fine:
        for row in rows.values():
            if row["group"] == group and row["key"] == key and row["kind"] == kind and row["channel"] == channel:
                row["fine"] = number
    return name, sorted(rows.values(), key=lambda row: (row["channel"], row["kind"], row["number"], row["key"]))


def message(row):
    text = f"Note 0x{row['number']:02X}" if row["kind"] == "note" else f"CC 0x{row['number']:02X}"
    if row["fine"] is not None:
        text += f"/0x{row['fine']:02X}"
    return text + f", ch {row['channel'] + 1}"


def print_table(name, rows, out):
    out.write(f"# {name or 'Mixxx mapping'}: a draft for Cuepoint\n\n")
    out.write("| Message | Mixxx | Cuepoint | Sure | Notes |\n|---|---|---|---|---|\n")
    for row in rows:
        notes = [row["why"]] if row["why"] else []
        if row["invert"]:
            notes.append("inverted")
        if row["relative"]:
            notes.append(f"relative, {row['steps']}")
        out.write(
            f"| {message(row)} | {row['group']} {row['key']} | {row['control'] or ''} | {row['sure']} | {'; '.join(notes)} |\n"
        )
    counts = {sure: sum(1 for row in rows if row["sure"] == sure) for sure in ("exact", "guess", "none")}
    out.write(f"\n{counts['exact']} exact, {counts['guess']} guessed, {counts['none']} with nothing to match.\n")


def bindings(rows, guesses):
    """The rows to bind, one per message and control, in Cuepoint's form."""
    seen = set()
    for row in rows:
        if row["control"] is None or (row["sure"] == "guess" and not guesses):
            continue
        key = (row["control"], row["kind"], row["channel"], row["number"])
        if key in seen:
            continue
        seen.add(key)
        yield row


def kind_of(control):
    name = control.split(".", 1)[-1] if control.split(".", 1)[0] in ("A", "B") else control
    return "encoder" if name in ENCODERS else "other"


def to_json(rows, device, guesses):
    out = []
    for row in bindings(rows, guesses):
        binding = {"control": row["control"]}
        if row["kind"] != "note":
            binding["kind"] = "controlChange"
        if row["channel"]:
            binding["channel"] = row["channel"]
        if row["number"]:
            binding["number"] = row["number"]
        if row["fine"] is not None:
            binding["fine"] = row["fine"]
        if row["invert"]:
            binding["invert"] = True
        if kind_of(row["control"]) == "encoder" and row["steps"] == "twosComplement":
            binding["steps"] = "twosComplement"
        out.append(binding)
    return {"device": device, "bindings": out}


def print_csharp(rows, out):
    for row in bindings(rows, guesses=True):
        comment = f" // {row['group']} {row['key']}" + (" (guess)" if row["sure"] == "guess" else "")
        if row["kind"] == "note":
            out.write(f'Note("{row["control"]}", {row["channel"]}, 0x{row["number"]:02X});{comment}\n')
            continue
        args = [f'"{row["control"]}"', str(row["channel"]), f"0x{row['number']:02X}"]
        if row["fine"] is None:
            args.append("fine: false")
        elif row["fine"] != row["number"] + 32:
            comment += f" (fine half 0x{row['fine']:02X}, not 32 above: bind it by hand)"
        if kind_of(row["control"]) == "encoder" and row["steps"] == "twosComplement":
            args.append("steps: StepEncoding.TwosComplement")
        if row["invert"]:
            comment += " (inverted in Mixxx)"
        out.write(f"Control({', '.join(args)});{comment}\n")


def main(argv=None):
    parser = argparse.ArgumentParser(description="Draft a Cuepoint controller mapping from a Mixxx .midi.xml file.")
    parser.add_argument("mapping", help="the Mixxx mapping (.midi.xml)")
    parser.add_argument("--csharp", action="store_true", help="print lines for a preset in ControllerPresets.cs")
    parser.add_argument("--json", metavar="PATH", help="write a mapping Cuepoint can read from its controllers folder")
    parser.add_argument("--guesses", action="store_true", help="put the guessed bindings in the --json mapping too")
    parser.add_argument("--device", help="the controller's name as the computer gives it, for --json")
    args = parser.parse_args(argv)

    name, rows = read_mapping(args.mapping)
    if args.csharp:
        print_csharp(rows, sys.stdout)
    else:
        print_table(name, rows, sys.stdout)
    if args.json:
        device = args.device or name
        with open(args.json, "w", encoding="utf-8") as file:
            json.dump(to_json(rows, device, args.guesses), file, indent=2)
            file.write("\n")
        print(f"\nWrote {args.json} for a controller named \"{device}\".", file=sys.stderr)


if __name__ == "__main__":
    main()
