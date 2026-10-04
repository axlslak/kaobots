Common issues
AOSP (AOSharp Plus) build (most common)
•	AOSP bots must be built in Release
•	The injected DLL must come from the Release output folder
•	Debug is not supported
Blocking (VPN / AV / Firewall)
•	Do not use a VPN
•	Antivirus may block AOSharp because injecting/loading DLLs into another app can look malicious
•	Firewall may block local IPC traffic for the same reason
Bad build
•	Set Parallel builds to 1
•	Do a Clean + Build (don’t use Rebuild)
Fix
•	Build AOSP in Release and inject the DLL from the Release folder
•	Turn off VPN
•	Add exceptions/allow rules for all AOSharp files/folders (and allow local IPC in the firewall if needed)
•	Set Parallel builds to 1, then Clean + Build