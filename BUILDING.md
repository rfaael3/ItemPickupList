# Building Item Pickup List 0.3.10

Author: **rfaael**.

1. Extract all files, keeping `Assets/ModIcon.png` beside the project in the `Assets` folder.
2. Set the game, mod profile, and Risk of Options paths in `ItemPickupList.csproj`.
3. Run `dotnet build -c Release` in the project directory.
4. Remove any previous ItemPickupList DLL copies from your mod profile.
5. Install `bin/Release/netstandard2.1/ItemPickupList.dll` in `BepInEx/plugins/ItemPickupList/`.

The icon is embedded in the DLL. Use the matching Thunderstore template and `BUILD_PACKAGE.ps1` to prepare the release ZIP.
The plugin identifier is `rfaael.ItemPickupList`; configuration is saved to `BepInEx/config/rfaael.ItemPickupList.cfg`. To retain settings from 0.3.7 or later, copy the previous configuration to this new filename before starting the game.
