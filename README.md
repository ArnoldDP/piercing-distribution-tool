# Piercing distribution tool

A Windows tool for Skyrim Special Edition that gives individual NPCs body piercings by patching their skins, so the piercings are part of the body: they show when the NPC is nude, hide under body armor, and are never stripped by OStim or anything else that unequips armor.

## What it does

- Loads your load order (enabled plugins only) and lists every NPC, with a 3D preview of the NPC nude: their real skin, FaceGen head, body textures and texture sets, skinned on the race skeleton and blended by weight.
- You build **piercing sets** from armor or armor addon records in your load order: search by name, EditorID, mesh path or plugin, or pick a plugin to list everything in it.
- You assign a set to an NPC, one NPC at a time. Nothing is handed out at random.
- **Write patch** produces a plugin (ESL-flagged when it fits) that gives each assigned NPC a copy of their current skin with the piercings added.

### Two ways to add the piercings

- **Baked into the body mesh** (default). The tool writes a copy of the body mesh with the piercing shapes merged in, plus a combined BodySlide morph file, so OBody/RaceMenu presets reshape the piercings with the body. Body armor hides them along with the body. Reflective (cube-mapped) shaders are switched to plain lit metal in the merged copies, alternate texture sets from the piercing's armor addon are written into the mesh, and an SMP piercing's physics link is carried onto the merged body when the body has none of its own.
- **Separate addons.** The piercing addons are added to the skin in their own slots. They show when nude, but don't hide under armor. The tool warns when a piercing's slot clashes with a slot the skin already uses.

## Requirements

- Windows, [.NET 10 desktop runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- Skyrim Special Edition
- The piercing mods you want to use, with their BodySlide meshes built if they ship as BodySlide projects

## Usage

1. Add `PiercingDistributionTool.exe` to Mod Organizer 2's executables and launch it from there, so it sees your mods. Leave the Data folder blank to auto-detect (under MO2 it uses MO2's game folder).
2. Click **Load game**.
3. Find piercings under **Find piercings in your load order** and add them to a set.
4. Pick an NPC, choose the set under **This NPC**, and check the preview.
5. Click **Write patch**. With MO2 the plugin and merged meshes land in overwrite; move them into a mod and enable it after your NPC appearance patches.

Rewrite the patch after regenerating NPC appearance patches (EasyNPC and the like) or rebuilding bodies or piercings in BodySlide: the skins and merged meshes are copies made when you write.

## Building

```
dotnet build
dotnet publish src/PiercingDistributionTool/PiercingDistributionTool.csproj -c Release -r win-x64 --self-contained false -o out
```

The exe opts out of CET shadow stacks (`<CETCompat>false</CETCompat>`); without that it crashes on start under MO2's virtual file system.

## Built with

- [Mutagen](https://github.com/Mutagen-Modding/Mutagen) for plugins and archives
- [NiflySharp](https://github.com/ousnius/nifly) (nifly) for meshes
- [BCnEncoder.Net](https://github.com/Nominom/BCnEncoder.NET) and [Pfim](https://github.com/nickbabcock/Pfim) for textures
- [OpenTK](https://github.com/opentk/opentk) for the preview

## License

GPL-3.0. See [LICENSE](LICENSE).
