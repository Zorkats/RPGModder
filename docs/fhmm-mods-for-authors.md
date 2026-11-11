# FHMM mods in RPGModder: a guide for mod authors

RPGModder runs FHMM mods as they are: FHMM itself is installed as a managed mod, and your files are deployed under `mods`. Some FHMM mods can also be republished as plain RPGModder mods, and they work better that way. This page tells you which kind yours is.

## Which kind is your mod?

| Your mod | Can it become a plain RPGModder mod? | What to do |
|---|---|---|
| Has scripts that call FHMM's functions | No. It needs FHMM while the game runs | Nothing. Players enable FHMM and your mod in RPGModder, then turn your mod on in FHMM's in-game mod menu |
| Only ships data or images that FHMM merges when the game starts | Yes | Republish the same files in RPGModder's layout (below) |

## Why republish a data or image mod

- RPGModder merges your files when the player deploys, not at every game start.
- RPGModder's conflict analysis shows where your mod and another one change the same thing.
- Players do not need FHMM for your mod.

## How to republish

1. Put your files in a folder that mirrors the game folder, for example `www/data/` or `img/pictures/`, exactly where the game reads them.
2. Generate `mod.json` with the **Auto-Packer** in RPGModder: choose your work folder and a clean copy of the game, then **Analyze** and **Generate**. The README's "Creator Tools" section explains each step.
3. Zip the folder with `mod.json` at its top, and upload it as a new file.

Keep your FHMM version available while players move over.

## Why there is no automatic converter

An FHMM mod's own script decides which folder FHMM merges from, so a tool can only guess it. A wrong guess makes a mod that deploys cleanly and then does nothing, or does something else. You know your mod; the tool does not.

## FHMM installed outside RPGModder

If a player copied FHMM into the game folder by hand, RPGModder notices it and stops before changing the game folder, so the player can sort out their install first. This concerns the player's install, not your mod.
