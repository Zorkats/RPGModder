# RPGModder

**A transactional mod manager for RPG Maker MV and MZ games.**

<img width="1277" height="718" alt="{90A49365-5EEB-4ACF-AA37-136548DD8857}" src="https://github.com/user-attachments/assets/20ea94fb-c1e7-48bd-8e26-8246455c8fc9" />


RPGModder is a standalone tool designed to solve conflicting files and corrupted game folders. It validates mod paths, preserves a vanilla snapshot, records deployments, and automatically rolls back a failed or interrupted deployment.

RPGModder currently uses transactional deployment rather than runtime filesystem virtualization. Mods are resolved and deployed into the game content directory under rollback protection.

## 🚀 Key Features

### Transactional deployment
RPGModder keeps a clean vanilla snapshot and a transaction journal for every deployment.
* **Validated manifests:** Mod IDs and file paths cannot escape their declared roots.
* **Automatic rollback:** Failed or interrupted deployments restore the previous live game state.
* **Deployment history:** Successful deployments and rollbacks are visible in the Activity workspace.

### Conflict analysis
The Conflicts workspace distinguishes ordinary file overwrites, structured JSON merges, and semantic JSON patches. Load order determines overwrite winners.

### 🌐 Nexus Mods Integration
Direct integration with the Nexus Mods ecosystem.
* **One-Click Install:** Supports `nxm://` protocol. Click "Download with Manager" on Nexus, and RPGModder handles the rest.
* **In-App Browsing:** View Latest, Trending, and Updated mods directly within the tool.

---

## The `mod.json`

The heart of RPGModder is the `mod.json` file. This small manifest file is what allows the "Safe-State" engine to work magic.

### Why does it exist?
Installing an RPG Maker mod means dragging files and **overwriting** your game data. This is destructive and almost permanent. If two mods wanted to change the screen resolution in `System.json`, the second one would overwrite the first, breaking features.

`mod.json` solves this by telling the manager **what** to change, rather than doing it blindly. instead of overwriting files, it provides:
* **Smart Patches:** Instead of replacing `System.json` entirely, it says "Change `screen_width` to 1280". This allows multiple mods to edit game settings simultaneously without conflict.
* **Clean Installs:** It tracks exactly which files belong to which mod, so they can be removed instantly without leaving "trash" behind in your game folder.

### Why Modders need to update
To support non-destructive modding, existing mods simply need to include this one file (`mod.json`) in their ZIP archive.
* **No Code Changes:** You do not need to rewrite your plugins or change your assets.
* **Distribution:** You just add `mod.json` to your folder, zip it up, and upload it.
* **Compatibility:** This ensures your mod plays nicely with others and doesn't break the user's game.

---

## Creator Tools: The Auto-Packer

I know writing JSON manually is tedious. **That is why the Auto-Packer exists.**

You do not need to learn the `mod.json` syntax. RPGModder includes a powerful "Diff Tool" that writes the file for you.

### How it works:
1.  **Select Work Folder:** Choose the folder where you are making your mod (or your modified project).
2.  **Select Vanilla Folder:** Choose a clean, unmodified copy of the game.
3.  **Analyze:** The Auto-Packer scans both folders and finds every difference.
    * It detects new images/audio and marks them for copy.
    * It detects changes to `System.json` and automatically calculates the specific patch values.
    * It finds new plugins you added.
4.  **Generate:** Click one button, and it spits out a ready-to-use `mod.json`.

**Ideally, creating a compatible mod takes less than 30 seconds.**

---

## 📥 Installation

Download the appropriate self-contained package from the [Releases Page](https://github.com/Zorkats/RPGModder/releases).

### Windows x64

1. Extract `RPGModder-<version>-win-x64.zip` anywhere.
2. Run `RPGModder.exe`.
3. Optionally register `nxm://` from **Settings** for Nexus one-click downloads.

### Linux x64

1. Install the desktop integration dependencies for your distribution:
   - Arch/CachyOS: `sudo pacman -S libsecret xdg-utils`
   - Debian/Ubuntu: `sudo apt install libsecret-tools xdg-utils`
   - Fedora: `sudo dnf install libsecret xdg-utils`
2. Extract `RPGModder-<version>-linux-x64.tar.gz`.
3. Run `packaging/linux/install.sh` for a per-user installation, or launch `./RPGModder` directly for portable use.
4. Register `nxm://` from **Settings** if the installer did not register it automatically.

Linux API keys are stored through the desktop Secret Service using `secret-tool`; RPGModder refuses to persist them as plaintext when secure storage is unavailable. Steam and Flatpak Steam libraries are detected, and Windows RPG Maker releases are launched through Steam/Proton when an AppID is available.

### Linux development and handheld testing

Cross-publish locally before deploying to the configured Ally test machine:

```powershell
dotnet publish RPGModder.UI/RPGModder.UI.csproj `
  --configuration Release `
  --runtime linux-x64 `
  --self-contained true `
  --output artifacts/linux-x64/RPGModder-2.0.0-linux-x64
```

The repository `.allyrc` then syncs that artifact and launches it through `ally-tooling`. The published executable also supports `--platform-diagnostics` and the isolated Linux integration check `--platform-self-test`.

On a Linux packaging machine without the .NET SDK, create the release archive from the synced cross-published directory:

```bash
PREBUILT_DIR=artifacts/linux-x64/RPGModder-2.0.0-linux-x64 \
  ./packaging/linux/package.sh 2.0.0
```

---

## 📖 Usage Guide

### 1. Managing Games
* RPGModder automatically scans your Steam library and common folders for RPG Maker games.
* Select a game from the dropdown or click **Browse** to find a Windows/Proton or native Linux game executable manually.
* The tool performs a one-time safe-state initialization and imports legacy RPGModder data into the per-game `.rpgmodder` workspace without deleting the legacy files.

### 2. Installing Mods
* **From Nexus:** Click "Mod Manager Download" on the website.
* **From File:** Drag and drop a `.zip` file or a mod folder directly onto the application window.
* **Activation:** Check the box next to a mod to enable it, then click **"Apply Changes"** to rebuild the game.

---

## Why does this mod manager exist instead of using FHMM?

* This began as a simple project to make a mod manager for Look Outside (my favorite RPG Maker game!) and it turned out to be much bigger as I kept going. I hope people give it a chance, it would make me very happy if modders use it. 

## 📄 License
This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---
