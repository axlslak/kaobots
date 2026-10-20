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

Radar **1.0.15** is a local vicinity HUD for one character, with no IPC. Build `Utility.Radar` in the solution and add `ADDME-Utility.Radar.dll` to AOSharp. Keep the matching output dependencies and updated `UI/RadarProfessions.xml`, `UI/RadarPlayers.xml` and `UI/RadarEditor.xml` beside the plugin. It uses AOSharpSDK 1.0.106 and targets .NET Framework 4.8/x86.

The transparent menu starts with **All (total players)**, then **NPCs**, **Pets**, all fourteen professions, and **Others** at the end. Every category has a count, including zero. **All** and the profession filters contain only other players. **NPCs** contains non-player characters other than pets, including humanoid vendors. **Pets** contains non-player characters positively identified by the engine's `IsPet` flag, without requiring their owner to be loaded nearby. **Others** contains loaded non-character entities such as terminals, vending machines, doors, chests and corpses. Character identity, rather than an HP threshold, distinguishes these groups, so weak creatures and unresolved HP do not move into Others.

Radar adopts your current AO target when enabled; with no eligible target, a fresh injection starts with no category selected. Click a category to browse its list and radar markers; click it again to hide the list. Category browsing never changes AO's target. The names column follows the selected category row and stays hidden with no category selection. Player names retain **(level/AI level)**, sorted by highest level, then highest AI level, with alphabetical ties. Unknown player levels show **?**. NPCs, pets and objects show available level and **HP current / maximum**; object level is labelled **Lv/QL** because it can represent template quality. Missing or inconsistent pools are omitted. NPCs and pets can have levels above 220.

The centre radar keeps your blue arrow pointing up and rotates the field with your character's facing. Five white rings mark **20, 40, 60, 80 and 100 horizontal metres**, with rotating compass positions. Matching characters are yellow heading arrows, and objects are small yellow diamonds; a character without a usable horizontal heading also uses a diamond. The selected entity is red. By default the complete radar occupies about half the screen height, with a 500-pixel outer circle at 1080p; its position and size can be edited. Only entities already loaded by your client are available; static scenery is not necessarily a dynel, and 100 metres is a display scale rather than a guaranteed discovery distance.

Click a name to set AO's ordinary selected target and mark that same entity with a red circle and direction line. Clicking an already selected name keeps it selected. Selecting a loaded entity through AO also selects its Radar row and automatically chooses its profession, NPCs, Pets or Others category. A player whose profession is unresolved uses All, then moves to the proper profession as the client cache resolves it. Manual category browsing pauses automatic category follow until the target changes, while the current target's red marker, radar symbol and resource readouts remain visible. Clearing AO's target, selecting yourself or targeting an unavailable entity removes the Radar entity highlight. Player readouts remain **above the circle**: red **current HP / maximum HP**, with blue **current nano / maximum nano** beneath it. NPCs, pets and objects show a red HP row there only when a usable pool is available. The centred line-drawn digits face the camera, scale naturally with distance and have no background. The marker follows the entity's location; it does not recolour its body model. Synchronization uses AO's ordinary selected target, separately from its fighting target. Radar does not issue an attack command.

Resource and level values come from the client cache and may be incomplete or stale. Invalid player pool values show **?**; the remote maximum-nano placeholder 10 is also treated as unknown. Non-character pools with maximum HP of 1 or less are omitted. Profession information is requested gradually for players without selecting them or opening Info windows; no information requests are added for NPCs, pets or objects. Native debug drawings follow the client's visibility behaviour. The renderer assumes the normal full-display game viewport.

- `/radar` toggles the HUD.
- `/radar on` enables it.
- `/radar off` disables it.

- `/radar edit` opens the layout editor and enables Radar if needed.
- `/radar lock` saves the edited layout and closes the editor.
- `/radar cancel` closes it without changing the saved layout.
- `/radar reset` previews the centred default size while editing, or restores and saves that default when no editor is open.

The layout editor is an ordinary AO window with a clear **LAYOUT MODE** banner, cyan corner brackets and edge marks, a prominent **Save & Lock** button, and amber **MOVING / RESIZING** feedback. Use the actual window title/tab strip to move it and its native edges or corners to resize. The body heading is informational. The frame fades in and out, and its corner accents expand/contract during that transition. The game may supply its normal move/resize cursors as well. The radar remains circular and the outer ring stays at 100 metres; resizing changes its screen footprint. A rectangular editing window fits the circular radar within its available canvas.

**Save & Lock** commits the layout. **Reset** previews the original centre and half-height footprint; **Cancel** retains the previous saved layout. Position and size are stored as screen proportions per character under `%LOCALAPPDATA%\AOSharp\Radar\<character ID>\layout.xml` and fitted to the available screen when loaded. Saving uses a temporary file and atomic replacement; a save failure keeps the editor open with an error message. Turning Radar off, zoning or unloading while editing attempts to save the last validated preview and closes the frame. If the game window becomes too small for editing, the editor closes after attempting to preserve that preview. No settings are written each frame.

This first editor is explicitly enabled with `/radar edit`. While it is open its native window receives mouse input; locking or cancelling removes that window and restores the normal click-through radar. Automatic edge-hover reveal is not implemented. Missing editor UI/native exports or an invalid coordinate mapping close only the editor, leaving the existing HUD available. The native frame mapping is checked at creation; precise chrome/grip alignment, skin/font metrics and live dragging still need an in-game check.

Counts and lists refresh every 250 ms; normal-target changes, positions, headings and selected-entity readouts are read each game frame. Target changes request an immediate list refresh. Radar clears its local selection when the target becomes unavailable, Radar is turned off or you zone; these actions never clear or change AO's target. Re-enabling Radar adopts the current target again. The browse category persists for the current injection until target follow chooses another category. The list is populated before native window creation and replaced when its roster, displayed names/levels or selection changes; HP updates existing text fields without replacing the list. The old `UI/Radar.xml` counter template is retained but unused.

The native drawing approach was informed by [Mali's Dungeon Map](https://github.com/MalishadeAO/malis-dungeon-map-2.0), the [aorust native GUI and rendering notes](https://github.com/reiserFSs/aorust), and [ao-vk's native renderer reconstruction](https://github.com/wannabuh/ao-vk). This update was reviewed as source only; no build, test run or packaged release was produced.
