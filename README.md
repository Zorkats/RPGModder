# RPGModder

**A modern, non-destructive Mod Manager for RPG Maker MV & MZ games.**

<img width="900" height="600" alt="{1106E3C2-BD36-425D-BF68-469AE4EF63C2}" src="https://github.com/user-attachments/assets/8e93b0be-7cb4-41a1-ad7f-83688a93e852" />


RPGModder is a standalone tool designed to solve the "RPG Maker Hell" of conflicting files and corrupt game folders. It uses a **Virtual File System (VFS)** approach to keep your vanilla game files pristine while allowing you to install, reorder, and merge mods seamlessly.

## 🚀 Key Features

### 🛡️ Safe-State Engine
Never corrupt your game again. RPGModder keeps a clean backup of your vanilla game. When you play, it virtually rebuilds the game folder with your active mods.
* **Zero-Risk Modding:** Uninstalling a mod is as simple as unchecking a box.
* **Auto-Recovery:** If a mod breaks your game, simply "Rebuild" to restore a working state.

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

1.  Download the latest release from the [Releases Page](#).
2.  Extract the ZIP anywhere.
3.  Run `RPGModder.UI.exe`.
4.  (Optional) On first launch, go to **Settings** and click **"Register"** to enable One-Click Downloads from Nexus Mods.

---

## 📖 Usage Guide

### 1. Managing Games
* RPGModder automatically scans your Steam library and common folders for RPG Maker games.
* Select a game from the dropdown or click **Browse** to find a `Game.exe` manually.
* The tool will perform a one-time "Safe State Initialization" to backup your vanilla files.

### 2. Installing Mods
* **From Nexus:** Click "Mod Manager Download" on the website.
* **From File:** Drag and drop a `.zip` file or a mod folder directly onto the application window.
* **Activation:** Check the box next to a mod to enable it, then click **"Apply Changes"** to rebuild the game.

---

## Why is the UI so simple and ugly?

* Honestly? This is my first time making an application on Avalonia from zero, so my experience is pretty much barebones. There's a bit of visual bugs regarding the emojis I used (as I wanted to give a bit of color to the application), but I hope to fix these and improve the visuals of the UI on future updates.

## And why does this mod manager exists? why not use FHMM? (Fear and Hunger Mod Manager)

* This began as a simple project to make a mod manager for Look Outside (my favorite RPG Maker game!) and it turned out to be much bigger as I kept going. I hope people give it a chance, it would make me very happy if modders use it. 

## 📄 License
This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---
