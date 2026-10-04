# Optional upstream Sector 13 mesh

`4365.navmesh.gz` preserves the original upstream `4365.navmesh` losslessly. It is compressed to fit the source-upload transport limit. This extra mesh is not referenced by `Shared.csproj`; the normal `NavMeshes/4365.navmesh` remains unchanged.

If you need this alternative mesh, extract it with 7-Zip or `gzip -dk 4365.navmesh.gz`. The extracted file is 19,416,388 bytes, SHA-256 `658a55c078e5bf9214e28d1b57368f0e73fa1ad74a1f3853625d78d89191de5e`.
