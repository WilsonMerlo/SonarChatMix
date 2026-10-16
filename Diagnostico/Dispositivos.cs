using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DiagnosticoDial;

// Nombres y rutas de dispositivos sacados de la base de PnP de Windows (no le pregunta nada al dispositivo).
static class Dispositivos
{
    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
        public DEVPROPKEY(string g, uint p) { fmtid = new Guid(g); pid = p; }
    }

    static readonly DEVPROPKEY InstanceId = new("78c34fc8-104a-4aca-9ea4-524d52996e57", 256);
    static readonly DEVPROPKEY BusReportedDeviceDesc = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);
    static readonly DEVPROPKEY FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    static readonly DEVPROPKEY DeviceDesc = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid clase, string? id, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid clase, string? id, char[] buf, uint len, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_PropertyW(string iface, ref DEVPROPKEY key, out uint tipo,
                                                       byte[]? buf, ref uint tam, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint nodo, string id, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_PropertyW(uint nodo, ref DEVPROPKEY key, out uint tipo,
                                               byte[]? buf, ref uint tam, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Parent(out uint padre, uint nodo, uint flags);

    const int CR_SUCCESS = 0, CR_BUFFER_SMALL = 0x1A;
    const uint DEVPROP_TYPE_STRING = 0x12;

    // Rutas de las interfaces presentes de una clase (ej. todas las colecciones HID).
    public static List<string> Interfaces(Guid clase)
    {
        for (int intento = 0; intento < 3; intento++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint largo, ref clase, null, 0) != CR_SUCCESS) return [];
            var buf = new char[largo];
            int cr = CM_Get_Device_Interface_ListW(ref clase, null, buf, largo, 0);
            if (cr == CR_BUFFER_SMALL) continue; // se conectó algo entre las dos llamadas
            if (cr != CR_SUCCESS) return [];
            return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        return [];
    }

    // "\\?\HID#VID_1532&PID_0A49&MI_03&Col02#8&...#{guid}" → "HID\VID_1532&PID_0A49&MI_03&COL02\8&..."
    public static string InstanciaDe(string ruta)
    {
        ruta = ruta.Replace(@"\??\", @"\\?\");
        var clave = InstanceId;
        uint tam = 0;
        if (CM_Get_Device_Interface_PropertyW(ruta, ref clave, out _, null, ref tam, 0) == CR_BUFFER_SMALL)
        {
            var buf = new byte[tam];
            if (CM_Get_Device_Interface_PropertyW(ruta, ref clave, out uint tipo, buf, ref tam, 0) == CR_SUCCESS
                && tipo == DEVPROP_TYPE_STRING)
                return Texto(buf, tam);
        }

        string r = ruta.StartsWith(@"\\?\") ? ruta[4..] : ruta;
        int g = r.LastIndexOf("#{", StringComparison.Ordinal);
        return (g >= 0 ? r[..g] : r).Replace('#', '\\');
    }

    // Nombre del producto tal como lo reporta por USB (ej. "BlackShark V2 Pro Xbox 2.4") + descripción de Windows.
    public static string NombreDe(string ruta)
    {
        if (CM_Locate_DevNodeW(out uint nodo, InstanciaDe(ruta), 0) != CR_SUCCESS) return "?";
        // Las colecciones HID suelen tener nombres genéricos; el del producto está en el nodo o en su padre.
        string? producto = Propiedad(nodo, BusReportedDeviceDesc);
        if (producto is null && CM_Get_Parent(out uint padre, nodo, 0) == CR_SUCCESS)
            producto = Propiedad(padre, BusReportedDeviceDesc);
        string? desc = Propiedad(nodo, FriendlyName) ?? Propiedad(nodo, DeviceDesc);
        return producto is null ? desc ?? "?" : desc is null ? producto : $"{producto} / {desc}";
    }

    public static string VidPid(string ruta)
    {
        var m = Regex.Match(ruta, @"VID_([0-9A-F]{4}).*?PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        return m.Success ? $"VID_{m.Groups[1].Value.ToUpperInvariant()} PID_{m.Groups[2].Value.ToUpperInvariant()}" : "";
    }

    static string? Propiedad(uint nodo, DEVPROPKEY clave)
    {
        uint tam = 0;
        if (CM_Get_DevNode_PropertyW(nodo, ref clave, out _, null, ref tam, 0) != CR_BUFFER_SMALL || tam == 0) return null;
        var buf = new byte[tam];
        if (CM_Get_DevNode_PropertyW(nodo, ref clave, out uint tipo, buf, ref tam, 0) != CR_SUCCESS
            || tipo != DEVPROP_TYPE_STRING) return null;
        string s = Texto(buf, tam);
        return s == "" ? null : s;
    }

    static string Texto(byte[] buf, uint tam) => Encoding.Unicode.GetString(buf, 0, (int)tam).TrimEnd('\0');
}
