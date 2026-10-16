using System.Runtime.InteropServices;

namespace DiagnosticoDial;

// Ventana invisible que recibe Raw Input de las teclas multimedia (página HID 0x0C, Consumer Control) y de
// los teclados, también con la consola en segundo plano (RIDEV_INPUTSINK). No bloquea nada: Windows
// sigue procesando las teclas como siempre.
sealed class MonitorRawInput : NativeWindow, IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Destino; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER { public uint Tipo, Tam; public IntPtr Dispositivo, WParam; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICELIST { public IntPtr Dispositivo; public uint Tipo; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pedidos, uint n, uint tam);

    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr raw, uint comando, IntPtr datos, ref uint tam, uint tamCabecera);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceInfoW(IntPtr dispositivo, uint comando, IntPtr datos, ref uint tam);

    [DllImport("user32.dll")]
    static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? lista, ref uint n, uint tam);

    const int WM_INPUT = 0x00FF, WM_INPUT_DEVICE_CHANGE = 0x00FE;
    const uint RID_INPUT = 0x10000003, RIDI_DEVICENAME = 0x20000007, RIDI_DEVICEINFO = 0x2000000B;
    const uint RIDEV_PAGEONLY = 0x20, RIDEV_INPUTSINK = 0x100, RIDEV_DEVNOTIFY = 0x2000;
    const uint RIM_TYPEKEYBOARD = 1, RIM_TYPEHID = 2;
    const uint ERROR = uint.MaxValue;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    readonly Dictionary<IntPtr, string> nombres = [];

    public MonitorRawInput() => CreateHandle(new CreateParams { Parent = HWND_MESSAGE });

    public void Iniciar()
    {
        RAWINPUTDEVICE[] pedidos =
        [
            // Toda la página Consumer (Volume Up 0xE9, Volume Down 0xEA, Mute 0xE2, multimedia…)
            new() { UsagePage = 0x0C, Usage = 0, Flags = RIDEV_PAGEONLY | RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, Destino = Handle },
            // Teclados (ahí aparecen VK_VOLUME_UP / VK_VOLUME_DOWN y de qué dispositivo vienen)
            new() { UsagePage = 0x01, Usage = 0x06, Flags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, Destino = Handle },
        ];
        if (RegisterRawInputDevices(pedidos, (uint)pedidos.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            Log.Escribir("RAW", "escuchando Raw Input: página 0x0C (Consumer Control) y teclados, también en segundo plano");
        else
            Log.Escribir("RAW", $"RegisterRawInputDevices falló (error {Marshal.GetLastWin32Error()})");

        ListarDispositivos();
    }

    void ListarDispositivos()
    {
        uint n = 0, tam = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(null, ref n, tam);
        var lista = new RAWINPUTDEVICELIST[n];
        if (GetRawInputDeviceList(lista, ref n, tam) == ERROR) return;

        foreach (var d in lista.Take((int)n))
        {
            if (d.Tipo == RIM_TYPEKEYBOARD)
                Log.Escribir("RAW", $"  teclado: {Describir(d.Dispositivo)}");
            else if (d.Tipo == RIM_TYPEHID && PaginaDe(d.Dispositivo) == 0x0C)
                Log.Escribir("RAW", $"  Consumer Control: {Describir(d.Dispositivo)}");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT) AlRecibir(m.LParam);
        else if (m.Msg == WM_INPUT_DEVICE_CHANGE)
        {
            bool llega = m.WParam == 1; // GIDC_ARRIVAL
            if (llega) nombres.Remove(m.LParam);
            Log.Escribir("RAW", $"{(llega ? "se conecta" : "se desconecta")}: {Describir(m.LParam)}");
        }
        base.WndProc(ref m); // DefWindowProc libera el WM_INPUT
    }

    void AlRecibir(IntPtr raw)
    {
        uint tamCab = (uint)Marshal.SizeOf<RAWINPUTHEADER>(), tam = 0;
        GetRawInputData(raw, RID_INPUT, IntPtr.Zero, ref tam, tamCab);
        if (tam == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)tam);
        try
        {
            if (GetRawInputData(raw, RID_INPUT, buf, ref tam, tamCab) == ERROR) return;
            var cab = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            IntPtr datos = buf + (int)tamCab;

            if (cab.Tipo == RIM_TYPEKEYBOARD)
            {
                // RAWKEYBOARD: MakeCode, Flags, Reserved, VKey (USHORT), Message (UINT), ExtraInformation (ULONG)
                ushort scan = (ushort)Marshal.ReadInt16(datos, 0);
                ushort flags = (ushort)Marshal.ReadInt16(datos, 2);
                ushort vk = (ushort)Marshal.ReadInt16(datos, 6);
                string accion = (flags & 1) != 0 ? "suelta" : "apretada"; // RI_KEY_BREAK
                Log.Escribir("RAW", $"tecla {(Keys)vk} (VK 0x{vk:X2}, scan 0x{scan:X2}, flags 0x{flags:X}) {accion} ← {Describir(cab.Dispositivo)}");
            }
            else if (cab.Tipo == RIM_TYPEHID)
            {
                // RAWHID: dwSizeHid, dwCount, bRawData[dwSizeHid * dwCount]
                int tamReporte = Marshal.ReadInt32(datos, 0), cuenta = Marshal.ReadInt32(datos, 4);
                var bytes = new byte[tamReporte * cuenta];
                Marshal.Copy(datos + 8, bytes, 0, bytes.Length);
                for (int i = 0; i < cuenta; i++)
                    Log.Escribir("RAW", $"HID {Log.Hex(bytes, i * tamReporte, tamReporte)} ← {Describir(cab.Dispositivo)}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    string Describir(IntPtr dispositivo)
    {
        if (dispositivo == IntPtr.Zero) return "(sin dispositivo: la generó Windows o un programa)";
        if (nombres.TryGetValue(dispositivo, out var d)) return d;

        string ruta = RutaDe(dispositivo);
        if (ruta == "") return $"0x{dispositivo.ToInt64():X}";
        ushort pagina = PaginaDe(dispositivo);
        d = $"{Dispositivos.VidPid(ruta)} \"{Dispositivos.NombreDe(ruta)}\"" +
            (pagina != 0 ? $" pág 0x{pagina:X4}" : "") + $" [{Dispositivos.InstanciaDe(ruta)}]";
        return nombres[dispositivo] = d;
    }

    static string RutaDe(IntPtr dispositivo)
    {
        uint tam = 0; // en caracteres
        GetRawInputDeviceInfoW(dispositivo, RIDI_DEVICENAME, IntPtr.Zero, ref tam);
        if (tam == 0) return "";
        IntPtr buf = Marshal.AllocHGlobal((int)tam * 2);
        try
        {
            return GetRawInputDeviceInfoW(dispositivo, RIDI_DEVICENAME, buf, ref tam) == ERROR
                ? "" : Marshal.PtrToStringUni(buf) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // RID_DEVICE_INFO (32 bytes): cbSize, dwType, y para HID: VID, PID, versión, usUsagePage (offset 20), usUsage.
    static ushort PaginaDe(IntPtr dispositivo)
    {
        uint tam = 32;
        IntPtr buf = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.WriteInt32(buf, 0, 32);
            if (GetRawInputDeviceInfoW(dispositivo, RIDI_DEVICEINFO, buf, ref tam) == ERROR) return 0;
            return Marshal.ReadInt32(buf, 4) == RIM_TYPEHID ? (ushort)Marshal.ReadInt16(buf, 20) : (ushort)0;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public void Dispose() => DestroyHandle();
}
