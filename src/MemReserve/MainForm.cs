using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MemReserve;

internal sealed class MainForm : Form
{
    static readonly Color PageColor = Color.FromArgb(245, 246, 248);
    static readonly Color CardColor = Color.White;
    static readonly Color BorderColor = Color.FromArgb(226, 228, 232);
    static readonly Color TextColor = Color.FromArgb(17, 24, 39);
    static readonly Color MutedColor = Color.FromArgb(107, 114, 128);
    static readonly Color HoldingColor = Color.FromArgb(22, 128, 74);
    static readonly Color ReleasedColor = Color.FromArgb(180, 83, 9);
    static readonly Color WaitingColor = Color.FromArgb(75, 85, 99);
    static readonly Color AcquiringColor = Color.FromArgb(29, 78, 216);
    static readonly Color FailedColor = Color.FromArgb(185, 28, 28);
    static readonly Color ButtonColor = Color.FromArgb(31, 41, 55);

    readonly System.Windows.Forms.Timer _timer;
    readonly NotifyIcon _tray;
    readonly NumericUpDown _cushion;
    readonly NumericUpDown _threshold;
    readonly Panel _accent;
    readonly Label _status;
    readonly Label _hero;
    readonly Label _available;
    readonly Label _detail;
    readonly Button _releaseButton;
    readonly Button _acquireButton;
    readonly CushionController _controller;
    bool _loading = true;
    bool _exiting;
    bool _hideTipShown;

    public MainForm()
    {
        Text = "内存预留";
        Font = new Font("Microsoft YaHei UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PageColor;
        Icon = CreateAppIcon();

        int totalMb = (int)(NativeMemory.GetTotalPhysical() / (1024 * 1024));
        int half = Math.Max(0, totalMb / 2);
        int cushionMax = AlignMaximum(half, 256);
        int thresholdMax = AlignMaximum(half, 512);
        AppSettings settings = LoadSettings();
        int cushionValue = Snap(settings.CushionMb, 256, cushionMax);
        int thresholdValue = Snap(settings.ReleaseThresholdMb, 512, thresholdMax);
        _controller = new CushionController(cushionValue, thresholdValue);

        _cushion = CreateNumber(256, cushionMax, cushionValue);
        _threshold = CreateNumber(512, thresholdMax, thresholdValue);
        _cushion.AccessibleName = "预留";
        _threshold.AccessibleName = "释放阈值";

        const int pad = 16;
        const int width = 384;
        int y = pad;

        var statusCard = CreateCard(pad, y, width, 124);
        _accent = new Panel
        {
            Dock = DockStyle.Left,
            Width = 4,
            BackColor = HoldingColor
        };
        _status = CreateTextLabel(TextColor, 9F, FontStyle.Regular);
        _hero = CreateTextLabel(HoldingColor, 20F, FontStyle.Bold);
        _available = CreateTextLabel(MutedColor, 9F, FontStyle.Regular);
        _status.Bounds = new Rectangle(20, 16, width - 36, 22);
        _hero.Bounds = new Rectangle(18, 40, width - 36, 42);
        _available.Bounds = new Rectangle(20, 86, width - 36, 24);
        statusCard.Controls.Add(_accent);
        statusCard.Controls.Add(_status);
        statusCard.Controls.Add(_hero);
        statusCard.Controls.Add(_available);
        Controls.Add(statusCard);
        y += statusCard.Height + 8;

        _detail = CreateTextLabel(MutedColor, 9F, FontStyle.Regular);
        _detail.BackColor = PageColor;
        _detail.Bounds = new Rectangle(pad, y, width, 40);
        Controls.Add(_detail);
        y += _detail.Height + 8;

        var settingsCard = CreateCard(pad, y, width, 104);
        AddSettingRow(settingsCard, 16, "预留", _cushion);
        AddSettingRow(settingsCard, 58, "释放阈值", _threshold);
        Controls.Add(settingsCard);
        y += settingsCard.Height + 12;

        var hint = CreateTextLabel(MutedColor, 9F, FontStyle.Regular);
        hint.BackColor = PageColor;
        hint.Bounds = new Rectangle(pad, y, width, 40);
        hint.Text = $"释放后需连续 {CushionController.RecoverHoldSeconds} 秒高于「阈值 + 预留 + {CushionController.MarginMb} MB」，{Environment.NewLine}才会重新占用。";
        Controls.Add(hint);
        y += hint.Height + 12;

        int buttonWidth = (width - 8) / 2;
        _releaseButton = CreateButton("立即释放", primary: false);
        _acquireButton = CreateButton("重新预留", primary: true);
        _releaseButton.Bounds = new Rectangle(pad, y, buttonWidth, 36);
        _acquireButton.Bounds = new Rectangle(pad + buttonWidth + 8, y, buttonWidth, 36);
        _releaseButton.Click += (_, _) => _controller.ReleaseNow();
        _acquireButton.Click += (_, _) => _controller.AcquireNow();
        Controls.Add(_releaseButton);
        Controls.Add(_acquireButton);
        y += _releaseButton.Height + pad;

        ClientSize = new Size(pad * 2 + width, y);

        _controller.StateChanged += (_, _) => ApplyState();
        _controller.ReservationReleased += (_, _) =>
            ShowBalloon("已释放", $"已释放预留内存。剩余 {_controller.AvailableMb} MB", ToolTipIcon.Warning);
        _controller.ReservationFailed += (_, message) =>
            ShowBalloon("占用失败", $"{message} 剩余 {_controller.AvailableMb} MB", ToolTipIcon.Error);

        _tray = new NotifyIcon
        {
            Icon = (Icon)Icon.Clone(),
            Visible = true,
            Text = "内存预留"
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示", null, (_, _) => ShowWindow());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => _controller.Tick();

        _cushion.ValueChanged += (_, _) => OnCushionChanged();
        _threshold.ValueChanged += (_, _) => OnThresholdChanged();
        FormClosing += OnFormClosing;
        _loading = false;
        ApplyState();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var context = SynchronizationContext.Current;
        if (context != null)
            _controller.UseUiContext(context);

        _timer.Start();
        _controller.Tick();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_exiting && WindowState == FormWindowState.Minimized)
            HideToTray();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    void OnCushionChanged()
    {
        if (_loading)
            return;

        int requested = (int)_cushion.Value;
        if (!_controller.TrySetCushionMb(requested))
        {
            _loading = true;
            _cushion.Value = _controller.CushionMb;
            _loading = false;
            return;
        }

        SaveSettings();
    }

    void OnThresholdChanged()
    {
        if (_loading)
            return;

        _controller.SetThresholdMb((int)_threshold.Value);
        SaveSettings();
    }

    void ApplyState()
    {
        bool failed = _controller.StatusText.StartsWith("失败");
        Color accent = _controller.Phase switch
        {
            CushionPhase.Holding => HoldingColor,
            CushionPhase.Acquiring => AcquiringColor,
            CushionPhase.Waiting when failed => FailedColor,
            CushionPhase.Waiting when _controller.StatusText == "已释放" => ReleasedColor,
            _ => WaitingColor
        };

        _accent.BackColor = accent;
        _hero.ForeColor = accent;
        switch (_controller.Phase)
        {
            case CushionPhase.Holding:
                _status.Text = "已预留";
                _hero.Text = $"{_controller.HeldMb:N0} MB";
                _available.Text = $"剩余 {_controller.AvailableMb:N0} MB";
                break;
            case CushionPhase.Acquiring:
                _status.Text = "正在占用";
                _hero.Text = $"{_controller.CushionMb:N0} MB";
                _available.Text = $"剩余 {_controller.AvailableMb:N0} MB";
                break;
            default:
                _status.Text = failed ? "占用失败" : _controller.StatusText;
                _hero.Text = $"{_controller.AvailableMb:N0} MB";
                _available.Text = "当前剩余";
                break;
        }

        _detail.Text = _controller.DetailText;
        bool acquiring = _controller.Phase == CushionPhase.Acquiring;
        _cushion.Enabled = !acquiring;
        _threshold.Enabled = !acquiring;
        _releaseButton.Enabled = _controller.Phase == CushionPhase.Holding;
        _acquireButton.Enabled = !acquiring && _controller.Phase != CushionPhase.Holding;
        _tray.Text = LimitTip($"{_controller.StatusText}，剩余 {_controller.AvailableMb} MB");
    }

    void ShowWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        WindowState = FormWindowState.Normal;
        if (_hideTipShown)
            return;

        _hideTipShown = true;
        string text = _controller.Phase == CushionPhase.Holding
            ? "内存仍被预留，右键托盘可退出。"
            : "程序仍在托盘运行，右键托盘可退出。";
        ShowBalloon("内存预留", text, ToolTipIcon.Info);
    }

    void ExitApp()
    {
        if (_exiting)
            return;

        _exiting = true;
        Close();
    }

    void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _exiting = true;
        _timer.Stop();
        SaveSettings();
        _controller.Shutdown();
        _tray.Visible = false;
    }

    void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        _tray.ShowBalloonTip(3000, title, text, icon);
    }

    static Panel CreateCard(int x, int y, int width, int height)
    {
        var card = new Panel
        {
            Location = new Point(x, y),
            Size = new Size(width, height),
            BackColor = CardColor
        };
        card.Paint += (_, e) =>
        {
            var rect = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawRectangle(pen, rect);
        };
        return card;
    }

    static void AddSettingRow(Panel parent, int top, string caption, NumericUpDown number)
    {
        var label = CreateTextLabel(TextColor, 9F, FontStyle.Regular);
        label.Text = caption;
        label.Bounds = new Rectangle(16, top, 88, 28);
        label.TextAlign = ContentAlignment.MiddleLeft;
        number.Location = new Point(112, top + 2);
        number.Width = 148;
        var unit = CreateTextLabel(MutedColor, 9F, FontStyle.Regular);
        unit.Text = "MB";
        unit.Bounds = new Rectangle(268, top, 48, 28);
        unit.TextAlign = ContentAlignment.MiddleLeft;
        parent.Controls.Add(label);
        parent.Controls.Add(number);
        parent.Controls.Add(unit);
    }

    static Label CreateTextLabel(Color color, float size, FontStyle style)
    {
        return new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = color,
            Font = new Font("Microsoft YaHei UI", size, style),
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    static NumericUpDown CreateNumber(int minimum, int maximum, int value)
    {
        var number = new NumericUpDown
        {
            Width = 148,
            Font = new Font("Microsoft YaHei UI", 10F),
            ThousandsSeparator = true,
            TextAlign = HorizontalAlignment.Right,
            Increment = 256,
            BorderStyle = BorderStyle.FixedSingle
        };
        int upper = Math.Max(minimum, maximum);
        number.Maximum = upper;
        number.Value = upper;
        number.Minimum = minimum;
        number.Value = Math.Clamp(value, minimum, upper);
        return number;
    }

    static Button CreateButton(string text, bool primary)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft YaHei UI", 9F),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 1;
        BindButtonFace(button, primary);
        return button;
    }

    static void BindButtonFace(Button button, bool primary)
    {
        void Apply()
        {
            if (!button.Enabled)
            {
                button.BackColor = Color.FromArgb(243, 244, 246);
                button.ForeColor = Color.FromArgb(156, 163, 175);
                button.FlatAppearance.BorderColor = BorderColor;
                return;
            }

            if (primary)
            {
                button.BackColor = ButtonColor;
                button.ForeColor = Color.White;
                button.FlatAppearance.BorderColor = ButtonColor;
                return;
            }

            button.BackColor = CardColor;
            button.ForeColor = TextColor;
            button.FlatAppearance.BorderColor = Color.FromArgb(209, 213, 219);
        }

        button.EnabledChanged += (_, _) => Apply();
        Apply();
    }

    static Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var path = RoundedRect(new Rectangle(1, 1, 30, 30), 7);
            using var brush = new SolidBrush(HoldingColor);
            graphics.FillPath(brush, path);
            using var bar = new SolidBrush(Color.White);
            graphics.FillRectangle(bar, 8, 9, 16, 3);
            graphics.FillRectangle(bar, 8, 15, 11, 3);
            graphics.FillRectangle(bar, 8, 21, 16, 3);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    static int AlignMaximum(int halfOfRamMb, int minimum)
    {
        int capped = Math.Min(32768, halfOfRamMb);
        if (capped < minimum)
            capped = minimum;

        int snapped = minimum + ((capped - minimum) / 256) * 256;
        return Math.Max(minimum, snapped);
    }

    static int Snap(int value, int minimum, int maximum)
    {
        if (value < minimum)
            value = minimum;
        if (value > maximum)
            value = maximum;

        int snapped = minimum + ((value - minimum) / 256) * 256;
        return Math.Min(snapped, maximum);
    }

    static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MemReserve",
        "settings.json");

    static AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    void SaveSettings()
    {
        try
        {
            string? directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var settings = new AppSettings
            {
                CushionMb = _controller.CushionMb,
                ReleaseThresholdMb = _controller.ThresholdMb
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // 设置写失败不影响预留本身。
        }
    }

    static string LimitTip(string text)
    {
        const int max = 63;
        return text.Length <= max ? text : text[..max];
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr hIcon);
}

sealed class AppSettings
{
    public int CushionMb { get; set; } = 2048;
    public int ReleaseThresholdMb { get; set; } = 2048;
}
