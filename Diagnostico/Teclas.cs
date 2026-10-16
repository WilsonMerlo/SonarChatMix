using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiagnosticoDial;

// Hook de teclado de bajo nivel: registra las teclas de volumen tal como las ve un hook (lo único que una app
// podría descartar). Con --bloquear-volumen además las descarta TODAS, también las del teclado, para ver si
// así deja de aparecer el cartel de volumen de Windows al girar el dial.
sealed class MonitorTeclas : IDisposable
{
    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetQueueStatus(uint flags);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string? name);

    const int WH_KEYBOARD_LL = 13;
    const uint QS_RAWINPUT = 0x0400, LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80;

    readonly HookProc proc; // referencia viva para que el GC no se la lleve
    readonly IntPtr hook;
    readonly bool bloquear;

    public MonitorTeclas(bool bloquear)
    {
        this.bloquear = bloquear;
        proc = AlTeclear;
        using var modulo = Process.GetCurrentProcess().MainModule!;
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(modulo.ModuleName), 0);
        Log.Escribir("TECLA", hook == IntPtr.Zero
            ? $"no se pudo poner el hook de teclado (error {Marshal.GetLastWin32Error()})"
            : bloquear ? "hook de teclado puesto: DESCARTA todas las teclas de volumen (también las del teclado)"
                       : "hook de teclado puesto: solo registra las teclas de volumen");
    }

    // KBDLLHOOKSTRUCT: vkCode, scanCode, flags, time, dwExtraInfo
    IntPtr AlTeclear(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vk = Marshal.ReadInt32(lParam, 0);
            if (vk is 0xAD or 0xAE or 0xAF)
            {
                uint flags = (uint)Marshal.ReadInt32(lParam, 8);
                // ¿Ya está en la cola el Raw Input (el reporte del dial) cuando el hook ve la tecla?
                bool rawEnCola = ((GetQueueStatus(QS_RAWINPUT) >> 16) & QS_RAWINPUT) != 0;
                Log.Escribir("TECLA", $"hook: {(Keys)vk} {((flags & LLKHF_UP) != 0 ? "suelta" : "apretada")} " +
                                      $"(flags 0x{flags:X}{((flags & LLKHF_INJECTED) != 0 ? ", inyectada" : "")}), " +
                                      $"Raw Input en cola: {(rawEnCola ? "sí" : "no")}{(bloquear ? " → DESCARTADA" : "")}");
                if (bloquear) return 1;
            }
        }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
    }
}
