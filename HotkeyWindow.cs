using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SonarChatMix;

// Ventana invisible (message-only) que recibe los atajos globales registrados con RegisterHotKey.
// Funcionan aunque estés dentro de un juego o con otra ventana en foco.
sealed class HotkeyWindow : NativeWindow, IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(Keys vk);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string? name);

    const int WM_HOTKEY = 0x0312, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WH_KEYBOARD_LL = 13;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    readonly List<int> ids = [];

    // Windows reserva F12 (para depuradores) y RegisterHotKey siempre lo rechaza. Para esos casos
    // escuchamos el teclado con un hook de bajo nivel, que es lo mismo que hace Sonar.
    readonly List<(int Id, Mod Mods, Keys Key)> porHook = [];
    readonly HookProc hookProc; // referencia viva para que el GC no se la lleve
    IntPtr hook;

    public event Action<int>? Pressed;

    public HotkeyWindow()
    {
        hookProc = AlTeclear;
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
    }

    public bool Register(int id, Mod mods, Keys key)
    {
        if (RegisterHotKey(Handle, id, (uint)mods, (uint)key))
        {
            ids.Add(id);
            return true;
        }
        if (key != Keys.F12) return false; // lo usa otro programa

        if (hook == IntPtr.Zero)
        {
            using var modulo = Process.GetCurrentProcess().MainModule!;
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(modulo.ModuleName), 0);
            if (hook == IntPtr.Zero) return false;
        }
        porHook.Add((id, mods, key));
        return true;
    }

    public void UnregisterAll()
    {
        foreach (int id in ids) UnregisterHotKey(Handle, id);
        ids.Clear();
        porHook.Clear();
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
    }

    IntPtr AlTeclear(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var key = (Keys)Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode
            Mod mods = ModsApretados();
            foreach (var h in porHook)
            {
                if (h.Key != key || h.Mods != mods) continue;
                Pressed?.Invoke(h.Id);
                return 1; // nos quedamos con la tecla, igual que un atajo normal
            }
        }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    static Mod ModsApretados()
    {
        static bool Abajo(Keys k) => GetAsyncKeyState(k) < 0;
        Mod m = Mod.None;
        if (Abajo(Keys.ControlKey)) m |= Mod.Ctrl;
        if (Abajo(Keys.Menu)) m |= Mod.Alt;
        if (Abajo(Keys.ShiftKey)) m |= Mod.Shift;
        if (Abajo(Keys.LWin) || Abajo(Keys.RWin)) m |= Mod.Win;
        return m;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY) Pressed?.Invoke((int)m.WParam);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }
}
