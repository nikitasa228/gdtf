CANDELA — PREVIEW BUILD

Candela is a stage lighting visualiser and editor: GDTF fixtures, MVR scenes, physically based light (candela, lux),
volumetric beams in haze, DMX and power cabling, and live control from lighting consoles (Art-Net, sACN, OSC, PSN).

This is a preview for trying the editor. Things change between previews and some parts are unfinished (see
"Known limitations" in the documentation). Save your projects often; the editor also autosaves every 5 minutes.


SYSTEM REQUIREMENTS

  * Windows 10 or Windows 11, 64-bit.
  * A graphics card with a Vulkan 1.3 driver: NVIDIA GeForce GTX 900 series or newer, AMD Radeon RX 400 or newer,
    Intel UHD/Iris Xe (11th generation) or Arc. Install the latest driver from the card maker.
  * 8 GB of memory (16 GB recommended), about 400 MB of disk space for the program.


WHAT SETUP DOES

  * Installs Candela for all users (C:\Program Files\Candela, needs administrator rights) or only for you
    (%LOCALAPPDATA%\Programs\Candela) — you choose on the next page.
  * Installs the Microsoft Visual C++ 2015-2022 runtime (x64) when it is missing; the editor's UI libraries need it.
  * Adds Start-menu shortcuts (Candela, Candela Command Prompt, Read Me, Documentation) and, if you wish, a desktop
    shortcut.
  * Makes .candela projects open in Candela and adds "Open with Candela" for .mvr scenes (optional).
  * Adds a Windows Firewall rule so a lighting console can send Art-Net, sACN, OSC and PSN to Candela (optional,
    explained on its own page).
  * Installs a sample scene ("Фокина.mvr", a theatre rig with 91 fixtures) with the GDTF files it needs.


THE FIXTURE LIBRARY

Candela draws fixtures from their GDTF files. The full library (about 1,100 fixtures, 1.8 GB) is not part of Setup.
At the first start Candela asks for the folder with your .gdtf files, or creates "Documents\Candela\Fixture Library"
for them. GDTF files are published by the manufacturers on https://gdtf-share.com and on their own websites.


THIS BUILD IS NOT SIGNED

Windows SmartScreen may say "Windows protected your PC" when you start Setup. Click "More info", then "Run anyway".


WHERE YOUR FILES ARE

  * Settings and layouts: %APPDATA%\Candela
  * Caches, autosaves, logs and crash reports: %LOCALAPPDATA%\Candela
  * Your projects: wherever you save them. The uninstaller never removes projects or the fixture library.


PROBLEMS?

Please report them on https://github.com/nikitasa228/gdtf/issues and attach the log
(%LOCALAPPDATA%\Candela\logs\candela-app.log) or the crash report the editor showed.


THIRD-PARTY SOFTWARE

Candela uses open-source components (.NET, Silk.NET, Dear ImGui, GLFW, ImGuizmo, Hexa.NET, shaderc, Assimp,
SharpGLTF, StbImageSharp, the Inter and Lucide fonts) under their own licences; see THIRD-PARTY-NOTICES.txt in the
installation folder.
