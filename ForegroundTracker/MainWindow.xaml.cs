using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Interop.UIAutomationClient;
using static ForegroundTracker.EventLog;

namespace ForegroundTracker;

public partial class MainWindow : Window
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectFocus = 0x8005;
    private const uint WineventOutofcontext = 0x0000;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool RegisterShellHookWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DeregisterShellHookWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string lpString);

    private delegate IntPtr CbtProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, CbtProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    internal static bool EnableWinEventForegroundHook = true;
    internal static bool EnableWinEventFocusHook = true;
    internal static bool EnableWndProc = true;
    internal static bool EnablePolling = true;
    internal static bool EnableWindowActivated = true;
    internal static bool EnableKeyboardFocus = true;
    internal static bool EnableAppActivated = true;
    internal static bool EnableShellHook = true;
    internal static bool EnableCbtHook = true;
    internal static bool EnableUIAutomationFocus = true;
    internal static bool EnableTitleBarClick = true;

    private AutomationFocusChangedEventHandler? uiaFocusHandler;
    private uint shellHookMsgId;
    private CbtProc? cbtHookDelegate;
    private IntPtr cbtHookHandle;
    private readonly DispatcherTimer foregroundTimer;
    private string lastTitle = "";

    private readonly WinEventDelegate foregroundHookDelegate;
    private readonly WinEventDelegate focusHookDelegate;
    private IntPtr foregroundHookHandle;
    private IntPtr focusHookHandle;
    private IntPtr thisHwnd;
    private bool isAppForeground;
    private CUIAutomationClass uiAutomation;
    private FocusChangedHandler focusChangedHandler;

    internal class FocusChangedHandler(Action<IUIAutomationElement> callback) : IUIAutomationFocusChangedEventHandler
    {
        public void HandleFocusChangedEvent(IUIAutomationElement sender) => callback(sender);
    }

    public MainWindow()
    {
        InitializeComponent();
        Log("Application started");

        foregroundTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        foregroundTimer.Tick += ForegroundTimer_Tick;
        foregroundTimer.Start();

        foregroundHookDelegate = OnForegroundChanged;
        focusHookDelegate = OnObjectFocused;

        // UIA event handlers must be registered from an MTA thread;
        // calling AddFocusChangedEventHandler on WPF's STA silently drops all callbacks.
        Task.Run(() =>
        {
            try
            {
                uiAutomation = new CUIAutomationClass();
                focusChangedHandler = new FocusChangedHandler(OnUiaFocusChanged);
                uiAutomation.AddFocusChangedEventHandler(null!, focusChangedHandler);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UIA focus handler setup failed: {ex.Message}");
            }
        });

        Loaded += (_, _) =>
        {
            thisHwnd = new WindowInteropHelper(this).Handle;

            var source = HwndSource.FromHwnd(thisHwnd);
            source?.AddHook(WndProc);
            Log("WndProc hook installed");

            shellHookMsgId = RegisterWindowMessage("SHELLHOOK");
            RegisterShellHookWindow(thisHwnd);
            Log($"ShellHook registered (msg=0x{shellHookMsgId:X})");

            const int WH_CBT = 5;
            cbtHookDelegate = OnCbtProc;
            cbtHookHandle = SetWindowsHookEx(WH_CBT, cbtHookDelegate, IntPtr.Zero, GetCurrentThreadId());
            Log($"CBT hook installed (handle=0x{cbtHookHandle:X})");

            foregroundHookHandle = SetWinEventHook(
                EventSystemForeground, EventSystemForeground,
                IntPtr.Zero, foregroundHookDelegate, 0, 0, WineventOutofcontext);
            focusHookHandle = SetWinEventHook(
                EventObjectFocus, EventObjectFocus,
                IntPtr.Zero, focusHookDelegate, 0, 0, WineventOutofcontext);
            Log($"ForegroundHook installed (handle=0x{foregroundHookHandle:X})");
            Log($"FocusHook installed (handle=0x{focusHookHandle:X})");

            uiaFocusHandler = OnUIAutomationFocusChanged;
            Automation.AddAutomationFocusChangedEventHandler(uiaFocusHandler);
            Log("UIAutomation FocusChanged handler installed");
        };
        Closed += (_, _) =>
        {
            if (uiaFocusHandler != null)
                Automation.RemoveAutomationFocusChangedEventHandler(uiaFocusHandler);
            if (cbtHookHandle != IntPtr.Zero)
                UnhookWindowsHookEx(cbtHookHandle);
            DeregisterShellHookWindow(thisHwnd);
            if (foregroundHookHandle != IntPtr.Zero)
                UnhookWinEvent(foregroundHookHandle);
            if (focusHookHandle != IntPtr.Zero)
                UnhookWinEvent(focusHookHandle);
        };
    }

    private void OnUiaFocusChanged(IUIAutomationElement obj)
    {
        if (!EnableUIAutomationFocus) return;
        try
        {
            var name = obj.CurrentName;
            var pid = obj.CurrentProcessId;
            var hwnd = new IntPtr(obj.CurrentNativeWindowHandle);
            bool isThis = hwnd == thisHwnd;
            var processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            bool isThisProcess = pid == processId;
            Log($"UIA-COM: FocusChanged name=\"{(string.IsNullOrEmpty(name) ? "(none)" : name)}\" " +
                $"pid={pid} hwnd=0x{hwnd:X}{(isThis ? " [THIS WINDOW]" : isThisProcess ? " [THIS PROCESS]" : "")}");
        }
        catch (COMException ex)
        {
            Log(ex.ToString());
        }
    }

    private void OnForegroundChanged(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (!EnableWinEventForegroundHook) return;
        Log("Foreground Hook called");
        bool isForeground = hwnd == thisHwnd;
        if (isForeground == isAppForeground) return;
        isAppForeground = isForeground;

        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        var title = sb.ToString();

        Log(isForeground
            ? "Hook: App gained foreground"
            : $"Hook: App lost foreground -> {(string.IsNullOrEmpty(title) ? "(none)" : title)}");
    }

    private void OnObjectFocused(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (!EnableWinEventFocusHook) return;
        Log("Focus Hook called");
        if (hwnd == IntPtr.Zero) return;

        var sbTitle = new StringBuilder(256);
        GetWindowText(hwnd, sbTitle, sbTitle.Capacity);
        var title = sbTitle.ToString();

        var sbClass = new StringBuilder(256);
        GetClassName(hwnd, sbClass, sbClass.Capacity);
        var className = sbClass.ToString();

        bool isThis = hwnd == thisHwnd;
        Log($"Hook: ObjectFocus hwnd=0x{hwnd:X} obj={idObject} child={idChild} " +
            $"class=\"{className}\" title=\"{title}\"{(isThis ? " [THIS APP]" : "")}");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_ACTIVATE = 0x0006;
        const int WM_ACTIVATEAPP = 0x001C;
        const int HSHELL_WINDOWACTIVATED = 4;
        const int HSHELL_RUDEAPPACTIVATED = 0x8004;

        const int WM_NCLBUTTONDOWN = 0x00A1;
        const int HTCAPTION = 2;

        if (EnableTitleBarClick && msg == WM_NCLBUTTONDOWN && wParam.ToInt32() == HTCAPTION)
        {
            Log("WndProc: Title bar clicked (WM_NCLBUTTONDOWN HTCAPTION)");
        }

        if (EnableWndProc)
        {
            switch (msg)
            {
                case WM_ACTIVATE:
                    int reason = wParam.ToInt32() & 0xFFFF;
                    string reasonName = reason switch
                    {
                        0 => "WA_INACTIVE",
                        1 => "WA_ACTIVE",
                        2 => "WA_CLICKACTIVE",
                        _ => $"unknown({reason})"
                    };
                    Log($"WndProc: WM_ACTIVATE {reasonName}");
                    break;

                case WM_ACTIVATEAPP:
                    bool activating = wParam != IntPtr.Zero;
                    Log($"WndProc: WM_ACTIVATEAPP activating={activating}");
                    break;
            }
        }

        if (EnableShellHook && shellHookMsgId != 0 && msg == (int)shellHookMsgId)
        {
            int code = wParam.ToInt32();
            if (code is HSHELL_WINDOWACTIVATED or HSHELL_RUDEAPPACTIVATED)
            {
                IntPtr activatedHwnd = lParam;
                var sb = new StringBuilder(256);
                GetWindowText(activatedHwnd, sb, sb.Capacity);
                var title = sb.ToString();
                bool isThis = activatedHwnd == thisHwnd;
                string variant = code == HSHELL_RUDEAPPACTIVATED ? "RUDE" : "NORMAL";
                Log($"ShellHook: {variant} hwnd=0x{activatedHwnd:X} " +
                    $"title=\"{(string.IsNullOrEmpty(title) ? "(none)" : title)}\"{(isThis ? " [THIS APP]" : "")}");
            }
        }

        return IntPtr.Zero;
    }

    private void OnUIAutomationFocusChanged(object sender, AutomationFocusChangedEventArgs e)
    {
        if (!EnableUIAutomationFocus) return;
        try
        {
            if (sender is AutomationElement el)
            {
                var name = el.Current.Name;
                var pid = el.Current.ProcessId;
                var hwnd = new IntPtr(el.Current.NativeWindowHandle);
                bool isThis = hwnd == thisHwnd;
                var processId = System.Diagnostics.Process.GetCurrentProcess().Id;
                bool isThisProcess = pid == processId;
                Log($"UIAutomation: FocusChanged name=\"{(string.IsNullOrEmpty(name) ? "(none)" : name)}\" " +
                    $"pid={pid} hwnd=0x{hwnd:X}{(isThis ? " [THIS WINDOW]" : isThisProcess ? " [THIS PROCESS]" : "")}");
            }
        }
        catch (ElementNotAvailableException)
        {
        }
    }

    private IntPtr OnCbtProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        const int HCBT_ACTIVATE = 5;
        if (EnableCbtHook && nCode == HCBT_ACTIVATE)
        {
            IntPtr activatingHwnd = wParam;
            var sb = new StringBuilder(256);
            GetWindowText(activatingHwnd, sb, sb.Capacity);
            var title = sb.ToString();
            bool isThis = activatingHwnd == thisHwnd;
            Log($"CBT: HCBT_ACTIVATE hwnd=0x{activatingHwnd:X} " +
                $"title=\"{(string.IsNullOrEmpty(title) ? "(none)" : title)}\"{(isThis ? " [THIS APP]" : "")}");
        }
        return CallNextHookEx(cbtHookHandle, nCode, wParam, lParam);
    }

    private void ForegroundTimer_Tick(object? sender, EventArgs e)
    {
        if (!EnablePolling) return;
        var hwnd = GetForegroundWindow();
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        var title = sb.ToString();

        if (title == lastTitle) return;
        lastTitle = title;

        Log($"GetForegroundWindow: {(string.IsNullOrEmpty(title) ? "(none)" : title)}");
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => Clear();
    private void MainWindow_OnActivated(object? sender, EventArgs e) { if (EnableWindowActivated) Log("MainWindow.OnActivated"); }
    private void MainWindow_OnDeactivated(object? sender, EventArgs e) { if (EnableWindowActivated) Log("MainWindow.OnDeactivated"); }
    private void MainWindow_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) { if (EnableKeyboardFocus) Log("MainWindow.OnGotKeyboardFocus"); }
    private void MainWindow_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) { if (EnableKeyboardFocus) Log("MainWindow.OnLostKeyboardFocus"); }
    private void MainWindow_OnMouseDown(object sender, MouseButtonEventArgs e) => Log("MainWindow: MouseDown");
}