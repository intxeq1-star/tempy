/*
 * CmdTerminal - a tiny native Windows GUI wrapper around cmd.exe
 *
 * - Runs a persistent, hidden cmd.exe process behind a window.
 * - Type a command in the input box, press Enter (or Run).
 *   The command is written to cmd.exe's stdin and any output is
 *   shown in the scrollable pane above.
 * - The shell is a real, persistent process, so things like `cd`
 *   or environment changes (`set X=1`) persist between commands.
 *
 * Build (cross-compile from Linux with Zig):
 *   zig cc -target x86_64-windows-gnu -O2 main.c -o CmdTerminal.exe \
 *       -Wl,--subsystem,windows -D_WIN32_WINNT=0x0601
 */

#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0601
#endif

#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ------------------------------------------------------------------ */
/* Constants                                                           */
/* ------------------------------------------------------------------ */

#define WM_APP_OUTPUT     (WM_APP + 1)   /* lParam = wchar_t* (heap) to append */
#define WM_APP_SHELLDIED  (WM_APP + 2)   /* reader thread lost the pipe        */

#define IDC_OUTPUT   1001
#define IDC_INPUT    1002
#define IDC_RUN      1003
#define IDC_CLEAR    1004
#define IDC_RESTART  1005

#define OUTPUT_LIMIT   3000000   /* chars kept in the output pane      */
#define OUTPUT_TRIM    1000000   /* chars dropped from the top on trim */
#define HISTORY_MAX    100

/* ------------------------------------------------------------------ */
/* Globals                                                             */
/* ------------------------------------------------------------------ */

static HWND   g_hwnd        = NULL;
static HWND   g_hOutput     = NULL;
static HWND   g_hInput      = NULL;
static HWND   g_hRun        = NULL;
static HWND   g_hClear      = NULL;
static HWND   g_hRestart    = NULL;
static HFONT  g_hFont       = NULL;
static HBRUSH g_hBrushBg    = NULL;

static HANDLE g_hChildProc  = NULL;    /* cmd.exe process handle       */
static HANDLE g_hChildStdin = NULL;    /* write end of child's stdin   */
static volatile LONG g_shellAlive = 0;
static volatile LONG g_expectExit = 0;

static wchar_t *g_history[HISTORY_MAX];
static int      g_histCount = 0;
static int      g_histNav   = -1;      /* -1 = editing the fresh line  */

static WNDPROC  g_oldInputProc = NULL;

/* ------------------------------------------------------------------ */
/* Output pane helpers                                                 */
/* ------------------------------------------------------------------ */

static void append_text(const wchar_t *t)
{
    int len;
    if (!g_hOutput) return;

    len = GetWindowTextLengthW(g_hOutput);
    if (len > OUTPUT_LIMIT) {
        SendMessageW(g_hOutput, EM_SETSEL, 0, OUTPUT_TRIM);
        SendMessageW(g_hOutput, EM_REPLACESEL, 0, (LPARAM)L"");
        len = GetWindowTextLengthW(g_hOutput);
    }
    SendMessageW(g_hOutput, EM_SETSEL, len, len);
    SendMessageW(g_hOutput, EM_REPLACESEL, 0, (LPARAM)t);
    SendMessageW(g_hOutput, EM_SCROLLCARET, 0, 0);
}

/* ------------------------------------------------------------------ */
/* Child cmd.exe management                                            */
/* ------------------------------------------------------------------ */

typedef struct ReaderArgs {
    HANDLE pipe;
    HWND   hwnd;
} ReaderArgs;

static DWORD WINAPI reader_thread(LPVOID param)
{
    ReaderArgs *args = (ReaderArgs *)param;
    HANDLE pipe = args->pipe;
    HWND   hwnd = args->hwnd;
    free(args);

    char  buf[4096];
    char  tmp[2 * sizeof(buf)];
    DWORD n;
    int   lastWasCR = 0;

    for (;;) {
        if (!ReadFile(pipe, buf, sizeof(buf), &n, NULL) || n == 0)
            break;

        /* Normalize bare LF -> CRLF so the EDIT control displays
         * line breaks correctly. Runs on raw bytes; CR/LF are the
         * same in the OEM code page cmd uses on pipes. */
        DWORD m = 0;
        for (DWORD i = 0; i < n; i++) {
            char c = buf[i];
            if (c == '\0') continue;
            if (c == '\n' && !lastWasCR)
                tmp[m++] = '\r';
            tmp[m++] = c;
            lastWasCR = (c == '\r');
            if (c == '\n') lastWasCR = 0;
        }

        int wlen = MultiByteToWideChar(CP_OEMCP, 0, tmp, (int)m, NULL, 0);
        wchar_t *w = (wchar_t *)malloc(((size_t)wlen + 1) * sizeof(wchar_t));
        if (!w) continue;
        MultiByteToWideChar(CP_OEMCP, 0, tmp, (int)m, w, wlen);
        w[wlen] = L'\0';

        if (!PostMessageW(hwnd, WM_APP_OUTPUT, 0, (LPARAM)w))
            free(w);   /* window gone, drop the chunk */

        if (m > n) Sleep(1); /* keep the UI from flooding on huge bursts */
    }

    CloseHandle(pipe);
    PostMessageW(hwnd, WM_APP_SHELLDIED, 0, 0);
    return 0;
}

static void shell_cleanup(void)
{
    if (g_hChildProc) {
        CloseHandle(g_hChildProc);
        g_hChildProc = NULL;
    }
    if (g_hChildStdin) {
        CloseHandle(g_hChildStdin);
        g_hChildStdin = NULL;
    }
    InterlockedExchange(&g_shellAlive, 0);
}

static BOOL shell_start(void)
{
    SECURITY_ATTRIBUTES sa;
    HANDLE hChildInR = NULL, hChildInW = NULL;
    HANDLE hChildOutR = NULL, hChildOutW = NULL;
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    wchar_t cmdline[] = L"cmd.exe /Q";
    ReaderArgs *ra;
    HANDLE hThread;

    shell_cleanup();

    sa.nLength = sizeof(sa);
    sa.lpSecurityDescriptor = NULL;
    sa.bInheritHandle = TRUE;

    if (!CreatePipe(&hChildInR, &hChildInW, &sa, 64 * 1024)) return FALSE;
    SetHandleInformation(hChildInW, HANDLE_FLAG_INHERIT, 0); /* parent keeps it */

    if (!CreatePipe(&hChildOutR, &hChildOutW, &sa, 64 * 1024)) {
        CloseHandle(hChildInR);
        CloseHandle(hChildInW);
        return FALSE;
    }
    SetHandleInformation(hChildOutR, HANDLE_FLAG_INHERIT, 0); /* parent keeps it */

    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput  = hChildInR;
    si.hStdOutput = hChildOutW;
    si.hStdError  = hChildOutW;      /* stderr merged into stdout */

    ZeroMemory(&pi, sizeof(pi));
    if (!CreateProcessW(NULL, cmdline, NULL, NULL, TRUE,
                        CREATE_NO_WINDOW, NULL, NULL, &si, &pi)) {
        CloseHandle(hChildInR);
        CloseHandle(hChildInW);
        CloseHandle(hChildOutR);
        CloseHandle(hChildOutW);
        return FALSE;
    }

    CloseHandle(pi.hThread);
    CloseHandle(hChildInR);   /* child owns these now */
    CloseHandle(hChildOutW);

    g_hChildProc  = pi.hProcess;
    g_hChildStdin = hChildInW;
    InterlockedExchange(&g_shellAlive, 1);

    ra = (ReaderArgs *)malloc(sizeof(ReaderArgs));
    ra->pipe = hChildOutR;
    ra->hwnd = g_hwnd;
    hThread = CreateThread(NULL, 0, reader_thread, ra, 0, NULL);
    if (hThread) CloseHandle(hThread);

    return TRUE;
}

static void shell_stop(void)
{
    InterlockedExchange(&g_expectExit, 1);
    if (g_hChildProc)
        TerminateProcess(g_hChildProc, 1);
    /* Closing our write end + the process exiting causes the reader
     * thread's ReadFile to fail, so the thread cleans itself up. */
    shell_cleanup();
}

/* ------------------------------------------------------------------ */
/* Command input handling                                              */
/* ------------------------------------------------------------------ */

static void history_push(const wchar_t *line)
{
    if (g_histCount > 0 &&
        wcscmp(g_history[g_histCount - 1], line) == 0) {
        g_histNav = -1;
        return;                      /* same as last command */
    }
    if (g_histCount == HISTORY_MAX) {
        free(g_history[0]);
        memmove(g_history, g_history + 1,
                (HISTORY_MAX - 1) * sizeof(wchar_t *));
        g_histCount--;
    }
    g_history[g_histCount] = (wchar_t *)malloc((wcslen(line) + 1) * sizeof(wchar_t));
    if (g_history[g_histCount]) {
        wcscpy(g_history[g_histCount], line);
        g_histCount++;
    }
    g_histNav = -1;
}

static void history_navigate(HWND hEdit, int dir)
{
    if (g_histCount == 0) return;

    if (g_histNav == -1)
        g_histNav = (dir < 0) ? g_histCount - 1 : -1;
    else
        g_histNav += dir;

    if (g_histNav < 0)             g_histNav = 0;
    if (g_histNav >= g_histCount)  g_histNav = -1;

    if (g_histNav == -1) {
        SetWindowTextW(hEdit, L"");
    } else {
        int end;
        SetWindowTextW(hEdit, g_history[g_histNav]);
        end = GetWindowTextLengthW(hEdit);
        SendMessageW(hEdit, EM_SETSEL, end, end);   /* caret to end */
    }
}

static void run_input(void)
{
    int len = GetWindowTextLengthW(g_hInput);
    wchar_t *line = (wchar_t *)malloc(((size_t)len + 1) * sizeof(wchar_t));
    if (!line) return;
    GetWindowTextW(g_hInput, line, len + 1);
    SetWindowTextW(g_hInput, L"");
    SetFocus(g_hInput);

    /* Ignore truly empty input (light Enter taps). */
    if (line[0] == L'\0') { free(line); return; }

    history_push(line);

    /* Echo the command into the output pane. */
    {
        size_t need = wcslen(line) + 16;
        wchar_t *echo = (wchar_t *)malloc(need * sizeof(wchar_t));
        if (echo) {
            _snwprintf(echo, need, L"\r\n> %s\r\n", line);
            echo[need - 1] = L'\0';
            append_text(echo);
            free(echo);
        }
    }

    /* `cls`/`clear` clears OUR window (cmd's own cls can't help us
     * because we don't have a real console). */
    if (_wcsicmp(line, L"cls") == 0 || _wcsicmp(line, L"clear") == 0) {
        SetWindowTextW(g_hOutput, L"");
        free(line);
        return;
    }

    if (!InterlockedCompareExchange(&g_shellAlive, 0, 0) || !g_hChildStdin) {
        append_text(L"[no shell running - press 'Restart Shell']");
        free(line);
        return;
    }

    /* Convert to the OEM code page cmd expects on its pipe, tack on
     * CRLF, and write it to the child's stdin. */
    {
        int bytes = WideCharToMultiByte(CP_OEMCP, 0, line, -1, NULL, 0, NULL, NULL);
        if (bytes > 0) {
            char *a = (char *)malloc((size_t)bytes + 2);
            if (a) {
                DWORD written;
                BOOL ok;
                WideCharToMultiByte(CP_OEMCP, 0, line, -1, a, bytes, NULL, NULL);
                a[bytes - 1] = '\r';
                a[bytes]     = '\n';
                ok = WriteFile(g_hChildStdin, a, (DWORD)bytes + 1, &written, NULL);
                if (!ok) {
                    InterlockedExchange(&g_shellAlive, 0);
                    append_text(L"[write failed - shell is gone; press 'Restart Shell']");
                }
                free(a);
            }
        }
    }
    free(line);
}

/* Subclass proc for the input box: Enter runs, Up/Down walk history. */
static LRESULT CALLBACK InputSubclassProc(HWND h, UINT msg, WPARAM w, LPARAM l)
{
    if (msg == WM_KEYDOWN) {
        if (w == VK_RETURN) {
            run_input();
            return 0;
        }
        if (w == VK_UP)   { history_navigate(h, -1); return 0; }
        if (w == VK_DOWN) { history_navigate(h, +1); return 0; }
    }
    if (msg == WM_CHAR && (w == VK_RETURN || w == '\n'))
        return 0;   /* swallow so it doesn't beep */
    return CallWindowProcW(g_oldInputProc, h, msg, w, l);
}

/* ------------------------------------------------------------------ */
/* Window layout                                                       */
/* ------------------------------------------------------------------ */

static void layout_controls(int w, int h)
{
    const int M = 8, ROW = 30, GAP = 6;
    int y, bx;
    const int bwRun = 70, bwClear = 70, bwRestart = 105;

    MoveWindow(g_hOutput, M, M, w - 2 * M, h - 3 * M - ROW, TRUE);

    y  = h - M - ROW;
    bx = w - M - bwRestart;
    MoveWindow(g_hRestart, bx, y, bwRestart, ROW, TRUE);
    bx -= GAP + bwClear;
    MoveWindow(g_hClear, bx, y, bwClear, ROW, TRUE);
    bx -= GAP + bwRun;
    MoveWindow(g_hRun, bx, y, bwRun, ROW, TRUE);
    MoveWindow(g_hInput, M, y, bx - GAP - M, ROW, TRUE);
}

/* ------------------------------------------------------------------ */
/* Main window proc                                                    */
/* ------------------------------------------------------------------ */

static LRESULT CALLBACK WndProc(HWND h, UINT msg, WPARAM w, LPARAM l)
{
    switch (msg) {
    case WM_CREATE: {
        HDC dc = GetDC(NULL);
        int dpi = dc ? GetDeviceCaps(dc, LOGPIXELSY) : 96;
        if (dc) ReleaseDC(NULL, dc);

        g_hFont = CreateFontW(-MulDiv(12, dpi, 96), 0, 0, 0, FW_NORMAL,
                              FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                              OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                              CLEARTYPE_QUALITY, FIXED_PITCH | FF_MODERN,
                              L"Consolas");
        g_hBrushBg = CreateSolidBrush(RGB(13, 13, 13));

        g_hOutput = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | WS_VSCROLL |
            ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
            0, 0, 10, 10, h, (HMENU)IDC_OUTPUT, NULL, NULL);
        g_hInput = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", L"",
            WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
            0, 0, 10, 10, h, (HMENU)IDC_INPUT, NULL, NULL);
        g_hRun = CreateWindowExW(0, L"BUTTON", L"Run",
            WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
            0, 0, 10, 10, h, (HMENU)IDC_RUN, NULL, NULL);
        g_hClear = CreateWindowExW(0, L"BUTTON", L"Clear",
            WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
            0, 0, 10, 10, h, (HMENU)IDC_CLEAR, NULL, NULL);
        g_hRestart = CreateWindowExW(0, L"BUTTON", L"Restart Shell",
            WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
            0, 0, 10, 10, h, (HMENU)IDC_RESTART, NULL, NULL);

        SendMessageW(g_hOutput, EM_SETLIMITTEXT, OUTPUT_LIMIT, 0);
        SendMessageW(g_hOutput, WM_SETFONT, (WPARAM)g_hFont, TRUE);
        SendMessageW(g_hInput,  WM_SETFONT, (WPARAM)g_hFont, TRUE);

        g_oldInputProc = (WNDPROC)SetWindowLongPtrW(
            g_hInput, GWLP_WNDPROC, (LONG_PTR)InputSubclassProc);

        g_hwnd = h;

        append_text(L"CmdTerminal - your window into cmd.exe\r\n"
                    L"Type a command below and press Enter. "
                    L"Up/Down = history. 'clear' clears this window.\r\n"
                    L"The shell is persistent: cd and set stick between commands.\r\n");

        if (!shell_start())
            append_text(L"\r\n[ERROR] could not launch cmd.exe\r\n");

        SetFocus(g_hInput);
        return 0;
    }

    case WM_SIZE:
        layout_controls(LOWORD(l), HIWORD(l));
        return 0;

    case WM_GETMINMAXINFO: {
        MINMAXINFO *mmi = (MINMAXINFO *)l;
        mmi->ptMinTrackSize.x = 480;
        mmi->ptMinTrackSize.y = 320;
        return 0;
    }

    case WM_CTLCOLOREDIT:
    case WM_CTLCOLORSTATIC: {
        HWND c = (HWND)l;
        if (c == g_hOutput || c == g_hInput) {
            HDC dcm = (HDC)w;
            SetBkColor(dcm, RGB(13, 13, 13));
            SetTextColor(dcm, c == g_hOutput ? RGB(190, 255, 190)
                                             : RGB(255, 255, 255));
            return (LRESULT)g_hBrushBg;
        }
        break;
    }

    case WM_COMMAND:
        switch (LOWORD(w)) {
        case IDC_RUN:     run_input();                          return 0;
        case IDC_CLEAR:   SetWindowTextW(g_hOutput, L"");
                          SetFocus(g_hInput);                   return 0;
        case IDC_RESTART:
            append_text(L"\r\n[restarting shell...]\r\n");
            shell_stop();
            if (shell_start())
                append_text(L"[shell restarted]\r\n");
            else
                append_text(L"[ERROR] could not launch cmd.exe\r\n");
            SetFocus(g_hInput);
            return 0;
        }
        break;

    case WM_APP_OUTPUT:
        append_text((const wchar_t *)l);
        free((void *)l);
        return 0;

    case WM_APP_SHELLDIED:
        if (InterlockedCompareExchange(&g_expectExit, 0, 0)) {
            InterlockedExchange(&g_expectExit, 0);
        } else {
            InterlockedExchange(&g_shellAlive, 0);
            shell_cleanup();
            append_text(L"\r\n[cmd.exe exited - press 'Restart Shell']\r\n");
        }
        return 0;

    case WM_DESTROY:
        if (InterlockedCompareExchange(&g_shellAlive, 0, 0))
            shell_stop();
        else
            shell_cleanup();
        if (g_hFont)    DeleteObject(g_hFont);
        if (g_hBrushBg) DeleteObject(g_hBrushBg);
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(h, msg, w, l);
}

/* ------------------------------------------------------------------ */
/* Entry point                                                         */
/* ------------------------------------------------------------------ */

int WINAPI WinMain(HINSTANCE hInst, HINSTANCE hPrev, LPSTR lpCmd, int nShow)
{
    WNDCLASSEXW wc;
    HWND h;
    MSG m;
    HINSTANCE user32;

    (void)hPrev; (void)lpCmd;

    /* Make text crisp on high-DPI displays. */
    user32 = GetModuleHandleW(L"user32.dll");
    if (user32) {
        typedef BOOL (WINAPI *SetDpiAwareFn)(void);
        SetDpiAwareFn fn = (SetDpiAwareFn)GetProcAddress(user32, "SetProcessDPIAware");
        if (fn) fn();
    }

    ZeroMemory(&wc, sizeof(wc));
    wc.cbSize        = sizeof(wc);
    wc.lpfnWndProc   = WndProc;
    wc.hInstance     = hInst;
    wc.hCursor       = LoadCursor(NULL, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
    wc.lpszClassName = L"CmdTerminalWindow";
    if (!RegisterClassExW(&wc)) return 1;

    h = CreateWindowExW(0, L"CmdTerminalWindow", L"CmdTerminal",
                        WS_OVERLAPPEDWINDOW,
                        CW_USEDEFAULT, CW_USEDEFAULT, 880, 560,
                        NULL, NULL, hInst, NULL);
    if (!h) return 1;

    ShowWindow(h, nShow);
    UpdateWindow(h);
    if (g_hInput) SetFocus(g_hInput);

    while (GetMessageW(&m, NULL, 0, 0) > 0) {
        TranslateMessage(&m);
        DispatchMessageW(&m);
    }
    return (int)m.wParam;
}
