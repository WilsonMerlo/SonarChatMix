namespace SonarChatMix;

// Ventanita de opciones (clic derecho en la bandeja → Opciones), al estilo de la de Lightshot.
sealed class OpcionesForm : Form
{
    public Ajustes Ajustes { get; }
    public bool EcoActivo => chkEco.Checked;
    public bool Aceptado { get; private set; }

    readonly CheckBox chkEco = new() { Text = "Arreglo de eco de Discord", AutoSize = true };
    readonly NumericUpDown numChatMix = new() { Minimum = 0.05m, Maximum = 0.5m, Increment = 0.05m, DecimalPlaces = 2, Width = 70 };
    readonly NumericUpDown numVolumen = new() { Minimum = 1, Maximum = 25, Width = 70 };
    readonly CheckBox chkEscritorio = new() { Text = "Grabar siempre el escritorio", AutoSize = true };
    readonly CheckBox chkMic = new() { Text = "Nunca silenciar el micrófono en los clips", AutoSize = true };
    readonly Dictionary<Accion, HotkeyBox> cajas = new();

    public OpcionesForm(Ajustes ajustes, IReadOnlySet<Accion> ocupados, bool ecoActivo, Icon icono)
    {
        Ajustes = ajustes;
        Text = "Opciones";
        Icon = icono;
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 560);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(PestañaGeneral(ecoActivo));
        tabs.TabPages.Add(PestañaAtajos(ocupados));

        // La ventana se abre con Show() (no modal) para que la bandeja siga respondiendo, y en ese modo
        // WinForms no la cierra solo por tener DialogResult: hay que cerrarla a mano.
        var aceptar = new Button { Text = "Aceptar", Width = 85 };
        var cancelar = new Button { Text = "Cancelar", Width = 85 };
        aceptar.Click += (_, _) => { Volcar(); Aceptado = true; Close(); };
        cancelar.Click += (_, _) => Close();
        var botones = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft,
            Height = 42, Padding = new Padding(6, 8, 6, 0),
        };
        botones.Controls.AddRange([cancelar, aceptar]);
        AcceptButton = aceptar;
        CancelButton = cancelar;

        Padding = new Padding(8, 8, 8, 0);
        Controls.Add(tabs);
        Controls.Add(botones);
    }

    TabPage PestañaGeneral(bool ecoActivo)
    {
        chkEco.Checked = ecoActivo;
        numChatMix.Value = (decimal)Ajustes.PasoChatMix;
        numVolumen.Value = (decimal)Math.Round(Ajustes.PasoVolumen * 100);

        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(10),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Fila(Control izq, Control? der = null)
        {
            int fila = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(izq, 0, fila);
            if (der is null) t.SetColumnSpan(izq, 2);
            else t.Controls.Add(der, 1, fila);
        }

        Fila(chkEco);
        Fila(Ayuda("Silencia a Discord en las salidas físicas para que, al compartir pantalla con sonido, " +
                   "tus amigos no se escuchen a sí mismos."));
        var pasos = Etiqueta("Pasos");
        pasos.Font = new Font(Font, FontStyle.Bold);
        pasos.Margin = new Padding(3, 14, 3, 4);
        Fila(pasos);
        Fila(Etiqueta("ChatMix por pulsación (de -1 a 1):"), numChatMix);
        Fila(Etiqueta("Volumen por pulsación (%):"), numVolumen);

        var moments = Etiqueta("SteelSeries Moments");
        moments.Font = new Font(Font, FontStyle.Bold);
        moments.Margin = new Padding(3, 14, 3, 4);
        Fila(moments);
        chkEscritorio.Checked = Ajustes.MomentsEscritorio;
        Fila(chkEscritorio);
        Fila(Ayuda("Si Moments no está grabando el escritorio, lo pone a grabar (revisa cada 30 s). " +
                   "Mientras grabás un juego no lo toca."));
        chkMic.Checked = Ajustes.MomentsMicSiempre;
        Fila(chkMic);
        Fila(Ayuda("Saca el atajo \"Activar silencio del micrófono\" de Moments y, si el micrófono de los clips " +
                   "queda silenciado, lo vuelve a activar."));

        var p = new TabPage("General");
        p.Controls.Add(t);
        return p;
    }

    TabPage PestañaAtajos(IReadOnlySet<Accion> ocupados)
    {
        var t = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Location = new Point(0, 0), Padding = new Padding(6, 4, 6, 4) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var tips = new ToolTip();

        foreach (var (grupo, items) in Acciones.Grupos)
        {
            var titulo = Etiqueta(grupo);
            titulo.Font = new Font(Font, FontStyle.Bold);
            titulo.Margin = new Padding(3, t.RowCount == 0 ? 4 : 12, 3, 4);
            t.Controls.Add(titulo, 0, t.RowCount++);
            t.SetColumnSpan(titulo, 3);

            foreach (var (accion, nombre) in items)
            {
                var caja = new HotkeyBox { Width = 134, Atajo = Ajustes.Atajos.GetValueOrDefault(accion) };
                if (ocupados.Contains(accion))
                {
                    caja.Ocupado = true;
                    tips.SetToolTip(caja, "Otro programa (¿Sonar?) ya usa este atajo. Sacalo de ahí o elegí otro.");
                }
                caja.Cambio += () => SacarDuplicado(caja);
                cajas[accion] = caja;

                var borrar = new Button
                {
                    Text = "×", Width = 24, Height = caja.Height, FlatStyle = FlatStyle.Flat,
                    ForeColor = SystemColors.GrayText, Margin = new Padding(0, 3, 3, 3), TabStop = false,
                };
                borrar.FlatAppearance.BorderSize = 0;
                borrar.Click += (_, _) => caja.Atajo = null;
                tips.SetToolTip(borrar, "Quitar atajo");

                int fila = t.RowCount++;
                t.Controls.Add(Etiqueta(nombre), 0, fila);
                t.Controls.Add(caja, 1, fila);
                t.Controls.Add(borrar, 2, fila);
            }
        }

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(t);

        var ayuda = Ayuda("Hacé clic en una casilla y apretá la combinación. Retroceso o Supr la borra. " +
                          "Las casillas en rojo están tomadas por otro programa.");
        ayuda.Dock = DockStyle.Bottom;

        var p = new TabPage("Atajos");
        p.Controls.Add(scroll);
        p.Controls.Add(ayuda);
        return p;
    }

    // Un mismo atajo no puede hacer dos cosas: si lo asignás acá, se lo saco a la otra acción.
    void SacarDuplicado(HotkeyBox cambiada)
    {
        if (cambiada.Atajo is null) return;
        foreach (var otra in cajas.Values)
            if (otra != cambiada && otra.Atajo == cambiada.Atajo) otra.Atajo = null;
    }

    void Volcar()
    {
        Ajustes.Atajos = cajas.Where(c => c.Value.Atajo is not null).ToDictionary(c => c.Key, c => c.Value.Atajo!);
        Ajustes.PasoChatMix = (double)numChatMix.Value;
        Ajustes.PasoVolumen = (double)numVolumen.Value / 100;
        Ajustes.MomentsEscritorio = chkEscritorio.Checked;
        Ajustes.MomentsMicSiempre = chkMic.Checked;
    }

    static Label Etiqueta(string texto) => new()
    {
        Text = texto, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 6),
    };

    static Label Ayuda(string texto) => new()
    {
        Text = texto, AutoSize = true, MaximumSize = new Size(390, 0),
        ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 6), Padding = new Padding(4, 4, 4, 4),
    };
}

// Casilla que "graba" la combinación que apretás.
sealed class HotkeyBox : TextBox
{
    Atajo? atajo;
    bool ocupado;

    public event Action? Cambio;

    public HotkeyBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        Cursor = Cursors.Hand;
        TextAlign = HorizontalAlignment.Center;
        Pintar();
    }

    public Atajo? Atajo
    {
        get => atajo;
        set
        {
            if (atajo == value) return;
            atajo = value;
            ocupado = false; // al cambiarlo deja de estar marcado como tomado
            Pintar();
            Cambio?.Invoke();
        }
    }

    public bool Ocupado { set { ocupado = value; Pintar(); } }

    void Pintar()
    {
        Text = atajo?.ToString() ?? "Ninguno";
        ForeColor = atajo is null ? SystemColors.GrayText : SystemColors.WindowText;
        BackColor = ocupado ? Color.FromArgb(255, 220, 220) : SystemColors.Window;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        Keys mods = keyData & Keys.Modifiers;

        // Esc y Tab siguen funcionando normal (cerrar / pasar a la siguiente casilla).
        if (mods == Keys.None && key is Keys.Escape or Keys.Tab) return base.ProcessCmdKey(ref msg, keyData);
        // Solo un modificador apretado: todavía no hay combinación.
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return true;

        if (mods == Keys.None && key is Keys.Back or Keys.Delete) Atajo = null;
        else
        {
            Mod m = Mod.None;
            if (mods.HasFlag(Keys.Control)) m |= Mod.Ctrl;
            if (mods.HasFlag(Keys.Alt)) m |= Mod.Alt;
            if (mods.HasFlag(Keys.Shift)) m |= Mod.Shift;
            Atajo = new Atajo(m, key);
        }
        return true;
    }
}
