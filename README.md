# MHO Package Modifier

A free, open-source Windows workbench (`MHO_UPK_Mod.exe`) for looking inside **Marvel Heroes Omega**'s game packages (`.upk`, a customized Unreal Engine 3 format) and changing them:

- **Values:** browse any package's objects and change them: fog, colors, material parameters.
- **Textures:** view and replace them, including images streamed from the `.tfc` caches.
- **Meshes:** view them in 3D, export them to FBX and import edited ones back; changes are confirmed in-game.
- **Zones:** edit a zone's building placements in Blender (a round trip), copy materials and objects between packages, and rebuild a zone's distant view.

Every write makes a `.bak` of the original, is checked by a dry run first, and can be undone. The manual opens with F1. The user guide is [`Dist/README.txt`](Dist/README.txt).

## Get it

[Latest release](https://github.com/leeper48/MHO-Package-Modifier/releases/latest): unzip it anywhere you can write to (not Program Files) and run `MHO_UPK_Mod.exe`. The app can update itself from this repo's releases (it asks first).

The app is not code-signed, so Windows SmartScreen may warn on first start ("More info", then "Run anyway"). Each release has a `.sha256` file to check the download with `Get-FileHash -Algorithm SHA256`.

## Building from source

- **Needs:** the .NET 8 SDK, on Windows.
- `build.bat` builds and publishes into `publish\`; `release.bat` makes a release zip in `releases\`. Pushing a tag `v<version>` (the csproj version) builds the release on GitHub and drafts it.

## Related

[MHO Extended Mod Manager](https://github.com/leeper48/MHO-UPK-Tools) (a mod manager for the game) builds on this app's code and includes this repo as a submodule.

## License

[MIT](LICENSE). Libraries: AssimpNet and [assimp](https://github.com/assimp/assimp); see `Dist/THIRD-PARTY-NOTICES.txt`.

Marvel Heroes Omega and Marvel characters are trademarks of their owners. This is an unofficial fan project, not affiliated with or endorsed by Marvel, Gazillion or Disney.
