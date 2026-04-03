# BTDSys PeerCtrl – ReBuzz Managed Machine Port

A faithful C# re-implementation of the classic **BTDSys PeerCtrl v1.5/1.6**
Buzz machine, written as a ReBuzz *managed machine*.

Original C++ source © 2002–2008 Ed Powley (BTDSys).  
This port carries the same BSD-style permissive licence.

---

## What is PeerCtrl?

PeerCtrl is a **general-purpose control machine** – it controls the parameters
of other machines in the session without producing any audio of its own.  
Classic use-cases include:

| Goal | How |
|------|-----|
| Drive several parameters at once from one slider | Add one assignment per target parameter on the same track |
| Smooth (glide) any parameter | Set the **Inertia** global parameter |
| Non-linear mapping | Draw a curve in the **Mapping** editor |
| MIDI CC → Buzz parameter | Assign a CC number per track |
| Endless rotary encoders (inc/dec) | Enable **Inc/Dec** mode on the MIDI panel |
| Tie tracks together | Enable **Slaved** on a track |

---

## Feature mapping vs. the original

| Original feature | Port status |
|---|---|
| Up to 255 tracks | 64 tracks (configurable via `MAX_TRACKS` constant) |
| Global Inertia parameter | ✅ identical behaviour |
| Per-track Slaved parameter | ✅ identical behaviour |
| Per-track Value parameter (0–65534) | ✅ identical behaviour |
| Multiple assignments per track | ✅ full support |
| Piecewise-linear mapping curve | ✅ interactive WPF editor |
| MIDI CC input (absolute) | ✅ |
| MIDI CC inc/dec (CC 96/97) | ✅ |
| MIDI inc/dec wrap mode | ✅ |
| MIDI feedback output | Stub – ReBuzz MIDI-out API hookup left as TODO |
| Ctrl Rate attribute (sub-tick updates) | Mapped to **Send Freq** attribute |
| Stop on Mute attribute | ✅ |
| Plugin interface (XY pad, Mixer GUI…) | Not ported – ReBuzz has native alternatives |
| bmx/bmxml state persistence | ✅ via `MachineState` / XML serialisation |
| ImportFinished (rename fix-up) | ✅ |

---

## Parameters

### Global
| Name | Range | Default | Description |
|------|-------|---------|-------------|
| Inertia | 0–1280 | 0 | Glide time in ticks/10. 0 = off; 10 = 1 tick; 1280 = 128 ticks |

### Track (per track)
| Name | Range | Default | Description |
|------|-------|---------|-------------|
| Slaved | 0/1 | 0 | When 1, this track mirrors the previous track's Value |
| Value | 0–65534 | 32767 | The 0–100% value sent through the mapping to the target parameter |

### Attributes
| Name | Range | Default | Description |
|------|-------|---------|-------------|
| MIDI Inc/Dec Amount | 0–65534 | 1024 | Step size for endless-encoder CC 96/97 messages |
| Send Freq | 0–64 | 2 | How often (in Work() calls) inertia is pushed out. 0 = tick only |
| Stop on mute | 0/1 | 0 | When 1, no control changes are sent while the machine is muted |

---

## Assignment Settings dialog

Open with **right-click → Assignment Settings…**

```
┌─ Track selector ─────────────────────────────────────────────────────┐
│  Track: [Track 1 ▾]                                                  │
├─ Assignment list ────────────────────────────────────────────────────┤
│  SynthX / Cutoff [tr 0]                                              │
│  SynthX / Resonance                                                  │
│  [Add]  [Delete]  [Clear]                                            │
├─ Target ─────────────────────────────────────────────────────────────┤
│  Machine:   [SynthX ▾]                                               │
│  Parameter: [Cutoff ▾]                                               │
│  Track:     [Track 1 ▾]  (or "All tracks")                           │
├─ MIDI CC ────────────────────────────────────────────────────────────┤
│  Controller: [74 ▾]   Channel: [1 ▾]                                 │
│  Last CC: —                                                          │
│  [x] Feedback   [x] Inc/Dec   [ ] Wrap                              │
├─ Mapping (non-linear curve) ─────────────────────────────────────────┤
│  ┌───────────────────────────────────────────────┐                  │
│  │  ╲                                            │ ← drag points    │
│  │   ╲                                           │   right-click    │
│  │    ╲                                          │   to remove      │
│  │     ╲                                         │   click empty    │
│  └───────────────────────────────────────────────┘   to add        │
│                                         [Reset to Linear]            │
├─ Parameter Info ─────────────────────────────────────────────────────┤
│  Name:  Cutoff                                                       │
│  Range: 0 – 127   Type: Byte   Group: Track                         │
└──────────────────────────────────────────────────────────────────────┘
                                          [Update Assignment]  [OK] [Cancel]
```

### Workflow

1. Select the **Track** whose `Value` parameter you want to use as a source.
2. Click **Add** to create a new (empty) assignment.
3. Choose the **Machine** and **Parameter** to control.
4. Choose **Track** on the target machine (or "All tracks").
5. (Optional) Configure a **MIDI CC** to drive this track from hardware.
6. (Optional) Draw a custom **Mapping** curve for non-linear response.
7. Click **Update Assignment**, then **OK**.

Repeat steps 2–7 to add more assignments to the same track (fan-out).

---

## Mapping curve editor

The mapping curve transforms the 0–100% track Value into the actual parameter
value sent to the target.  The curve is stored as a sorted list of control
points and evaluated as a piecewise-linear function.

- **Left-click** on empty canvas → adds a control point
- **Drag** an existing point → moves it
- **Right-click** on a point → removes it (endpoints are locked)
- **Reset to Linear** → restores the default straight-line mapping

The default straight-line maps `Value=0 → 100%` of the parameter range and
`Value=65534 → 0%`, identical to the original PeerCtrl default.

---

## Inertia

When **Inertia** is non-zero, every value change is interpolated over
`Inertia / 10` ticks.  The interpolation step is recalculated per `Work()`
call using the host's `SamplesPerTick`.

Setting **Inertia = 0** in a pattern immediately snaps all tracks to their
target values.

---

## MIDI inc/dec (endless encoder) mode

Enable **Inc/Dec** on a track's MIDI assignment.  The machine then listens for
CC 96 (increment) and CC 97 (decrement) messages on the configured channel.
The **controller** field selects which *value byte* is considered "this
encoder's" message (Doepfer Pocket Dial style).

The step size is controlled by the **MIDI Inc/Dec Amount** attribute.

With **Wrap** enabled, incrementing past 100% wraps to 0% and vice-versa.

---

## Building

### Prerequisites
- Visual Studio 2022 or `dotnet` CLI ≥ 9.0
- ReBuzz installed (for the `BuzzGUI.*` NuGet packages)

### Steps

```bash
# 1. Restore NuGet packages
dotnet restore

# 2. Build release
dotnet build -c Release

# The DLL is copied to %APPDATA%\ReBuzz\Gear\Generators\ automatically.
# Adjust <OutputPath> in PeerCtrl.csproj if your install is elsewhere.
```

If you are building inside the **ReBuzz solution**:

1. Add the `PeerCtrl` project to `ReBuzz.sln`.
2. Replace the `PackageReference` entries with:
   ```xml
   <ProjectReference Include="..\BuzzGUI.Interfaces\BuzzGUI.Interfaces.csproj"/>
   <ProjectReference Include="..\BuzzGUI.Common\BuzzGUI.Common.csproj"/>
   ```

### Output path

The default `OutputPath` in the `.csproj` is:

```
%APPDATA%\ReBuzz\Gear\Generators\
```

Change this to your actual ReBuzz gear folder if different.

---

## Extending

### Adding more assignments per track

Already supported.  Click **Add** multiple times on the same track.

### Custom non-linear curves

The `EnvData` class stores any number of `EnvPoint` objects.  You can
programmatically add curves from code:

```csharp
var a = new TrackAssignment { MachineName = "MySynth", ParamIndex = 2 };
a.Mapping.Points = new List<EnvPoint>
{
    new EnvPoint { X = 0,     Y = 65535 },
    new EnvPoint { X = 16384, Y = 60000 },  // gentle S-curve
    new EnvPoint { X = 49152, Y = 5000  },
    new EnvPoint { X = 65535, Y = 0     },
};
machine.GetTrack(0).Assignments.Add(a);
machine.ResolveAssignment(a);
```

### MIDI feedback

The stub is in `TrackAssignment.ApplyValue()`.  Hook into
`host.Machine.Graph.Buzz.MidiOut(device, midiMessage)` when ReBuzz exposes
that API, matching the original's `pCB->MidiOut(device, data)` call.

---

## File overview

| File | Purpose |
|------|---------|
| `PeerCtrl.cs` | Machine class, track state, data structures, inertia, MIDI |
| `SettingsWindow.xaml` | WPF layout for the Assignment Settings dialog |
| `SettingsWindow.xaml.cs` | Dialog code-behind + interactive `CurveCanvas` control |
| `PeerCtrl.csproj` | .NET 9 / WPF project targeting x64 |

---

## Licence

BSD 3-Clause (matching the original BTDSys source code).

```
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice,
  this list of conditions and the following disclaimer.
* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.
* Neither the name of BTDSys nor the names of its contributors may be used
  to endorse or promote products derived from this software without
  specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED.
```
