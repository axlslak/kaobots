# kaobots

AOSharp profession handlers and managers, based on [AOSp Bots](https://gitlab.com/aosharp-plus/aosp-bots).

Credit to **Knowidea / never-knows-best (Know)** and all original contributors. This repository builds on their work; original source notices are retained. Imported from upstream source commit `59de6fe9a0ec297e71fbf4338fdcb01683947865`.

## Additions in this source import

- **Master’s Bidding:** separate attack/support pet toggles in the shared Pets buff window, requirement checks, specific offensive-proc conflict checks, and `/petstats` diagnostics. The nano must be uploaded on the character for the option to appear.
- **Agent snare-perk controls:** Tranquilizer appears in Holds; disabled Tranquilizer and Soften Up actions are removed from the pending queue. Already submitted actions cannot be recalled.
- **Pet Commands for every profession:** control pet owners on the same IPC channel from a leader such as Keeper.
- **HelpManager 42 Autofence:** optional per-character attempts to use a nearby Biological Transceiver until its protection buff is present. Requires 10 free NCU; delays attempts while busy and stops attempting when the object is absent or out of range. It does not navigate or manage pet movement.
- **Manager Sync:** stop repeated syncbag scans after a scheduled pass; preserve reopening after zoning. Give OnUpdate work a nonblocking one-second startup grace period. Remember the saved outside team invite preference across loads; save invite state changes immediately.

## Versions

- In-game handler and settings version: **2.45**. The footer reports the source revision independently of saved preferences.
- Profession handler file versions: **1.0.4.0**; assembly identities remain unchanged for compatibility.
- HelpManager: **2.0.9**.
- Manager Sync: **2.0.12**.

## Status and known issues

This is a source import, not a packaged release. No build or test run was performed for this import. Earlier in-game use confirmed pet buffing and Agent raid snaring, but the complete imported set has not been validated together.

**42 Autofence Broadcast Settings has a reported issue under investigation. Enable the option individually on each character for now; do not assume a broadcast applied it.** Existing-pet buff behavior and background-client stalls may still need further investigation. The Sync changes address specific work scheduling, not every possible cause of client lag.

For Agent raid snaring in Mimic Fixer, select Spin Nanoweb in the Fixer area-snare selection and enable the raid snare option. The ordinary Holds activation need not also be enabled; the raid targeting path excludes unrelated nearby mobs.

Deploy the complete matching output, including `ProfessionHandler.Generic.dll`, the selected profession DLLs and UI assets. See [the upstream setup notes](UPSTREAM-README.md) for its build/deployment guidance. No automated build or release workflow is enabled here.

One unused alternative Sector 13 navigation mesh is stored losslessly compressed; see [its extraction note](Shared/NavMeshes/s13%20mesh/README.md). All project-referenced navigation assets retain their original format.


## Handler (Auto)

Add `ADDME-ProfessionHandler.Auto.dll` to AOSharp and enable **Handler (Auto)** on each character. It reads the character's real profession once and initializes the matching existing profession handler. Agents keep the Agent handler while mimicking another profession. The normal `/handler` window and per-character handler settings remain in use.

Keep the complete matching plugin output together: Auto, the profession handler DLLs, Generic, Shared, other dependencies, `UI` and `Bosses.json`. Register only Auto in AOSharp; the other handler DLLs stay beside it. Other managers remain separate selectable plugins. Individual profession plugins remain available for manual selection.

Enable either Auto or one manual profession handler per character. Updated entry points reject a second handler, including Auto/manual combinations in either order. Startup errors collected by the existing handler error path now reach plugin initialization; runtime error reporting keeps its existing behavior. If startup fails, eject/reinject with matching output. Existing full-domain loader cleanup/retry remains responsible for cleanup; there is no live profession switching.

Auto uses the selected handler's original `Init` and `Teardown` lifecycle, including message registration and resource paths. Unsupported professions or missing modules report a startup failure. This adds no new work to the game update loop. The launcher requires no changes. First source version: Auto1.0.0. Source review only; runtime verification pending. No builds/tests/releases performed.


## Which DLLs to add

Injectable outputs use `ADDME-<old filename>.dll`, including Auto, individual profession handlers, managers, automatons and utilities. Sort the output folder by name, select the ADDME DLLs as a group, and add/drop them into AOSharp. Registering plugins adds them to the library; choose which ones to enable for each character. Use Auto OR one individual profession handler per character.

Dependencies such as `ProfessionHandler.Generic.dll`, `Shared.dll`, AOSharpSDK and Newtonsoft.Json keep their original filenames. Keep them and the UI/data resources beside the plugins. Internal assembly names and binding versions remain unchanged. Auto loads the prefixed profession modules. Older generated unprefixed plugin DLL aliases are removed when their prefixed output is copied successfully; dependency files are left in place.


## Radar

Radar **1.0.11** is a local vicinity HUD for one character, with no IPC. Build `Utility.Radar` in the solution and add `ADDME-Utility.Radar.dll` to AOSharp. Keep the matching output dependencies and `UI/RadarProfessions.xml` and `UI/RadarPlayers.xml` beside the plugin. It uses AOSharpSDK 1.0.106 and targets .NET Framework 4.8/x86.

The transparent profession menu shows **All (total)** and a count for every profession, including zero. It starts with no category selected. Click a category to show its players; click it again to clear it. Names show **(level/AI level)**, sorted by highest level, then highest AI level, with alphabetical ties. The names column follows the selected profession row and stays hidden with no selection. Unknown levels show **?**. Profession information is requested gradually without selecting people or opening Info windows.

The centre radar keeps your blue arrow pointing up and rotates the field with your character's facing. Five white rings mark **20, 40, 60, 80 and 100 horizontal metres**, with rotating compass positions. Matching players are yellow heading arrows; the selected player is red. The complete radar occupies about half the screen height, with a 500-pixel outer circle at 1080p. Only players already loaded by your client are available; 100 metres is a display scale, not a guaranteed discovery distance.

Click a name to mark that player with a red circle and direction line; click it again to clear. Two centred world-space readouts sit **above the circle**: red **current HP / maximum HP**, with blue **current nano / maximum nano** beneath it. The line-drawn digits face the camera and scale naturally with distance. They have no background. This marks the player's location; it does not recolour the body model or change AO's current target.

Resource values come from the client cache. Invalid values show **?**; the remote maximum-nano placeholder 10 is also treated as unknown. No freshness guarantee is provided. Native debug drawings follow the client's visibility behaviour. The renderer assumes the normal full-display game viewport.

- `/radar` toggles the HUD.
- `/radar on` enables it.
- `/radar off` disables it.

Counts and lists refresh every 250 ms; positions, headings and selected-player readouts update each game frame. Selection clears when the player leaves the list, the category is cleared, Radar is turned off or you zone. The category choice, including no selection, persists for the current injection. The old `UI/Radar.xml` counter template is retained but unused.

The native drawing approach was informed by [Mali's Dungeon Map](https://github.com/MalishadeAO/malis-dungeon-map-2.0) the [aorust native GUI and rendering notes](https://github.com/reiserFSs/aorust), and [ao-vk's native renderer reconstruction](https://github.com/wannabuh/ao-vk). This is a source addition; no build, test run or packaged release was produced for this copy.
