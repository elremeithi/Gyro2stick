# Gyro2stick in Playnite

| File | Playnite field | Size |
|---|---|---|
| `icon.png` | Icon | 256×256 |
| `cover.png` | Cover image | 600×900 (2:3) |
| `background.jpg` | Background image | 1920×1080 |
| `logo.png` | Logo (Extra Metadata Loader / fullscreen themes) | transparent |

Regenerate them with `..\make_playnite_media.ps1`.

## Add it
1. **Add Game → Manually…**
2. **General:** Name `Gyro2stick`. Optionally set Platform to `PC (Windows)` and Category to `Tools`.
3. **Media:** click the folder button next to Icon, Cover image and Background image, and pick the files above.
4. **Actions:** add a **File** action with the path to `E:\apps\claude\Gyro2stick\publish\Gyro2stick.exe`. Set the working directory to its folder.
5. **Logo (optional):** with the *Extra Metadata Loader* add-on, right-click the game, then **Extra Metadata → Logos → Select local logo…** and pick `logo.png`.

## Launching without a UAC prompt
The app asks for admin rights, so launching the exe from Playnite shows a UAC prompt each time. To avoid it:

1. In Gyro2stick, tick **Start with Windows** and click Apply. This creates the scheduled task `Gyro2stick`.
2. Change the Playnite action to a **File** action with the path `C:\Windows\System32\schtasks.exe` and the arguments `/Run /TN Gyro2stick`.

The task starts the app in the tray. Double-click the tray icon to open the window.

If the app is already running, launching it again shows a "Gyro2stick is already running" message.
