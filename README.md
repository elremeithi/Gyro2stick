# DS Gyro 360

Makes DualSense / DualSense Edge / DualShock 4 controllers (USB or Bluetooth) show up as
Xbox 360 controllers, with the gyro added to the right stick.

## Use
Copy `publish\DSGyro360.exe` to any 64-bit Windows 10/11 PC and run it (it asks for admin).
No .NET install and no internet needed. On first run it offers to install the bundled drivers:
- **ViGEmBus 1.22.0** (required): creates the virtual Xbox 360 pads.
- **HidHide 1.5.230** (recommended): hides the real pad from games so they don't see double input. Restart Windows after installing it.

## Controls
- **ON/OFF**: master switch. OFF removes the virtual pads and unhides the real controllers.
- **Gyro**: enable, activation (Hold L2 by default; R2 / L1 / R1 / finger on touchpad / always),
  sensitivity (1.0x = 100°/s of turning gives full stick), deadzone (°/s), anti-deadzone, invert X/Y.
- **Anti-deadzone** (gyro, left stick, right stick; ROG Ally style): output starts at this value as soon as
  you move, so the game's own stick deadzone doesn't swallow small movements. Usually 10-25%.
- **Recalibrate gyro**: put the controllers down, click, keep them still for 1 second (also runs automatically when a controller connects).
- **Xbox slot**: the XInput player number (1-4) each controller got.
- Rumble passthrough, stick deadzone, start minimized, close to tray.
- **Shortcuts**: PS + a button presses a key combination, a mouse button or a gamepad button, or opens a program (the game doesn't see the combo). Click the binding to pick it Steam-style from a keyboard, mouse or gamepad drawn on screen. Mouse and gamepad buttons stay pressed while you hold the combo button. Ready-made actions include Alt+Tab, Alt+F4, Game Bar and NVIDIA screenshot / record, and Alt+X / S / W / Q.
- **Start with Windows**: tick it and click Apply. On any PC this creates a logon task named `DSGyro360` that starts the app in the tray with admin rights and no UAC prompt. The app re-points the task to wherever the exe is each time it starts, so moving or updating the exe keeps working. If the task can't be created, a message says why.

## Macros (button combos, like reWASD)
The **Macros** tab binds a controller button combo (1–3 buttons, e.g. L1 + Square) to a sequence of steps. **+ New macro…** offers ready-made examples to start from: turbo (rapid fire), press a button twice, toggle auto-run, toggle hold, a combo move, a keyboard shortcut, or a blank macro. Under the chart, a sentence says what the selected macro does. You edit it as a flow chart:
- **Trigger block**: the combo, plus the activator (*On press*, *Long press* with a hold time, *Double press* with a time window, *On release*), and whether the game sees the combo buttons.
  - Pressed alone, a combo's buttons still reach the game. Once the whole combo is held, they're hidden.
  - With a long or double press on a single button, that button is held back until it's clear which one you meant. A short press is then passed through.
- **Step blocks**, from the palette:
  - *Press / Hold / Release* a controller button (Xbox names, with the PlayStation name in brackets).
  - *Key*, *Key down*, *Key up* (any key or combination such as Ctrl+R).
  - *Mouse click*.
  - *Wait*.
  - *Move stick*: left or right stick, X/Y -100..100, held for a set time.
  - *Rumble*.
  - Click a block to edit it on the right. Drag a block, or use Move up / Move down, to reorder it. Delete removes the selected block.
- **End block**: *Once* (or N times), *While the combo is held*, or *Toggle* (runs until the combo is pressed again), with an optional pause between runs. A loop arrow shows when the macro repeats.

Macros belong to the profile in use, so they're exported with profiles. Click **Apply** to make changes live. Many games miss presses shorter than about 20 ms.

## Remap (games without controller support)
The **Remap** tab turns the controller into keyboard and mouse for games that only support those. **Off** keeps it an Xbox controller. A game's layout:
- The left stick presses the movement keys. Pushing it gently or all the way also holds the game's sneak or run key.
- The right stick and the gyro move the mouse. The gyro follows the Settings page activation (Hold L2 by default), or tick *Gyro aims all the time*.
- The buttons press the game's default keys. Some buttons do one thing on a tap and another on a hold. With Menu / Options held, the D-pad and face buttons give a second set of actions (quick save, quick load, status screens).
- With *Only while the game is the window in front* ticked (the default), the layout only applies while the game is in front. Anywhere else the controller works as usual. While it applies, the virtual Xbox pad stays idle, so the game doesn't see two inputs.
- Keys are sent with their hardware scan codes, which older DirectInput games need.

Layouts, built from each game's manual (default key configuration chart):

| Control | No One Lives Forever (2000) | No One Lives Forever 2 (2002) |
|---|---|---|
| Left stick | Move (W A S D); full push runs (Enter) | Move (W A S D); gentle push sneaks (Left Shift) |
| Right stick / gyro | Look (mouse) | Look (mouse) |
| RT / R2 | Fire / use gadget (left mouse) | Fire / use gadget (left mouse) |
| LT / L2 | Zoom in (wheel up) | Zoom (Z) |
| A / Cross | Jump (Space) | Jump (Space) |
| B / Circle | Crouch, toggled (C) | Toggle crouch (Left Ctrl) |
| X / Square | Reload (middle mouse) | Reload (R) |
| Y / Triangle | Use / activate (right mouse) | Tap: action, use / search (Enter). Hold: pick up / drop body (G) |
| RB / R1, LB / L1 | Next / previous weapon or gadget (Left Shift / ]) | Next / previous weapon or gadget ([ / ]) |
| D-pad | Up: next ammo / gadget mode (Left Ctrl). Down: holster (H). Left: torch (L). Right: inventory (I) | Up: change function (F). Down: holster (H). Left / right: lean (Q / E, while held) |
| L3 / R3 | Always run (Caps Lock) / zoom out (wheel down) | Keychain light (L) / last weapon (X) |
| View / Create | Mission objectives (O, while held) | Mission status (Tab) |
| Menu / Options | Menu (Esc) | Menu (Esc) |
| Menu + D-pad | Up quick save (F6), down quick load (F9), left status (Tab), right turn around | Up quick save (F6), down quick load (F9), left player status / skills (P), right intelligence (`) |
| Menu + X / Y | Crosshair (X) / centre view (End) | Centre view (End) / turn around (Backspace) |

Keep the game's default keys (Restore defaults in its Controls menu). In NOLF 1, turn Always Run off so a gentle push walks quietly. In NOLF 2, leave Always Run on (the default).

**Battlefield: Bad Company 2 (2010)** has no controller support on PC. This layout follows the game's own Xbox 360 / PS3 layout, using the PC default keys:

| Control | Action (key) |
|---|---|
| Left stick / right stick + gyro | Move or drive (W A S D) / look and aim (mouse) |
| RT / LT | Fire (left mouse) / aim and scope, vehicles' secondary fire (right mouse) |
| A / B / X / Y | Jump (Space) / interact, enter and exit vehicles (E) / reload, UAV climb (R) / switch weapon (wheel) |
| LB / RB | Grenade, UAV descend (F) / knife (middle mouse) |
| L3 / R3 | Sprint (Shift; click, stops when you stop moving) / crouch (Ctrl, toggle) |
| D-pad | Left / right: gadgets, vehicle seats 3 / 4 (3 / 4). Up: vehicle camera (C). Down: map (M) |
| View / Create | Tap: spot enemy (Q). Hold: scoreboard (Tab) |
| Menu / Options | Menu (Esc). Menu + D-pad left / right: sidearm or seat 1 / primary or seat 2 (1 / 2). Menu + up: binoculars or seat 5. Menu + A: respawn / squad menu (Enter) |

## Profiles (per game)
Pick a **Profile** next to the tabs, or from the tray menu. Each profile has its own copy of the gyro, controller, shortcut, macro and Performance-page settings. Changes you Apply (and Performance changes) are saved into the profile in use. App settings (start with Windows, tray) and built-in controller setup are shared by all profiles.
- **Profiles…** opens the manager: *New* (copy of the settings in use), *Duplicate*, *Rename*, *Delete*. *Default* is always there.
- **Games**: add a game to a profile with *Add running…* (pick a game window that's open) or *Browse .exe…*. With *Switch profiles automatically* ticked, the profile turns on while that game is the window in front. When you leave the game, the app goes back to the profile you picked yourself. A small notification shows each switch. Auto-switching pauses while you have unapplied changes.
- **Export all…** saves every profile, with its games and options, to a `.json` file, as a backup or to move to another PC. **Import / restore…** loads such a file. *Yes* replaces all your profiles (restore a backup). *No* adds them, overwriting profiles that have the same name.

## Handheld PCs (GPD Win 4)
The built-in controller and its gyro work too:
- **Built-in controller: Auto** (the default) turns this on for GPD, AYANEO and ONEXPLAYER devices; **On** forces it on any PC.
- The built-in Xbox-style pad is read through XInput and hidden from games with HidHide. A virtual Xbox 360 pad takes its place, with the gyro added to the right stick. Buttons, sticks, triggers and rumble pass through, and Guide works as PS for the shortcuts.
- The gyro is read from the Windows motion sensor. On the GPD Win 4 this needs GPD's **BMI260 sensor driver**, from GPD's driver pack. The status line under the drivers shows whether a sensor was found. To bundle that driver, put its files (`.inf`, `.sys`, `.cat`) in `Drivers\GpdSensor\` and rebuild. The app then installs it at first run on a GPD with no gyro sensor. It isn't bundled at the moment.
- **Built-in gyro axes / Detect axes…**: if aiming goes the wrong way or on the wrong axis, click Detect axes. Turn the device left, then tilt it to look up, then click Use and Apply.
- In the GPD's mouse mode the controller disappears, and the app waits until you switch back to gamepad mode.

## Performance page (GPD Win 4 6800U)
Quick profiles, also in the tray menu under **Performance**. They apply instantly and are restored at startup:

| Profile | Power limit (default, adjustable 5-30 W) | Frame cap | Fan curve |
|---|---|---|---|
| Off | not changed | none | GPD automatic |
| 30 FPS | 12 W | 30 | quiet |
| 45 FPS | 18 W | 45 | balanced |
| 60 FPS | 28 W | 60 | performance |

- **Power limit**: sets the processor's sustained and boost limits through its power controller (SMU), the same way RyzenAdj does. It works on Ryzen 6000 mobile chips and is re-applied every 10 s, because firmware can reset it.
- **Frame cap**: uses RivaTuner Statistics Server's global limit, and the app starts RTSS if needed. RTSS can't be bundled. On an offline PC, copy its installer (`RTSSSetup….exe`, or the `.zip` from Guru3D) next to `DSGyro360.exe`. The first-run setup then offers to install it, or use *Install RivaTuner…* on the Performance page. The cap is removed when you pick Off or exit the app. Only a cap set by this app is ever removed.
- **Fan**: *Curve* follows the CPU temperature, using the curve of the selected profile. *Automatic* hands control back to GPD's firmware. *Fixed* holds a set speed. Above 95 °C the fan always runs at full speed, and it returns to automatic when the app exits.
- **Driver**: this page needs **PawnIO**, a signed hardware-access driver from pawnio.eu. It replaces WinRing0, which Windows Defender now blocks. It's bundled with the app: click *Install PawnIO driver…* on the page.

Settings: `%APPDATA%\DSGyro360\settings.json`.

## Build
```
dotnet publish -c Release -o publish
```
