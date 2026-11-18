# True Acoustics – Echo & Matter (BETA)

*Formerly **Materialklang** (Material Sound). Same mod, same settings file – only the name changed: it does much more than material sounds now.*

Sound that knows what a wall is made of and which way the sound really travels. Concrete swallows the highs and lets a dull thump through, a wooden door is a weak barrier, a steel door a strong one, a fight in the next hall comes in through the door it really comes through – and a gunshot behind you sounds different from one in front of you. Works for **all** sounds in the game, not just gunshots.

**🎧 Hear the difference yourself:** [True Acoustics Demo](https://claude.ai/artifact/7BXLomAnuBnL6ofqfq41RC) – vanilla vs. True Acoustics side by side, plus a sound sandbox where you build walls, doors and windows (or load a real map) and listen live. Headphones recommended.

> **BETA – version 0.8.9-beta.** This version has been played in real raids, but by **one tester**, mostly on **Factory** (plus a first check on Customs). Several parts were added in the last days and are running but not yet judged by ear (see the labels below). Expect rough edges and values that still need tuning. Feedback is very welcome: please open an [issue](https://github.com/Kaanori/spt-true-acoustics/issues) and, if you can, attach the relevant lines of `BepInEx/LogOutput.log` (they start with `[Diagnostics]`, `[Reverb]` or `[Calibration]`).

**Version numbers:** this release is **0.8.9** everywhere (Forge, GitHub, DLL/log). 0.8.9 = 0.8.8 plus the map data, which is now included in the download; everything else is unchanged. The two hotfixes before 0.8.8 were published as 0.8.6 and 0.8.7 on the Forge and as v0.8.5.1 and v0.8.5.2 on GitHub – same files, different numbers. Since 0.8.8 there is one number.

## How this mod was made (AI disclosure)

This mod was **programmed by Claude**, an AI assistant made by Anthropic.
**Kaan was the tester**: Kaan has no programming background, played with the mod
on SPT 4.1.6, reported what went wrong, and Claude fixed it. No human programmer
has reviewed the code. It is open source so anyone can check it — if you find a
bug, please open an [issue](https://github.com/Kaanori/spt-true-acoustics/issues).

## Requirements

- SPT 4.1.6 (EFT 0.16.9). BepInEx ships with SPT.
- Designed as an add-on for [Sound Engine Backport](https://github.com/RedTheFoxx/SPT-SEB) (SEB 1.0.x / 1.1.x): recommended, but not required – without it the weapon directivity, the close-recording mode and the air absorption of gunshots switch off, everything else keeps working. Optional: SAIN 4.5.1 (bot hearing, off by default). The reverb zones use the Meta XR Audio library that ships with the game.

## Installation

1. Download `Materialklang-0.8.9-beta-with-map-data.zip` from the [Releases](https://github.com/Kaanori/spt-true-acoustics/releases) page.
2. Extract it **into your SPT game folder** (the folder that contains `EscapeFromTarkov.exe`).
   The ZIP already has the right layout; the mod ends up in `BepInEx/plugins/`.
   On Windows, make sure "Extract All" does not add an extra subfolder.
3. Restart the game.

## Uninstall

Delete the mod's files from `BepInEx/plugins/` (settings: `BepInEx/config/` for client mods) and restart the game.

## At a glance: what's in, what still causes problems, what's coming

How sure we are about each point:

| Label | Meaning |
|---|---|
| **[heard]** | Played in raids and judged by ear by the tester. |
| **[log]** | Running in raids; the sound log / BepInEx log (or a recording) shows it works as intended. Not separately judged by ear. |
| **[built]** | Built and checked in the offline tests, running in the tester's game, but not yet verified in a raid. |
| **[off]** | Experimental, **off by default**. |

### What's in (0.8.9-beta)

**Loudness and distance**
- **[heard]** **One loudness curve instead of a flat factor.** Insulation now counts in real decibels (`Loudness factor` 1 = real physics: quiet sounds behind thick concrete are gone). Only very loud sounds are squeezed, like the game mix does it: above a real level of 85 dB(A) at your ear each real dB counts 0.2 dB (`Compression knee (dB(A))`, `Compression above knee`). A rifle shot behind a closed door stays clearly audible, footsteps behind the same door get quiet. The knee was tried at 95 and set back to 85 by ear.
- **[heard] on Factory** **Other people's shots always use the close recording** (`13. Distance (SEB) → Near recording at every distance (physics does the rest)`). Distance then comes only from physics – level, air (ISO 9613-1 over the whole distance), walls – instead of Sound Engine Backport's thinner far recordings. The stereo width of the close recording narrows with distance.
- **[log]** **Distance of the close recording through the loudness curve** (`Near recording: distance through the loudness curve`). Added after the first raids with the close recording: plain 1/r made distant shots far too quiet. On Customs it applied to every shot of other players and bots, up to 400 m; not yet judged by ear on the large maps.
- **[log]** **Explosions** behind walls and rooms are squeezed as much as the game squeezes explosions over distance (`Explosions: compression from the game distance curve`).
- **[built]** **Calibrated source levels** (`17. All fixed sources → Calibrate source levels`): the V-Ex taxi, BTR, fires, generators, alarms and dripping water play at their real level relative to footsteps, and fall off with a physical distance law (point source 1/r, long sources like trains and the BTR more slowly near by).
- **[log]** **Lamp hum at a real level** (`Lamps: real hum level (direct sound + room field)`, `Lamps: hum level at 1 m (dB(A))` = 45): fluorescent lamps were the only continuous source without level calibration and could be heard through rooms.

**Doors, windows and walls**
- **[log]** **Doors and windows by their real leaf:** material and measured thickness of each door leaf or pane, per band (250 Hz / 1 kHz / 4 kHz), with the unsealed gap around it (doors at most about 23 dB, windows about 30 dB). Replaces the five fixed door/window values of 0.8.5. Doors are found in local raids (fix of 0.8.5.1).
- **[built]** **Gate wings** of big steel gates (e.g. Factory) let sound through with the small pass door (`4. Doors and Windows → Gate wings: count with the opening`).
- **[built]** **Several walls in a row** count wall by wall with the rooms in between; glass panes, double shells, thin sheet metal (2 mm) and the reverberant field of the source room are part of the wall calculation.
- **[built]** **Outdoors around buildings:** when the game knows no route, sound goes over roofs and terrain (ISO 9613-2 screening) and rows of houses add their own loss (`Room paths: housing along the path (ISO 9613-2 A.3)`); a big opening (bunker hatch, hall gate) radiates towards where you stand instead of equally in all directions.

**Direction – sound comes from where it really comes in**
- **[log]** **From the opening:** sound through doors and windows is heard from the opening it comes out of, not through the wall from the source. When openings lie on different sides, the side of the **first sound front** leads, like your ear locates it (`Room paths: direction from the leading path`, `Room paths: first sound front from all paths`). Impacts, casings, doors, windows and grenades in other rooms follow the same rule (`Room paths: direction for one-shot sounds`).
- **[log]** **Several ways at once:** when a sound reaches you through openings in clearly different directions, each way plays as its own voice from its opening, with its own share of energy, timbre and travel delay (`Room paths: second path as its own voice`, up to 3 voices per sound). For enemy footsteps/gear, voices and one-shot sounds; not for gunshots.
- **[log]** **Lamps** carry their hum on an invisible carrier, so it can be moved to the opening (`Room paths: carrier for fixed sources with visible parts`).
- **Precomputed obstacle paths – new in 0.8.9: map data included in the download** (`19. Path search (experimental) → Precomputed obstacle paths`): inside buildings, when a pillar, machine or container blocks the line and the game finds no way around, level and direction come from a table calculated in advance for the map (11 maps, indoor areas only). **[log] on Factory** on the tester's PC with the previous table format; **[log]** with the map data of this release (format 8) on Factory on the tester's PC (table loaded, 335 game files checked in 0.4 s). Loading on another PC is **[built]**: checked under Linux and with Windows paths under Wine, not on a real second Windows PC and not against a second installation – please report which line your BepInEx log shows (see [Installation](#installation)).

**Timbre and filters**
- **[log]** **Low-pass fitted to the game's real filter** (`14. Sound Paths → Low-pass fit for the 24 dB/oct game filter`, on): measured on 17.11.: the game's low-pass falls 24 dB per octave, not 12. Shots through doors keep their highs instead of losing a whole band above the mids.
- **[log]** **No bass cut on the path** (`0. Physics Mode → No bass cut on the path (high-pass)`, on): the game's 80 Hz high-pass cut 15–27 dB at 63–100 Hz off every shot. A recording showed the bass restored (63 Hz back within about 3 dB of the clip).
- **[log]** **Occluded shots stay in the normal mix** (`7. Weapon Directivity → Occluded shots: no extra occluded mix (physics)`, on): Sound Engine Backport played an extra unfiltered copy of occluded shots (up to +6 dB and a jump at its threshold).
- **[built]** **Muffling follows the loudness curve** (`Timbre follows loudness curve`): a shot that stays loud behind a wall is no longer loud and very dull at the same time.

**Rooms and reverb**
- **[log]** **Reverb zones per part of a room** instead of one box over the whole room, with **room absorption measured from random lines through each room** (`9. Reverb by Material → Room absorption from random chords`): machines, crates and walkways count. The big halls on Factory now come out at about 4–5 s (1 kHz) instead of 5.5–7 s. Measured once per map and cached.
- **[log]** **The room part of a sound from another room stays audible as dry sound** (`Diffuse sound stays audible`): the game's reverb plays that part 30–50 dB too quietly (measured). Only the part that arrives with the event counts for short sounds, and voices from big halls only with their intelligible early part. In a listening test, this "dry" version was the one that sounded right.

**Game bugs fixed**
- **[log]** **One-shot sounds start only after their occlusion** – no more short full-volume click (about 1/50 s) of impacts and ricochets behind walls; a recording showed 30 such spikes before the fix.
- **[log]** **Doors, containers, switches and breaking windows start silent** until their path is calculated (same switch; in a Customs raid every door and window sound in other rooms started muffled).
- **[log]** **Grenade bounces** behind walls were never occluded (pooled sound kept the old short range) – fixed (`Occlusion range follows the playing sound`).
- **[built]** **Audio culling** of the game only ever handled its last 10 looping sources; fires, the V-Ex taxi, machines and lamps could stay silent for a whole raid (`1. General → Fix game audio culling (all looping sources)`).
- **[log]** **Steadier sound:** no low-pass flutter ("robot sound"); no clicks from the mod found in raid recordings. **[built]** No flaring start of footsteps (`Idle sources keep their level`).

**Compatibility and tools**
- **[log]** Sound Engine Backport **1.0.x and 1.1.x** (since 0.8.7, on GitHub v0.8.5.2; played with 1.1.1). **[built]** SEB's legacy (V1) weapons now get weapon directivity and the distance rules too (`Cover SEB legacy (V1) weapons`).
- **[log]** **Test source** (section 20) for measurements: place a sound somewhere and play it from elsewhere.
- **[off]** `21. Gunshot spatialization (experimental)`, `22. Meta occlusion (experimental)`, `19. Path search (experimental)` (needs a map network that is not shipped), `Save heard sounds for the demo`.
- **Switched off after a listening test:** a real reverb tail for sounds from other rooms (`Room paths: diffuse part as real reverb`) was built and ran in raids, but every variant sounded wrong to the tester; only the dry version sounded right. It is **off** and not being developed further.

### Known issues – what still causes problems

- **Mostly tested on Factory, by one tester.** The large outdoor maps (Customs, Woods, Shoreline, Reserve, Streets, Lighthouse, Interchange) have been played much less with this version (Customs: one raid, checked in the logs).
- **Loudness of distant shots is still being tuned.** With the close recording, distant shots first came out far too quiet; the curve-based distance (see above) works in the logs (Customs, up to 400 m), but has not been judged by ear on the large maps yet. If shots sound wrong to you, switch `13. Distance (SEB) → Near recording at every distance (physics does the rest)` off – then shots use Sound Engine Backport's recordings again, with the far-shot curve of section 13 (or SEB's own curve if you also switch `13. Distance (SEB) → Enabled` off).
- **Distant shots fade out at the game's range limit** (indoors about 160–200 m, suppressed shots outdoors about 240–300 m). That fade is now the biggest level loss for far shots.
- **Shots from a neighbouring hall can sound almost unmuffled.** Big halls connected by large openings let a lot through, and loud sounds are squeezed by the loudness curve. The "only what arrives with the event" limit does not apply to gunshots and impacts yet (their clip length is not known to the mod).
- **Keycard reader beeps** are not covered by the silent start of door sounds; their first audio block can still slip out at full volume.
- **Lamp hum level still needs tuning.** First measurement (Factory): by the mod's own level reference the game plays lamps at about 35 dB(A) in 1 m – not louder than a real lamp – but with an almost flat distance curve (−6 dB at 10 m instead of −20 dB). With the default of 45 dB(A) the mod therefore changes little, and lamps behind open doors can still be heard. Lower `Lamps: hum level at 1 m (dB(A))` (for example to 35) if lamps are too loud.
- **Obstacle in the same room:** a pillar or machine between you and a source in the same hall lowers the direct sound, but the hall's own reverberant field is not added back to the dry sound yet, so it can muffle more than in reality.
- **Map data (precomputed obstacle paths):** indoor areas only – outdoors, and where no map data is loaded, the ISO 9613-2 screening limit applies (at most 20 dB for one edge, 25 dB for several) and the sound is heard from the source, not from the edge. Five maps (Reserve, Ground Zero, Streets, Interchange, Labs) still use an older edge model, which makes paths around edges slightly louder; on Interchange long distances often have no value yet. Map variants from other mods (e.g. backported maps) are not covered. After a game update the data no longer matches and is not loaded (the mod keeps working). In corridors with a clear view, the walls of the corridor do not add level yet.
- **Some metal is too heavy:** hollow barrels and some large metal plates (cranes, wall panels) count as solid steel and insulate up to about 45 dB.
- **Performance.** On a busy Customs raid the filter part of the mod took about 3 % of the game's main thread, with single frames up to 16 ms. In the first raid on a map, the rooms are measured in the background (1 ms per frame; about 16 s on Factory, about 70 s on Reserve), afterwards it is cached. Setting up the reverb zones at raid start can take up to about 1 s on big maps. If you see frame drops, lower `10. Advanced (rays) → Transmission rays x` / `Reflection rays x` (4 in this release, 1 = game value; from the next raid) or switch off `5. Walls Between Rooms`, `9. Reverb by Material` or the extra voices (`Room paths: second path as its own voice`) first.
- **If you switch the close recording off:** the distance curve of `13. Distance (SEB)` (`Enabled`, on in this release) then applies to other people's shots again, and with its `Far clip compensation (dB)` a shot at 100 m can be louder than one at 50 m. With the close recording on (default) this curve is not used for other people's shots; section 13 then only extends the outdoor range of unsuppressed shots to 900 m and keeps the flatter explosion curve.
- **Volume steps at indoor/outdoor changes** can still happen: the game switches its indoor/outdoor factor at once when you or a bot step through a door.
- **Not tested with Guns Are Loud** or other mods that change the mixer. If something sounds odd, switch True Acoustics's sections off one by one in F12.
- **Bot hearing (SAIN) is experimental** and off by default; it changes how far bots hear you.
- **Game updates** can break hooks (that part then stays vanilla, with a log warning).

### What's coming

- Check and tune distant shots on the large outdoor maps (close recording + loudness curve).
- Handle the fade at the game's range limit for distant shots.
- Silent start for keycard reader beeps.
- Clip lengths for gunshots and impacts, so the room part of a shot from another hall is limited like for footsteps.
- Tune the lamp level by ear (first measurement: the game's 1 m level is realistic, its distance curve is too flat).
- Rebuild the map data of Reserve, Ground Zero, Streets, Interchange and Labs with the corrected edge model; level gain in corridors (after a measurement).
- Better timing for the extra voices (today about 21 ms start uncertainty hides many second paths), and extra voices for looping sounds.
- Lower the frame-time peaks of the filter part.
- Later, no date yet: better outdoor propagation around buildings, a check together with Guns Are Loud.

## How this mod was made (AI disclosure)

This mod was **programmed by Claude**, an AI assistant made by Anthropic.
**Kaan was the tester**: Kaan has no programming background, played with the mod
on SPT 4.1.6, reported what went wrong, and Claude fixed it. No human programmer
has reviewed the code. It is open source so anyone can check it — if you find a
bug, please open an [issue](https://github.com/Kaanori/spt-true-acoustics/issues).

## Requirements

- SPT 4.1.6 (EFT 0.16.9). BepInEx ships with SPT.
- Designed as an add-on for [Sound Engine Backport](https://github.com/RedTheFoxx/SPT-SEB) (SEB 1.0.x / 1.1.x): recommended, but not required – without it the weapon directivity, the close-recording mode and the air absorption of gunshots switch off, everything else keeps working. Optional: SAIN 4.5.1 (bot hearing, off by default). The reverb zones use the Meta XR Audio library that ships with the game.

## Installation

1. Download `Materialklang-0.8.9-beta-with-map-data.zip` (189 MB) from the [release page](https://github.com/Kaanori/spt-true-acoustics/releases/tag/v0.8.9-beta). It is the only file: mod and map data in one download (one VirusTotal report for exactly this file).
2. Extract it **into your SPT game folder** (the folder that contains `EscapeFromTarkov.exe`).
   The ZIP already has the right layout; the mod ends up in `BepInEx/plugins/`, the map data in `BepInEx/cache/TrueAcoustics/`.
   On Windows, make sure "Extract All" does not add an extra subfolder.
3. Restart the game.

**Defaults are the author's tested settings.** The defaults (since 0.8.8) are the settings the tester played with. **If you update from 0.8.7 or older, BepInEx keeps your saved values** – to get the tested state, either delete `BepInEx/config/kaan.materialklang.cfg` once (all your own changes are lost) or set these by hand in F12:

- `0. Physics Mode (real values)`: `Loudness factor` 1 (was 0.2 – an old 0.2 squeezes on top of the new loudness curve); `No bass cut on the path (high-pass)` on; `Quiet sources: real audibility` off; `Room paths: diffuse part as real reverb` off
- `7. Weapon Directivity`: `Occluded shots: no extra occluded mix (physics)` on
- `9. Reverb by Material`: `Min. absorption` 0.08 (no effect any more, listed for completeness)
- `10. Advanced (rays)`: `Transmission rays x` 4, `Reflection rays x` 4, `Max. bounces` 6 (from the next raid)
- `13. Distance (SEB)`: `Enabled` on; `Near recording at every distance (physics does the rest)` on
- `14. Sound Paths (physics)`: `Band low-pass strength` 0.6; `Low-pass fit for the 24 dB/oct game filter` on
- `15. Ambient emitters`: `Indoor room tone volume (%)` 30

`3. Bot Hearing (SAIN) → Mode` stays **Off** by default.

**What is in the download:** the mod itself (`BepInEx/plugins/Materialklang/`, under 1 MB) and the map data for the precomputed obstacle paths (`BepInEx/cache/TrueAcoustics/`, one `.wegtabelle` file per map, 11 maps, about 480 MB unpacked). The 189 MB of the download are almost entirely the map data.

- The map data only fits **SPT 4.1.6 / Escape from Tarkov 0.16.9.40743**. At raid start the mod checks name, size and a content digest of the game files each table was made from (the first raid on a map reads the files once, later raids use a small digest cache). If they differ, the table is simply not loaded and the mod keeps working with the ISO 9613-2 screening limit.
- Check the BepInEx log at raid start: `Precomputed obstacle paths: map … loaded …` means it works; `… no precomputed obstacle paths for this map (<reason>) …` means it was not used and why. **Please tell us which line you see** – loading on other PCs has not been confirmed yet.
- Older map data files with the same map name from earlier versions can be deleted.
- **Deleting `BepInEx/cache/TrueAcoustics/`** makes sense if your game is not EFT 0.16.9.40743 (the map data would never load and only takes disk space), after a game update, or if you want the mod without the precomputed paths. The mod then uses the screening limit and the log shows one info line per raid (`… they come with the separate map data download …` – the wording is older; the map data is included in the download now). Your other cache files there are recreated automatically.

The map data contains derived numbers only (room names, probe points, levels and path lengths, the list of game file names with sizes and digests) – no meshes, textures or sounds.

The mod also writes small files of its own to `BepInEx/cache/TrueAcoustics/` (room absorption measured per map, the footstep reference for the level calibration, the digest cache for the map data). They are created on your PC and can be deleted at any time.

## Uninstall

Delete `BepInEx/plugins/Materialklang/` (settings: `BepInEx/config/kaan.materialklang.cfg`, map data and caches: `BepInEx/cache/TrueAcoustics/`) and restart the game.

## What it does, in plain words

Escape from Tarkov (and SPT) already shoots invisible rays between a sound and your ears to decide how muffled it is, and it knows which rooms are connected by which doors. But it treats every wall the same: a thin plywood board and a thick concrete wall differ only in how many metres the ray travelled through them, and a sound from the next room always seems to come straight through the wall.

True Acoustics looks at what the ray actually hit (concrete, wood, metal, glass, soil, ...) and works out the insulation from that, per frequency band. It follows the sound through the openings it really takes, plays it from the direction it comes in, and fixes a few bugs of the game's own audio system on the way.

It is an add-on for **Sound Engine Backport (SEB)** and is meant to be played together with it, but most of it also works without.

## Features

| Feature | What you hear |
|---|---|
| **Physics Mode** (on by default) | Real building acoustics: the insulation of a wall follows the *mass law* (heavier and thicker = quieter), per band 250 Hz / 1 kHz / 4 kHz, with coincidence correction, flanking and the rooms on both sides. Thick concrete is nearly silent, a plank is not. Applies to every sound with an obstacle in the way. |
| **Loudness curve** (Physics Mode) | Insulation counts in real decibels; only very loud sounds (gunshots, explosions, impacts) are squeezed above a knee, like the game mix squeezes them. |
| **Material table** (sections `2.xx`) | The simpler, hand-tunable alternative to Physics Mode: attenuation per metre and base attenuation for 15 material groups. Used when Physics Mode is off. |
| **Doors and windows** | Each closed door leaf and window pane insulates by its own material and measured thickness, with the gap around it; partly open doors and broken windows let the open part through; big gate wings count. |
| **Walls between rooms** | Vanilla only lets sound travel between rooms through doors and windows. True Acoustics also measures the straight line through the walls (several walls in a row, wall by wall) and adds both ways as energy. |
| **Room paths and direction** | Sound through openings is calculated from the real path (edge diffraction at the openings, the rooms in between) and heard from the opening it comes out of; several ways at once can play as separate voices. |
| **Precomputed obstacle paths** | Inside buildings: ways around pillars and machines that the game does not find can come from a table prepared per map (map data included in the download). |
| **Timbre** | Materials filter highs and lows differently. In Physics Mode the band calculation does this; the filter is fitted to the game's real 24 dB/octave low-pass, and the game's bass cut is removed. |
| **Weapon directivity** | Other people's gunshots are quieter and duller when the muzzle points away from you (less so indoors). Needs SEB. |
| **Distance of gunshots** (SEB) | Close recording at every distance, distance through physics and the loudness curve; plus a longer outdoor range (section 13). |
| **Air absorption** | Highs fade with distance, calculated with ISO 9613-1 from the raid's temperature and pressure and an estimated humidity. |
| **Forest** | Sound through dense forest loses level and highs (ISO 9613-2 foliage table); trees counted from the map at raid start. |
| **Fixed and scripted sources** (Physics Mode) | Ambient emitters, alarms, vehicles, trains, airdrops, flares, the mortar whistle and doors carry physically further, at a calibrated level, and are muffled by walls even where the game never occludes them. |
| **Reverb by material** | At raid start every interior room is measured (wall materials, furniture via random chords), and Meta XR Audio *control zones* per part of the room shift reverb time and level. |
| **Bot hearing** (off by default) | With SAIN installed, bots can hear through walls according to the material in between. Optional and experimental. |
| **Game fixes** | Audio culling of looping sources, one-shot and door sounds that slipped out unmuffled, grenade bounces that were never occluded. |
| **Sound log, live tuning, test source** (off by default) | Developer/tuning tools: one log line per sound near you, a JSON file that changes settings while the game runs, a test sound you can place and play. See [Sound log and live tuning](#sound-log-and-live-tuning). |

Every part has its own switch in the F12 menu. Switch off `1. General → Enabled` and you get vanilla sound back.

## Requirements and compatibility

| | |
|---|---|
| **Tested with** | SPT 4.1.6 (EFT `0.16.9.40743`), Sound Engine Backport 1.1.1 (earlier versions with 1.0.3/1.0.4/1.1.0), SAIN 4.5.1, BepInEx 5; developed and played on Linux (Proton). |
| **Files** | `Materialklang.dll` -> `BepInEx/plugins/Materialklang/` (GUID `kaan.materialklang`) |
| **Settings** | `BepInEx/config/kaan.materialklang.cfg` (F12 menu, English labels) |
| **License** | MIT |

- **Sound Engine Backport (SEB)** – *recommended, not required* (soft dependency). Checked against SEB 1.0.x, 1.1.0 and 1.1.1. With another SEB version the SEB parts stay off (log warning), and if SEB changes something True Acoustics relies on, those parts switch themselves off for the session (log error) while shots keep playing normally. Without SEB, **weapon directivity**, the **close-recording mode** and the **air absorption of gunshots** switch off (the log says so); everything else only touches the base game's own audio system. Running it *without* SEB has not been tested.
- **SAIN** – optional, only for **bot hearing**, which is **off** by default. Tested with SAIN 4.5.1.
- **Meta XR Audio** – not something you install: it is part of the game and is what the game itself uses for room reverb. The reverb zones talk to it directly. If it is unavailable the zones are skipped and the rest keeps working.
- It hooks into internal parts of the game's audio system, so a **game update can break parts of it**. Where a hook target is missing, True Acoustics logs a warning and that part stays vanilla.
- Other mods that change volume, room sound or the mixer can fight with it. **Guns Are Loud** has not been tested together with this version.

## Settings overview (F12)

Sections and the most important settings with their defaults in 0.8.9-beta (the tester's settings). All values have tooltips in the menu, and almost all apply **live**.

| Section | Key settings (default) |
|---|---|
| **0. Physics Mode (real values)** | `Enabled` (on); `Max. insulation (dB)` (60); `Loudness factor` (**1**, was 0.2); `Compression knee (dB(A))` (85); `Compression above knee` (0.2); `Explosions: compression from the game distance curve` (on); `Timbre follows loudness curve` (on); `No bass cut on the path (high-pass)` (on); `Coincidence correction` (on); per material group `Density` and `Solid up to`. **Room paths:** `direction from the opening` (on), `direction from the leading path` (on), `first sound front from all paths` (on), `direction for one-shot sounds` (on), `second path as its own voice` (on), `voices per sound` (3), `carrier for fixed sources with visible parts` (on), `physical openings` (on), `diffraction when the game has no route` (on), `housing along the path (ISO 9613-2 A.3)` (on), `diffuse part as real reverb` (**off**). **Diffuse part:** `Diffuse sound as reverb` (on), `Diffuse sound stays audible` (on), `Diffuse sound: only what arrives with the event` (on), `Speech from other rooms: only the intelligible early part` (on), `Obstacle in the same room keeps the reverb` (on). **Fixes:** `Idle sources keep their level` (on), `Occlusion range follows the playing sound` (on), `One-shot sounds start only after their occlusion` (on). `Quiet sources: real audibility` (off) |
| **1. General** | `Enabled` (on); `Overall strength` (1.0); `Diagnostic log` (on); `Steady occlusion` (on); `Fix game audio culling (all looping sources)` (on) |
| **2. 01 ... 15** (one per material group) | `Attenuation per meter`, `Base attenuation` – only used while Physics Mode is off |
| **3. Bot Hearing (SAIN)** | `Mode` (**Off** / QuieterOnly / Replace); `Strength` (1.0); floors per sound type; `Calculations per frame` (6) |
| **4. Doors and Windows** | `Enabled` (on); `Door leaf: use measured thickness` (on); `Fallback thickness: wood/plastic leaf (m)` (0.04); `Fallback thickness: glass pane (m)` (0.006); `Metal door: sheet thickness (mm)` (1.5); `Gate wings: count with the opening` (on). The old fixed values (`Door thin wood` …) are gone. |
| **5. Walls Between Rooms** | `Enabled` (on); `Max. distance (m)` (40); `Shell thickness (m)` (0.15); `Door/window leaf: room path only` (on) |
| **6. Timbre** | `Enabled` (on); `Strength` (1.0) – no effect while Physics Mode is on |
| **7. Weapon Directivity** | `Enabled` (on); `Rear side (dB)` (-4); `Rear low-pass (Hz)` (6000); `Indoor factor` (0.5); `Cover SEB legacy (V1) weapons` (on); `Occluded shots: no extra occluded mix (physics)` (on) |
| **8. Air Absorption** | `Enabled` (on); `Air clarity factor` (1.0); `Shot offset` (1.0); `Use raid weather (ISO 9613-1)` (on); `Base humidity (%)` (60) |
| **9. Reverb by Material** | `Enabled` (on); `Strength` (1.0); `Max zone boxes` (2000); `Reverb level follows reverb time` (on); `Room absorption from random chords` (on); `Diagnostics` (on). `Max zones`, `Only largest box per room` and `Min. absorption` (0.08) are no longer used. |
| **10. Advanced (rays)** | `Transmission rays x` (**4**; 1 = game value); `Reflection rays x` (**4**); `Max. bounces` (**6**; 0 = game value 3); `Energy kept per bounce` (0 = game value) – take effect from the next raid; more rays cost more CPU |
| **11. Sound log & live tuning** | `Sound log` (**off**); `Log radius (m)` (60); `Max. file size (MB)` (50); `Mark odd sound key` (unbound); `Live tuning` (**off**); `Save heard sounds for the demo` (**off**); map export for the demo sandbox |
| **12. Forest** | `Enabled` (on); `Strength` (1.0); `Density threshold (trees per 100 m2)` (3) |
| **13. Distance (SEB)** | `Near recording at every distance (physics does the rest)` (on); `Near recording: width shrinks with distance` (on); `Near recording: source width (m)` (2); `Near recording: distance through the loudness curve` (on). `Enabled` (**on**): `Max. distance outdoors (m)` (900, SEB 600), `Explosions: SPT curve` (on), `Suppressed: keep mid clip` (on), `Far clip compensation (dB)` (4) – its distance curve and far clip compensation only apply to other people's shots when the close recording is off |
| **14. Sound Paths (physics)** | `Diffraction and reflection in dB` (on); `Edge diffraction: wedge angle (deg)` (90); `Octave bands for the low-pass` (on); `Band low-pass strength` (**0.6**; 1 = physical, lower = brighter); `Low-pass fit for the 24 dB/oct game filter` (on); `Energy sum of all paths` (on); `Receiving room` (on); `Flanking transmission` (on); `Path blend time (ms)` (300) |
| **15. Ambient emitters** | `Enabled` (on); `Range factor` (4); `Max. original range (m)` (15); `Volume trim (dB)` (0); `Indoor room tone volume (%)` (**30**) |
| **16. Loud signals** | `Enabled` (on); `Range (m)` (300); `Range factor` (15); `Name keywords` |
| **17. All fixed sources** | `Enabled` (on); ranges for small/medium sources and vehicles; `Calibrate source levels` (on); `Lamps: real hum level (direct sound + room field)` (on); `Lamps: hum level at 1 m (dB(A))` (45); `Wall damping for non-occluded sources` (on) with its budget, detour and `Wall damping: air absorption (ISO 9613-1)` (on), `Wall damping: ground under the path is no wall` (on), `Wall damping: real audibility` (on) |
| **18. Scripted sources** | Trains, airdrop, BTR turret/horn, flares, mortar whistle, doors – all on, ranges as in 0.8.5 |
| **19. Path search (experimental)** | `Precomputed obstacle paths` (on – uses the map data included in the download); `Precomputed paths: line-of-sight check` (on); `Precomputed paths: max. rise / max. fall (dB/s)` (60 / 15); `Path search (experimental)` (**off** – needs a path network file per map that is not shipped) |
| **20. Test source (measurement)** | Keys to place and play a test sound (unbound); `Signal` (Click / PinkNoise / GameClip / Gunshot); `Repeat`; `Reference tick` |
| **21. Gunshot spatialization (experimental)** | `Gunshots: HRTF (experimental)` (**off**) |
| **22. Meta occlusion (experimental)** | `Override Meta occlusion intensity` (**off**) – for measurements only |

Notes:

- **Physics Mode is on by default**, so the material table (`2.xx`) and Timbre are inactive until you switch it off.
- **Too quiet or too loud behind walls?** The curve is set by `Compression knee (dB(A))` and `Compression above knee`. A higher knee or a steeper slope makes loud sounds behind walls quieter (a knee of 95 made shots behind gates too quiet in testing). `Loudness factor` below 1 squeezes everything on top of that (old behaviour).
- Only these need a game restart: `3. Bot Hearing (SAIN) / Mode` and the four `10. Advanced (rays)` settings (they take effect from the next raid).

## Sound log and live tuning

Two developer tools for tuning and bug reports, both **off by default** and both in F12 section `11. Sound log & live tuning`. With them off they cost one boolean check.

### Sound log

Switch on `Sound log`, play, and each processed sound within `Log radius (m)` (60) of you becomes one line in `BepInEx/Materialklang-Logs/<date_time>-<map>.log` (a new file per raid; the last `Keep last raids` (10) files are kept; logs outside a raid are named `…-outside-raid.log`). At most `Max. lines per source per second` (4) per sound source. Writing happens on a background thread.

A line looks like this (one line, wrapped and shortened here):

```
14:32:05.123 t=412.35 f=24510 | Gunshot(Gunshots) clip=ak74_shot_03 src=SuperSource/BetterSource_12 owner=Bot_Pmc_1 @Bots
 | d=23.4 m | path=through walls [T=0.31 D=0.55 R=0.62 effect=0.62]
 | hits: ConcreteStone 0.28 m 41.2 dB x7 "Wall_A12"; WoodThick 0.04 m 8.1 dB x2 (free rays 3/12)
 | vol=0.870 (-1.2 dB) lp=4210 Hz hp=10 Hz | mode=physics ...
 | rooms src=Hall_A ear=Kitchen | ... | ids src#… occ#… calc#…
```

- `d` distance to you, `path` the chosen path (`direct`, `through walls`, `via doors/windows (rooms)`, `through wall between rooms`), `hits` the pieces the rays went through (material, thickness, dB, number of rays, collider name), `vol`/`lp`/`hp` the final volume and filters.
- Since 0.8.8 lines carry many more fields (direction from the opening, extra voices, diffuse part, lamp level, `near re 1st blend limit` for the close recording …). The `ids` at the end tell sources of the same name apart – group by them when you compare lines.
- Fixed sources have their own lines (`[Walls]`, `[Fixed]`, `[Vehicle]`, `[Scripted]`, `[Ambient]`), the precomputed paths `[PathTable]`.
- Settings in effect are written at the top of each file, and every live change as a `[Live]` line.

**Mark odd sound:** bind `Mark odd sound key` and press it when something sounds wrong; a `=== MARK n ===` line with time and your position goes into the log. A short ding confirms the mark.

### Live tuning

Switch on `Live tuning`. The plugin creates `BepInEx/config/kaan.materialklang.live.json` and checks it once per second. It holds overrides with the exact F12 names:

```json
{
  "0. Physics Mode (real values)/Compression knee (dB(A))": 90,
  "5. Walls Between Rooms/Enabled": false
}
```

- Only values that changed since the last read are applied; `"reload": true` applies everything again. Keys starting with `_` are comments.
- Each applied value is logged (`[Live] … -> …`). Values outside the allowed range are limited; unknown names and wrong types are reported once and ignored.
- Need a restart: `3. Bot Hearing (SAIN)/Mode`; the `10. Advanced (rays)` values take effect from the next raid. The one-shot trigger `Export map for sandbox` is ignored in the file.

## Physics and sources

What True Acoustics calculates, what each part is based on, and where a value is simply game data or chosen by ear. Numbers in square brackets point to the reference list at the end of this section.

### 1. Wall insulation: the mass law (Physics Mode)

A wall stops sound mainly by being heavy: the sound has to shake it, and a heavier wall moves less. The textbook rule for this is the *mass law*. For sound arriving from all directions, as in a real room (*field incidence*), it is [1, 2]:

```
TL = 20 · log10(m · f) − 47 dB        m = density × thickness (kg/m²), f = 250 Hz / 1 kHz / 4 kHz
TL is clamped to 0 ... 60 dB ("Max. insulation")
```

- **TL** (transmission loss) = how many decibels the wall takes away. The rule rises by 6 dB per doubling of mass or frequency, so walls take more highs than lows.
- **The 60 dB cap** stands for *flanking*: in a real building sound also leaks around a wall (ceilings, floors, pipes) [3]. The value 60 dB itself is an assumption.
- **Hollow objects:** if the game reports a piece thicker than the material's `Solid up to` value, it counts as two shells of that thickness (simplification).
- **Coincidence:** above the *coincidence frequency* `fc` of a panel, real walls insulate less than the mass law says [1, 2]. This is corrected for concrete/stone, thick metal, thick wood, glass and door leaves with the three-region shape of **Sharp's prediction method** [12, 13]; the loss factor 0.1 is fitted to typical measured curves, **not a literature value**.
- **Several walls in a row** (since 0.8.8): walls separated by air count one by one, each with the room behind it (EN ISO 12354-1 [3]); a glass pane and a double shell count as such, and the sound field of the source room drives the wall (a small hard room pushes more through its walls than a large soft one).

Examples with the default densities (single wall, 250 Hz, before the loudness curve):

| Piece | Surface mass | TL at 250 Hz |
|---|---|---|
| Thin wood, 2 cm | 10 kg/m² | 21 dB |
| Thin metal, 2 mm | 16 kg/m² | 25 dB |
| Thick wood, 10 cm | 60 kg/m² | 37 → 27 dB (coincidence correction) |
| Concrete, 10 cm | 230 kg/m² | 48 → 37 dB (coincidence correction) |
| Concrete, 20 cm | 460 kg/m² | 54 → 46 dB (coincidence correction) |
| Concrete, 1 m | 2300 kg/m² | 68 → 67, capped 60 dB |

### 2. Default densities

| # | Group | Density (kg/m³) | `Solid up to` (m) | Basis |
|---|---|---|---|---|
| 01 | Concrete/Stone | 2300 | 1.0 | Plain normal-weight concrete; EN 1991-1-1 gives 24 kN/m³ [4] |
| 02 | Soil/Terrain | 1700 | 3.0 | Typical value for soil (roughly 1500–2000) |
| 03 | Gravel/Pebbles | 1600 | 3.0 | Typical value for loose gravel |
| 04 | Metal thick | 7850 | 0.01 | Structural steel, EN 1993-1-1 [5] |
| 05 | Metal thin | 7850 | 0.002 | Steel, as above (sheet 2 mm since 0.8.8) |
| 06 | Wood thick | 600 | 0.3 | Typical softwood (roughly 400–700) |
| 07 | Wood thin | 500 | 0.05 | Typical softwood / plywood |
| 08 | Glass | 2500 | 0.02 | Soda-lime glass, EN 572-1 [6] |
| 09 | Soft (cardboard, fabric, plastic, rubber) | 500 | 0.1 | Mixed value, chosen by ear |
| 10 | Grate/Fence/Grass | 0 | 0 | Treated as open |
| 11 | Body/Armor | 1000 | 0.5 | About the density of water / body tissue |
| 12 | Water | 1000 | 3.0 | Water |
| 13 | Unknown | 2000 | 0.5 | Assumption (something heavy in between) |
| 14 | Door leaf | 600 | 0.06 | Like thick wood; assumption |
| 15 | Snow | 300 | 3.0 | Middle of the range for settled snow [14] |

The `Solid up to` thicknesses are assumptions about the game's geometry, not literature values.

### 3. From decibels to what you hear

- **Loudness curve (since 0.8.8).** `volume factor = 10^(−dB_played / 20)`, the standard conversion from a level difference to a sound-pressure factor [2]. With `Loudness factor` 1 every dB of insulation is played as long as the real level at your ear (without walls) stays below the knee (85 dB(A)): footsteps, voices and cars follow real physics. Above the knee only 0.2 dB per real dB is played (soft knee, 10 dB wide). **Why:** a real gunshot is about 140 dB(A) at 1 m, the game's own close gunshot clips play like 93–96 dB(A) – the game mix already squeezes loud sounds by about this slope. The knee value 85 is chosen so the game's level range up to about 80 dB(A) is played 1:1; it was tested against 95 by ear. Example: a closed door (23 dB) takes about 9 dB off a rifle shot 80 m away, 60 dB of concrete still about 46 dB.
- **Explosions** use the slope of the game's own explosion distance curve instead (about 0.31).
- **Distance of the close recording:** beyond the first blend distance of the weapon's sound bank (5 m), the drop over distance goes through the same curve, from a real level of 140 dB(A) at 1 m (suppressed 120 dB(A)). Unsuppressed rifle relative to 5 m: 55 m −4 dB, 400 m −8 dB (plain 1/r: −21 / −38 dB). These real levels are typical values, not measurements of particular weapons.
- **Muffling (low-pass):** the band values (250 Hz / 1 kHz / 4 kHz) are turned into the one low-pass the game has per sound. Since 0.8.8 the fit assumes the game's real filter (measured: 24 dB per octave, Q 1). For loud sounds the muffling is squeezed by the same factor as the volume. The mapping is my own approximation, not a formula from the literature.
- **All paths add up as energy:** through the wall, around edges (UTD diffraction, ported from Valve's Steam Audio, see `THIRD_PARTY_NOTICES.md`), over reflections and through the rooms – `10 lg Σ 10^(L/10)`, never above a clear line.

### 4. Doors and windows

Since 0.8.8 a closed door or intact window is a **leaf of its own material and thickness** (the game's ballistic material of the leaf; thickness measured on its collider, panes from the game's own pane model), calculated with the same mass law and coincidence correction as walls, per band. Metal doors thicker than their group's `Solid up to` count as hollow doors of two 1.5 mm sheets. A **gap** around the leaf is a parallel opening: 0.5 % of the area for doors (an unsealed door then reaches at most about 23 dB; measured unsealed interior doors: R'w 15–20 dB), 0.1 % for windows (at most about 30 dB). The gap shares are assumptions fitted to typical measured values. Examples at 250 Hz / 1 kHz / 4 kHz (leaf only): wooden door 4 cm 21 / 31 / 49 dB, hollow steel door 28 / 40 / 52 dB, glass 6 mm 25 / 37 / 40 dB. Partly open doors and broken windows let the open part through.

### 5. Walls between rooms and bot hearing

- **Walls between rooms** use the same mass law for each wall piece on the straight line (five lines, rays in both directions). Single surfaces without a back face get a typical thickness of their group (metal sheet 2 mm, glass 6 mm, thin wood 12 mm, solid groups 0.15 m `Shell thickness`) – assumptions about typical building parts. A door leaf that is already an opening of the room path is not counted a second time.
- **Bot hearing (SAIN)** turns the energy that passes the walls into a hearing-range factor with the exponent 0.5 (spherical spreading [2]). The floors per sound type are game-balance values, chosen by hand.

### 6. Air absorption

Air swallows high frequencies over distance; the international calculation method is ISO 9613-1 [7]. True Acoustics uses it with the raid's temperature and pressure; humidity is **estimated** (rain 95–100 %, fog 80–100 %, otherwise `Base humidity (%)` plus up to 10 points with clouds – my own assumptions). The cut-off is the frequency at which the air absorbs 3 dB over the distance (a choice, not part of the standard), times the `Air clarity factor`. With the close-recording mode, air absorption counts over the whole distance of a shot (fitted to the game's 4-pole filter); otherwise the part already baked into SEB's far recordings is subtracted (`Shot offset`).

### 7. Weapon directivity

The muzzle blast of a firearm is directional [8]. True Acoustics uses `w = (1 − cos angle) / 2`, level `Rear side (−4 dB) × w^1.3 × indoor factor`, low-pass `22 kHz × (6 kHz / 22 kHz)^(w² × indoor factor)`. **Shape and numbers are tuned by ear**; the reference only supports *that* gunshots are directional.

### 8. Timbre (Physics Mode off)

The low/high weights per material that tilt the filter are **chosen by ear**. In Physics Mode this part is off, because the band calculation already carries the tilt.

### 9. Rooms and reverb

- **Room shape:** a room is the union of the game's room boxes; each part gets its own Meta XR control zone, so a zone no longer reaches into a neighbouring room.
- **Absorption:** the game's baked room data treat every surface as α = 0.1. True Acoustics measures the wall materials and, since 0.8.8, the **mean free path through the room with random lines** (`4V/S`, Kuttruff [10]), so machines, crates, pipes and walkways count with their own surface. Open portals count with their real area (α = 1). α per material and band are **game data** (an unused material table that ships with EFT, 26 materials).
- **Reverb time** by Eyring's formula with the air term (ISO 354 [17], Eyring [11]); the **difference** to the game's baked value is sent to Meta as a control zone, the reverb level follows the reverb time (`+10·lg(T_new/T_base)`).
- **Sound from other rooms:** the energy that leaks from the source room through openings or walls is split into a directed part (straight or around the edges of the openings) and a diffuse part (the source room's reverberant field, Kuttruff/Barron [10]). The game's reverb plays the diffuse part 30–50 dB too quietly (measured on Factory), so it is played with the dry sound. For short sounds only the part that arrives within 125 ms (LAFmax), for voices only the part within 50 ms (D50, ISO 3382-1 [18]) counts. These two limits describe how a listener hears a room, not a property of the room; they are the current solution, not the final one.

### 10. Game values and ray settings

- Ray counts (12 transmission / 24 reflection rays), max. 3 bounces and 70 % energy kept per bounce are the **game's** values; the `Advanced` multipliers only scale them.
- The material table in sections `2.xx` is **hand-tuned**, not physics; it is only used while Physics Mode is off.

### 11. Forest (foliage attenuation)

ISO 9613-2:1996, annex A [15]: for a path through foliage of length `d_f` the attenuation is `D_f × d_f` per octave band (10–20 m: 0–3 dB in total; 20–200 m: 0.02–0.12 dB per metre). **I could not open the standard itself (paid)**; the values were checked against the CadnaA documentation [16]. Trees are the map's own tree objects, counted at raid start in a 10 × 10 m grid; a cell counts fully as dense foliage from 3 weighted trees per 100 m² (assumption). Level from the 500 Hz band, the rest of the slope as a low-pass.

### 12. Precomputed obstacle paths

**The map data is included in the download** (since 0.8.9). How it works when a table is present: inside buildings, when source and listener are in the same room and an obstacle blocks the line without the game finding a way around, the level per band and the direction come from a table built in advance for each map (probe points about every 3 m; per pair of probes the two best ways around the edges, UTD diffraction; looking up a value takes about 1 µs). Before a value is used, the mod checks which probes it can really see from the source and from you (a few line checks per frame). The value is never louder than a clear line and never quieter than the screening limit of ISO 9613-2 (at most 20 dB for one edge, 25 dB for several) [15]. Without a table (map data not loaded, outdoors, or a place without a value) the mod uses that screening limit directly. Tables are checked against the game files by name, size and a content digest; a table made for other game files, or in an older table format, is rejected with the reason in the log. The idea of precomputing paths between probe points follows published game-audio systems (e.g. Valve's Steam Audio).

### References

1. L. L. Beranek, I. L. Vér (eds.): *Noise and Vibration Control Engineering.* Wiley 1992; 2nd ed. 2006.
2. D. A. Bies, C. H. Hansen, C. Q. Howard: *Engineering Noise Control*, 5th ed. CRC Press 2017.
3. EN ISO 12354-1:2017 – *Building acoustics – Estimation of acoustic performance of buildings from the performance of elements – Part 1: Airborne sound insulation between rooms.*
4. EN 1991-1-1:2002 – *Eurocode 1: Actions on structures – Densities, self-weight, imposed loads for buildings*, Annex A.
5. EN 1993-1-1:2005 – *Eurocode 3: Design of steel structures*, 3.2.6.
6. EN 572-1 – *Glass in building – Basic soda lime silicate glass products – Part 1.*
7. ISO 9613-1:1993 – *Acoustics – Attenuation of sound during propagation outdoors – Part 1: Calculation of the absorption of sound by the atmosphere.*
8. S. D. Beck, H. Nakasone, K. W. Marr: "Variations in recorded acoustic gunshot waveforms generated by small firearms." *J. Acoust. Soc. Am.* 129(4), 1748–1759 (2011).
9. W. C. Sabine: *Collected Papers on Acoustics.* Harvard University Press 1922.
10. H. Kuttruff: *Room Acoustics*, 6th ed. CRC Press 2017.
11. C. F. Eyring: "Reverberation time in 'dead' rooms." *J. Acoust. Soc. Am.* 1(2A), 217–241 (1930).
12. B. H. Sharp: "Prediction methods for the sound transmission of building elements." *Noise Control Engineering* 11(2), 53–63 (1978). (Bibliographic data checked; the article itself was not available to me.)
13. "Acoustic properties of glubam and SPF", arXiv:2212.04173 – applies Sharp's model around the coincidence frequency.
14. Typical snow densities from snow science and avalanche handbooks; no single table is cited.
15. ISO 9613-2:1996 – *Acoustics – Attenuation of sound during propagation outdoors – Part 2: General method of calculation* (screening, housing annex A.3, foliage annex A.2).
16. Datakustik, CadnaA documentation, "Built-Up Areas and Foliage" – reproduces the foliage table of ISO 9613-2.
17. ISO 354:2003 – *Acoustics – Measurement of sound absorption in a reverberation room.*
18. ISO 3382-1:2009 – *Acoustics – Measurement of room acoustic parameters – Part 1: Performance spaces* (definition D50).

## Credits

- **[Sound Engine Backport](https://github.com/RedTheFoxx/SPT-SEB) by RedTheFoxx** – the live sound engine for SPT this is an add-on for. True Acoustics only hooks into it at run time (Harmony); no SEB code is included. Thanks for a mod to build on.
- **SAIN** – the bot AI whose hearing check the optional bot mode adjusts.
- **Meta XR Audio** (Meta Platforms) and **Battlestate Games' spatial audio system** – the ray-based occlusion and the room reverb that this mod feeds with materials; the absorption values per material come from the unused material table that ships in the game.
- **Valve's Steam Audio** (Apache 2.0) – the UTD edge diffraction is ported from it (see `THIRD_PARTY_NOTICES.md`).
- The physics are textbook acoustics – see [Physics and sources](#physics-and-sources) for the references.
- **BepInEx** and **Harmony** for making all of this possible.

## Changelog

See [CHANGELOG.md](https://github.com/Kaanori/spt-true-acoustics/blob/main/CHANGELOG.md).

## Support

Bugs and questions: [GitHub issues](https://github.com/Kaanori/spt-true-acoustics/issues).

## License

MIT — see [LICENSE](https://github.com/Kaanori/spt-true-acoustics/blob/main/LICENSE).

## Building from source

Needs the .NET 10 SDK and an SPT 4.1.6 installation to build against.

```bash
./build.sh             # Linux: build only
./build.sh --install   # build and copy into the game folder
```
```powershell
.\build.ps1            # Windows
.\build.ps1 -Install
```

The game folder is set in `Directory.Build.props` (Windows default `C:/SPT`)
or via the environment variable `SPT_DIR`.

## Support

Bugs and questions: [GitHub issues](https://github.com/Kaanori/spt-true-acoustics/issues).

## License

MIT — see [LICENSE](https://github.com/Kaanori/spt-true-acoustics/blob/main/LICENSE).
