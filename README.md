# kaobots

AOSharp profession handlers and managers, based on [AOSp Bots](https://gitlab.com/aosharp-plus/aosp-bots).

Credit to **Knowidea / never-knows-best (Know)** and all original contributors. This repository builds on their work; original source notices are retained. Imported from upstream source commit `59de6fe9a0ec297e71fbf4338fdcb01683947865`.

## Additions in this source import

- **Master’s Bidding:** separate attack/support pet toggles in the shared Pets buff window, requirement checks, specific offensive-proc conflict checks, and `/petstats` diagnostics. The nano must be uploaded on the character for the option to appear.
- **Agent snare-perk controls:** Tranquilizer appears in Holds; disabled Tranquilizer and Soften Up actions are removed from the pending queue. Already submitted actions cannot be recalled.
- **Pet Commands for every profession:** control pet owners on the same IPC channel from a leader such as Keeper.
- **HelpManager 42 Autofence:** optional per-character attempts to use a nearby Biological Transceiver until its protection buff is present. Requires 10 free NCU; delays attempts while busy and stops attempting when the object is absent or out of range. It does not navigate or manage pet movement.
- **Manager Sync:** stop repeated syncbag scans after a scheduled pass; preserve reopening after zoning. Give OnUpdate work a nonblocking one-second startup grace period.

## Versions

- Shared handler settings version: **2.44** (retains the existing numeric format).
- Profession handler file versions: **1.0.3.0**; assembly identities remain unchanged for compatibility.
- HelpManager: **2.0.8**.
- Manager Sync: **2.0.10**.

## Status and known issues

This is a source import, not a packaged release. No build or test run was performed for this import. Earlier in-game use confirmed pet buffing and Agent raid snaring, but the complete imported set has not been validated together.

**42 Autofence Broadcast Settings has a reported issue under investigation. Enable the option individually on each character for now; do not assume a broadcast applied it.** Existing-pet buff behavior and background-client stalls may still need further investigation. The Sync changes address specific work scheduling, not every possible cause of client lag.

For Agent raid snaring in Mimic Fixer, select Spin Nanoweb in the Fixer area-snare selection and enable the raid snare option. The ordinary Holds activation need not also be enabled; the raid targeting path excludes unrelated nearby mobs.

Deploy the complete matching output, including `ProfessionHandler.Generic.dll`, the selected profession DLLs and UI assets. See [the upstream setup notes](UPSTREAM-README.md) for its build/deployment guidance. No automated build or release workflow is enabled here.
