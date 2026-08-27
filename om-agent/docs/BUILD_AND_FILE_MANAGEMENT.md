# How we stay under 120 MB and keep the workspace clean

This is the **reusable method** for building .NET Windows tools on a Linux sandbox without
cluttering the workspace or blowing past the 120 MB limit. Reuse the same pattern for the whole project.

---

## 1. The 3 rules that keep the workspace tiny

**Rule A — Only source + the final deliverable live in the workspace.**
The workspace holds `.cs`, `.csproj`, docs, and ONE final `.exe`. Everything transient (build output,
NuGet cache, the .NET SDK) is built somewhere else.

**Rule B — Build OUTSIDE the workspace, then copy just the result back.**
The `.NET SDK`, `bin/`, `obj/`, and NuGet package cache all live under **`/var/tmp`** (big disk,
writable, NOT in the workspace snapshot).

**Rule C — Keep exactly ONE copy of big binaries.**
A 67 MB self-contained EXE is the bulk of the budget. A duplicate would double it.
