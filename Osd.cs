using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace SonarChatMix;

// Íconos de la fuente de símbolos de Windows (Segoe Fluent Icons / Segoe MDL2 Assets).
static class Glifo
{
    public const string Juego = "", Chat = "", Microfono = "", Volumen = "",
                        Silencio = "", Auriculares = "", Parlantes = "";

    // Qué dibujito le toca a un dispositivo según su nombre.
    public static string DeDispositivo(string nombre)
    {
        string n = nombre.ToLowerInvariant();
        if (n.Contains("micr") || n.Contains("mic ")) return Microfono;
        if (n.Contains("headset") || n.Contains("auricular") || n.Contains("headphone") || n.Contains("arctis")) return Auriculares;
        return Parlantes;
    }
}

// Cartelito que aparece abajo al centro. No roba el foco y los clics lo atraviesan.
sealed class Osd : Form
{
    enum Modo { Texto, ChatMix, Volumen, Dispositivo }

    static readonly Color Fondo = Color.FromArgb(32, 36, 44);
    static readonly Color Pista = Color.FromArgb(62, 68, 80);
    static readonly Color Tenue = Color.FromArgb(150, 156, 168);
    static readonly Color Acento = Color.FromArgb(255, 106, 0); // naranja SteelSeries

    static readonly string FuenteIconos =
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    readonly System.Windows.Forms.Timer timer = new();

    Modo modo;
    string texto = "";
    double valor;
    bool mute;
    string glifo = "", canal = "", claveCanal = "";
    string actual = "", siguiente = "";
    int direccion;

    public Osd()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Fondo;
        Opacity = 0.94;
        DoubleBuffered = true;
        timer.Tick += (_, _) => { timer.Stop(); Hide(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80        // WS_EX_TOOLWINDOW: no aparece en Alt+Tab
                       | 0x08000000   // WS_EX_NOACTIVATE: no roba el foco al juego
                       | 0x20;        // WS_EX_TRANSPARENT: los clics pasan a través
            return cp;
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int redondeado = 2; // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND (Windows 11)
        DwmSetWindowAttribute(Handle, 33, ref redondeado, sizeof(int));
    }

    // ¿Está a la vista el carrusel de este canal? (si sí, la próxima pulsación cambia de dispositivo)
    public bool MostrandoDispositivo(string clave) => Visible && modo == Modo.Dispositivo && claveCanal == clave;

    public void Mostrar(double v)
    {
        modo = Modo.ChatMix;
        valor = v;
        Abrir(420, 84, Config.OsdMs);
    }

    public void MostrarVolumen(string glifoCanal, string nombre, double v, bool silenciado)
    {
        modo = Modo.Volumen;
        glifo = silenciado ? Glifo.Silencio : glifoCanal;
        canal = nombre;
        valor = v;
        mute = silenciado;
        Abrir(420, 84, Config.OsdMs);
    }

    // siguienteDisp = null si hay un solo dispositivo. direccion: +1 siguiente, -1 anterior.
    public void MostrarDispositivo(string clave, string glifoCanal, string nombre,
                                   string actualDisp, string? siguienteDisp, int dir)
    {
        modo = Modo.Dispositivo;
        claveCanal = clave;
        glifo = glifoCanal;
        canal = nombre;
        actual = actualDisp;
        siguiente = siguienteDisp ?? "";
        direccion = dir;
        Abrir(620, 112, Config.OsdDispositivoMs);
    }

    public void MostrarTexto(string t)
    {
        modo = Modo.Texto;
        texto = t;
        Abrir(420, 72, Config.OsdMs);
    }

    void Abrir(int ancho, int alto, int ms)
    {
        float k = DeviceDpi / 96f;
        Size = new Size((int)(ancho * k), (int)(alto * k));
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - (int)(80 * k));
        Invalidate();
        if (!Visible) Show();
        timer.Stop();
        timer.Interval = ms;
        timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float k = DeviceDpi / 96f;
        g.ScaleTransform(k, k);
        float w = Width / k, h = Height / k;

        switch (modo)
        {
            case Modo.Texto:
                using (var f = new Font("Segoe UI", 11f, FontStyle.Bold))
                    Texto(g, texto, f, Color.White, new RectangleF(12, 0, w - 24, h), StringAlignment.Center);
                break;

            case Modo.ChatMix:
                PintarChatMix(g, w, h);
                break;

            case Modo.Volumen:
                PintarVolumen(g, w, h);
                break;

            case Modo.Dispositivo:
                PintarDispositivo(g, w, h);
                break;
        }
    }

    void PintarChatMix(Graphics g, float w, float h)
    {
        // [icono juego] ────●──── [icono chat]
        //            más chat (+0.3)
        Icono(g, Glifo.Juego, 22, valor < -0.001 ? Acento : Color.White, new RectangleF(14, 10, 44, 40));
        Icono(g, Glifo.Chat, 22, valor > 0.001 ? Acento : Color.White, new RectangleF(w - 58, 10, 44, 40));

        float x0 = 70, x1 = w - 70, y = 30;
        using (var pista = new Pen(Pista, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pista, x0, y, x1, y);
        float medio = (x0 + x1) / 2, perilla = medio + (float)valor * (x1 - x0) / 2;
        using (var activo = new Pen(Acento, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(activo, medio, y, perilla, y);
        using (var marca = new Pen(Tenue, 1.5f)) g.DrawLine(marca, medio, y - 7, medio, y + 7);
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, perilla - 8, y - 8, 16, 16);

        string estado = Math.Abs(valor) < 0.001 ? "Centrado"
                      : valor < 0 ? $"Más juego ({valor:0.0})"
                      : $"Más chat (+{valor:0.0})";
        using var f = new Font("Segoe UI", 10f, FontStyle.Bold);
        Texto(g, estado, f, Color.White, new RectangleF(0, 50, w, 26), StringAlignment.Center);
    }

    void PintarVolumen(Graphics g, float w, float h)
    {
        // [icono]  MAESTRO                        45%
        //          ████████████░░░░░░░░░░░░░░
        Icono(g, glifo, 24, mute ? Tenue : Acento, new RectangleF(14, 0, 48, h));

        using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
            Texto(g, canal.ToUpperInvariant(), f, Tenue, new RectangleF(70, 16, 200, 20), StringAlignment.Near);
        using (var f = new Font("Segoe UI", 11f, FontStyle.Bold))
            Texto(g, mute ? "Silenciado" : $"{valor * 100:0}%", f, Color.White,
                  new RectangleF(w - 130, 14, 110, 24), StringAlignment.Far);

        float x0 = 72, x1 = w - 22, y = 54;
        using (var pista = new Pen(Pista, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pista, x0, y, x1, y);
        if (valor > 0.001)
            using (var activo = new Pen(mute ? Tenue : Acento, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(activo, x0, y, x0 + (float)valor * (x1 - x0), y);
    }

    void PintarDispositivo(Graphics g, float w, float h)
    {
        // ┌────────┬──────────────────────────┬───┬────────────────────┐
        // │ [icono]│ [🎧] G433 Gaming Headset  │ › │ [🔊] Realtek Audio │
        // │ JUEGO  │      Altavoces            │   │      Altavoces     │
        // └────────┴──────────────────────────┴───┴────────────────────┘
        //            AHORA                          SI APRETÁS DE NUEVO
        using var chica = new Font("Segoe UI", 7.5f, FontStyle.Bold);

        // Canal
        Icono(g, glifo, 26, Acento, new RectangleF(10, 14, 76, 46));
        Texto(g, canal.ToUpperInvariant(), chica, Color.White, new RectangleF(6, 62, 84, 18), StringAlignment.Center);
        using (var sep = new Pen(Pista, 1)) g.DrawLine(sep, 94, 16, 94, h - 16);

        // Actual (resaltado)
        var cajaActual = new RectangleF(106, 14, 260, h - 28);
        using (var b = new SolidBrush(Color.FromArgb(44, 50, 62))) RellenarRedondeado(g, b, cajaActual, 10);
        using (var borde = new Pen(Acento, 1.5f)) BordeRedondeado(g, borde, cajaActual, 10);
        Texto(g, "AHORA", chica, Acento, new RectangleF(cajaActual.X + 12, cajaActual.Y + 6, 120, 14), StringAlignment.Near);
        Tarjeta(g, actual, new RectangleF(cajaActual.X + 8, cajaActual.Y + 20, cajaActual.Width - 16, cajaActual.Height - 24),
                Color.White, 11f, 22);

        // Siguiente (atenuado), del lado hacia donde vas
        var cajaSig = new RectangleF(398, 14, w - 398 - 10, h - 28);
        if (siguiente == "")
        {
            using var f = new Font("Segoe UI", 9f, FontStyle.Italic);
            Texto(g, "Único dispositivo", f, Tenue, cajaSig, StringAlignment.Center);
            return;
        }
        Icono(g, direccion < 0 ? "" : "", 14, Tenue, new RectangleF(368, 0, 30, h)); // ‹ o ›
        Texto(g, direccion < 0 ? "ANTERIOR" : "SIGUIENTE", chica, Tenue,
              new RectangleF(cajaSig.X + 4, cajaSig.Y + 6, 140, 14), StringAlignment.Near);
        Tarjeta(g, siguiente, new RectangleF(cajaSig.X, cajaSig.Y + 20, cajaSig.Width, cajaSig.Height - 24),
                Tenue, 9.5f, 18);
    }

    // "Altavoces (G433 Gaming Headset)" → título "G433 Gaming Headset", subtítulo "Altavoces".
    void Tarjeta(Graphics g, string dispositivo, RectangleF r, Color color, float tam, float tamIcono)
    {
        string titulo = dispositivo, sub = "";
        int p = dispositivo.IndexOf(" (", StringComparison.Ordinal);
        if (p > 0 && dispositivo.EndsWith(')'))
        {
            sub = dispositivo[..p];
            titulo = dispositivo[(p + 2)..^1];
        }

        Icono(g, Glifo.DeDispositivo(dispositivo), tamIcono * 0.75f, color, new RectangleF(r.X, r.Y, tamIcono + 10, r.Height));
        float tx = r.X + tamIcono + 12, tw = r.Right - tx;
        using var fTitulo = new Font("Segoe UI", tam, FontStyle.Bold);
        using var fSub = new Font("Segoe UI", tam * 0.75f);
        if (sub == "")
            Texto(g, titulo, fTitulo, color, new RectangleF(tx, r.Y, tw, r.Height), StringAlignment.Near);
        else
        {
            Texto(g, titulo, fTitulo, color, new RectangleF(tx, r.Y, tw, r.Height * 0.6f), StringAlignment.Near, abajo: true);
            Texto(g, sub, fSub, Tenue, new RectangleF(tx, r.Y + r.Height * 0.6f, tw, r.Height * 0.4f), StringAlignment.Near, arriba: true);
        }
    }

    void Icono(Graphics g, string glifo, float tam, Color color, RectangleF r)
    {
        using var f = new Font(FuenteIconos, tam, GraphicsUnit.Pixel);
        Texto(g, glifo, f, color, r, StringAlignment.Center);
    }

    static void Texto(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h,
                      bool arriba = false, bool abajo = false)
    {
        using var b = new SolidBrush(c);
        using var fmt = new StringFormat
        {
            Alignment = h,
            LineAlignment = arriba ? StringAlignment.Near : abajo ? StringAlignment.Far : StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        if (!s.Contains('\n')) fmt.FormatFlags |= StringFormatFlags.NoWrap;
        g.DrawString(s, f, b, r, fmt);
    }

    static GraphicsPath Redondeado(RectangleF r, float radio)
    {
        var p = new GraphicsPath();
        float d = radio * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void RellenarRedondeado(Graphics g, Brush b, RectangleF r, float radio)
    {
        using var p = Redondeado(r, radio);
        g.FillPath(b, p);
    }

    static void BordeRedondeado(Graphics g, Pen pen, RectangleF r, float radio)
    {
        using var p = Redondeado(r, radio);
        g.DrawPath(pen, p);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
