# Changelog

All notable changes to RPGModder are documented here.

## [2.0.0] - 2026-07-10

### Added

- Transactional mod deployment with recovery journals, snapshots, rollback, and deployment history.
- Resumable compatibility migration for mods, profiles, active-profile state, backups, and saves from earlier RPGModder versions.
- Redesigned Avalonia workspace for mods, conflicts, activity, creator tools, Nexus Mods, and settings.
- Linux x64 support for native and Steam/Proton RPG Maker MV/MZ games.
- Linux Steam library discovery, including standard and Flatpak installations.
- Linux `nxm://` registration through XDG desktop integration.
- Linux Secret Service credential storage through `secret-tool`; plaintext API-key persistence is prohibited.
- Self-contained Linux packaging, per-user install/uninstall scripts, desktop entry, and application icon.
- Cross-platform automatic update selection and transactional restart scripts.

### Changed

- Game discovery recognizes both root and `www/` engine layouts and uses platform-correct path comparison.
- Manual game selection supports native Linux launchers as well as Windows executables used through Proton.
- Settings files receive user-only permissions on Linux where the filesystem supports Unix modes.
- Project publishing now supports both `win-x64` and `linux-x64` runtime identifiers.

### Fixed

- Canonical Nexus `nxm://game/mods/id/files/id` links are parsed using the URI host as the game domain.
- Failed or interrupted deployments no longer leave partially modified game content.
- Unsafe manifest paths and invalid mod IDs are rejected before installation.
- Legacy workspace imports no longer disappear after upgrading to the redesigned release.
