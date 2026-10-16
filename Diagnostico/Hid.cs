using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiagnosticoDial;

// Lee los reportes de entrada de todas las colecciones HID de Razer (VID 0x1532).
// SOLO LECTURA: abre con GENERIC_READ (sin permiso de escritura) y solo llama a ReadFile, así que no
// puede mandar output ni feature reports. Cada lector recibe su propia copia de los reportes: no se los
// saca a Windows ni a Synapse.
sealed class MonitorHid : IDisposable
{
    static readonly Guid GuidHid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public uint Tam; public ushort Vid, Pid, Version; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, LargoEntrada, LargoSalida, LargoFeature;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)] public ushort[] Resto;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string ruta, uint acceso, uint compartir, IntPtr seg,
                                             uint disposicion, uint flags, IntPtr plantilla);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, byte[] buf, uint aLeer, out uint leidos, IntPtr overlapped);

    [DllImport("hid.dll")]
    static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attr);

    [DllImport("hid.dll")]
    static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr datos);

    [DllImport("hid.dll")]
    static extern bool HidD_FreePreparsedData(IntPtr datos);

    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr datos, out HIDP_CAPS caps);

    const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    // ruta → lector (null si la colección no se puede leer: teclado/mouse de Windows o sin reportes de entrada)
    readonly Dictionary<string, Lector?> conocidos = new(StringComparer.OrdinalIgnoreCase);
    bool primera = true;

    public void Sincronizar()
    {
        var presentes = Dispositivos.Interfaces(GuidHid)
            .Where(r => r.Contains("VID_1532", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (primera && presentes.Count == 0) Log.Escribir("HID", "no hay ningún dispositivo HID de Razer (VID 0x1532) conectado");
        primera = false;

        foreach (var ruta in conocidos.Keys.Where(r => !presentes.Contains(r)).ToList())
        {
            Log.Escribir("HID", $"desaparece {Dispositivos.InstanciaDe(ruta)}");
            conocidos[ruta]?.Cerrar();
            conocidos.Remove(ruta);
        }

        foreach (var ruta in presentes)
            if (!conocidos.ContainsKey(ruta)) conocidos[ruta] = Abrir(ruta);
    }

    static Lector? Abrir(string ruta)
    {
        string nombre = Dispositivos.NombreDe(ruta), instancia = Dispositivos.InstanciaDe(ruta);

        // Primero sin permisos (acceso 0), solo para leer VID/PID y el tamaño de los reportes.
        var attr = new HIDD_ATTRIBUTES { Tam = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        HIDP_CAPS caps = default;
        using (var info = CreateFileW(ruta, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
        {
            if (info.IsInvalid)
            {
                Log.Escribir("HID", $"no se puede abrir {instancia} (error {Marshal.GetLastWin32Error()})");
                return null;
            }
            HidD_GetAttributes(info, ref attr);
            if (HidD_GetPreparsedData(info, out IntPtr pre))
            {
                if (HidP_GetCaps(pre, out caps) != HIDP_STATUS_SUCCESS) caps = default;
                HidD_FreePreparsedData(pre);
            }
        }

        string corto = $"PID_{attr.Pid:X4} pág 0x{caps.UsagePage:X4}/0x{caps.Usage:X4} \"{nombre}\"";
        Log.Escribir("HID", $"aparece VID_{attr.Vid:X4} {corto} · reportes: entrada {caps.LargoEntrada} B, " +
                            $"salida {caps.LargoSalida} B, feature {caps.LargoFeature} B · {instancia}");

        if (caps.LargoEntrada == 0)
        {
            Log.Escribir("HID", "    no manda reportes de entrada: no hay nada que leer");
            return null;
        }

        var h = CreateFileW(ruta, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int e = Marshal.GetLastWin32Error();
            Log.Escribir("HID", $"    no se puede leer (error {e}{(e == 5 ? ": Windows la tiene en exclusiva, es teclado o mouse" : "")})");
            h.Dispose();
            return null;
        }

        Log.Escribir("HID", "    leyendo (solo lectura)");
        var lector = new Lector(h, caps.LargoEntrada, corto);
        lector.Iniciar();
        return lector;
    }

    public void Dispose()
    {
        foreach (var l in conocidos.Values) l?.Cerrar();
        conocidos.Clear();
    }

    // Un hilo por colección, bloqueado en ReadFile hasta que llega un reporte o se desconecta el dispositivo.
    sealed class Lector(SafeFileHandle h, int largo, string desc)
    {
        volatile bool cerrado;

        public void Iniciar() => new Thread(Leer) { IsBackground = true, Name = "HID " + desc }.Start();

        void Leer()
        {
            var buf = new byte[largo];
            while (true)
            {
                if (!ReadFile(h, buf, (uint)buf.Length, out uint leidos, IntPtr.Zero))
                {
                    int e = Marshal.GetLastWin32Error();
                    if (!cerrado) Log.Escribir("HID", $"deja de leer {desc} (error {e})");
                    return;
                }
                Log.Escribir("HID", $"{Log.Hex(buf, 0, (int)leidos)} ← {desc}");
            }
        }

        public void Cerrar()
        {
            cerrado = true;
            h.Dispose();
        }
    }
}
