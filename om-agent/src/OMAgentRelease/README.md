# OM Client — Control Center & Application Blocker (v1.0.0)

A website-first control center and robust application blocking agent for Windows 10/11. Built with an HTML5/CSS3/JavaScript dashboard rendered natively through the Windows WebView2 (Edge) engine.

---

## ❓ How the OM Agent Stops Apps from Opening (Resolving All Doubts)

Many tools attempt to block applications by running a loop that detects processes and calls `taskkill`. **That is brittle, causes window flickering, wastes CPU, and lets apps run for a fraction of a second.**

The OM Agent uses a **multi-layered, kernel-supported Windows enforcement mechanism** that stops the app before it even launches:

### Layer 1: Image File Execution Options (IFEO) Interception (Instantaneous & Universal)
- **Windows Registry:** `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<app.exe>`
- **Value:** `Debugger = "systray.exe"` (or dummy redirect)
- **How it works:** When any user, shortcut, background task, or command prompt attempts to launch the executable (e.g. `notepad.exe` or `game.exe`), the Windows kernel checks IFEO. Because a debugger hook is present, **Windows executes the redirect instead of the application binary!**
- **Result:** The application never opens, 0 milliseconds of execution, 0 process creation, and zero window flicker.
- **Unblocking:** Simply deletes the `Debugger` value. The app opens normally again immediately. No reboot required!

### Layer 2: Windows Explorer DisallowRun Policy
- **Windows Registry:** `HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\DisallowRun`
- **Value:** Lists the blocked executable names.
- **How it works:** When a user double-clicks an icon on the Desktop, File Explorer, or Start Menu, Windows Explorer checks the policy and displays:
  > *"This operation has been cancelled due to restrictions in effect on this computer. Please contact your system administrator."*

### Layer 3: Active Worker Process Watcher
- Even if an application was already running before the block was enabled, the background `OMAgent` service running as `SYSTEM` detects the running PID and terminates it instantly (`Process.Kill()`).

### Layer 4: Critical Whitelist Protection
- You can **never accidentally lock yourself out of Windows or the agent**:
  Critical system executables (`OMClient.exe`, `OMAgent.exe`, `explorer.exe`, `cmd.exe`, `powershell.exe`, `taskmgr.exe`, `svchost.exe`, `csrss.exe`) are protected in code and cannot be blocked.

---

## 🔒 Password-Protected Rule Editing (Password: `om`)

To prevent unauthorized users from tampering with blocking rules or removing restrictions:
- All editing actions are locked in **View-Only Mode** by default.
- Modifying rules, adding a new blocked application, toggling blocks on/off, deleting rules, or starting the Test Phase requires the administrator password:
  **Password:** `om`
- Clicking **"🔓 Unlock to Edit"** prompts for the password. Once unlocked, the interface switches to **Admin Mode** allowing full rule management.

---

## 🧪 Interactive Test Phase (Verify Blocking with Notepad)

The agent includes a dedicated **Test Phase** directly in the UI so you can test and confirm the blocking capability on your own machine before rolling out restrictions:

1. **Step 1 — Block Test App:**
   - Default target is `notepad.exe` (Windows Notepad — safe, lightweight, present on all Windows PCs).
   - Enter password `om` and click **"1. Block Test App"**.
   - The status changes to `[TEST BLOCK ACTIVE]`.
   - **Go to Windows right now:** Press `Win + R`, type `notepad`, and press Enter. **Notice that Notepad does NOT open!**
2. **Step 2 — Verify Block (Automated Launch Test):**
   - Click **"2. Try Launching App (Verify)"**.
   - The agent attempts to spawn `notepad.exe` via `Process.Start`.
   - Checks if Windows prevented launch or returned Access Denied.
   - The interactive Test Console prints step-by-step verification:
     - `[IFEO] Registry debugger intercept set: OK`
     - `[DisallowRun] Policy rule registered: OK`
     - `[Launch Test] Process creation prevented: Access is denied.`
     - `[Verdict] PASSED: notepad.exe was successfully prevented from opening!`
3. **Step 3 — End Test & Restore:**
   - Click **"3. End Test & Restore"**.
   - The agent cleans up the IFEO and DisallowRun entries.
   - Status returns to `[RESTORED]`.
   - Open Notepad again: it opens normally!

---

## 🛠️ Dashboard Tabs & Features

| Tab | Purpose |
|---|---|
| **Dashboard** | Worker service status, PID, live heartbeat, last run result, active blocked apps counter. |
| **App Control** | View and edit blocked apps, password unlock (`om`), add/toggle/delete rules, and run the **Test Phase**. |
| **Commands** | Execute commands with **ADMIN** (SYSTEM) or **USER** privileges in both **cmd** and **PowerShell**. |
| **Logs** | Real-time tail of the `OMAgent` service and block enforcement log. |
| **About** | System overview and architectural notes. |

---

## 💻 Building the Versioned Executable

On your Windows build machine (or any environment with .NET 8 SDK):
```powershell
cd om-bg\webview
.\publish-versioned.ps1
```
This produces `out\OMClient_1.0.0.exe` (self-contained, single-file, native Windows x64 executable).
