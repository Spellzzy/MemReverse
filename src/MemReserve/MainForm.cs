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
    readonly Panel _statusCard;
    readonly Panel _historyCard;
    readonly Panel _settingsCard;
    readonly Panel _optionsCard;
    readonly Panel _accent;
    readonly Label _status;
    readonly Label _hero;
    readonly Label _available;
    readonly Label _detail;
    readonly Label _history;
    readonly Label _hint;
    readonly Button _releaseButton;
    readonly Button _acquireButton;
    readonly Button _launchButton;
    readonly CheckBox _startupCheck;
    readonly CheckBox _hardLimitCheck;
    readonly CheckBox _pauseCheck;
    readonly Label _guardNote;
    readonly ToolStripMenuItem _resumeItem;
    readonly ToolStripMenuItem _endItem;
    readonly LowMemoryWatch _lowMemory = new();
    readonly CushionController _controller;
    readonly bool _startInTray;
    bool _allowShow;
    bool _loading = true;
    bool _exiting;
    bool _hideTipShown;

    public MainForm(bool startInTray = false)
    {
        _startInTray = startInTray;
        Text = "内存预留";
        Font = new Font("Microsoft YaHei UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PageColor;
        Icon = CreateAppIcon();
        if (startInTray)
            ShowInTaskbar = false;

        int totalMb = (int)(NativeMemory.GetTotalPhysical() / (1024 * 1024));
        int half = Math.Max(0, totalMb / 2);
        int cushionMax = AlignMaximum(half, 256);
        int thresholdMax = AlignMaximum(half, 512);
        AppSettings settings = LoadSettings();
        int cushionValue = Snap(settings.CushionMb, 256, cushionMax);
        int thresholdValue = Snap(settings.ReleaseThresholdMb, 512, thresholdMax);
        _controller = new CushionController(cushionValue, thresholdValue);
        if (StartupRegistration.IsEnabled())
        {
            try
            {
                StartupRegistration.SetEnabled(true);
            }
            catch (Exception)
            {
            }
        }

        if (settings.HardLimit && !_controller.SetHardLimit(true, out _))
            settings.HardLimit = false;
        _controller.SetPauseOnThreshold(settings.PauseOnThreshold);

        _cushion = CreateNumber(256, cushionMax, cushionValue);
        _threshold = CreateNumber(512, thresholdMax, thresholdValue);
        _cushion.AccessibleName = "预留";
        _threshold.AccessibleName = "释放阈值";

        const int width = 460;
        _statusCard = CreateCard(0, 0, width, 116);
        _accent = new Panel
        {
            Dock = DockStyle.Left,
            Width = 4,
            BackColor = HoldingColor
        };
        _status = CreateTextLabel(TextColor, 9F, FontStyle.Regular);
        _hero = CreateTextLabel(HoldingColor, 20F, FontStyle.Bold);
        _available = CreateTextLabel(MutedColor, 9F, FontStyle.Regular);
        _status.Bounds = new Rectangle(20, 12, width - 36, 20);
        _hero.Bounds = new Rectangle(18, 32, width - 36, 40);
        _available.Bounds = new Rectangle(20, 76, width - 36, 24);
        _statusCard.Controls.Add(_accent);
        _statusCard.Controls.Add(_status);
        _statusCard.Controls.Add(_hero);
        _statusCard.Controls.Add(_available);
        Controls.Add(_statusCard);

        _detail = CreateTextLabel(MutedColor, 9F, FontStyle.Regular);
        _detail.BackColor = PageColor;
        _detail.AutoEllipsis = true;
        Controls.Add(_detail);

        _historyCard = CreateCard(0, 0, width, 110);
        var historyTitle = CreateTextLabel(TextColor, 9F, FontStyle.Regular);
        historyTitle.Text = "最近放开";
        historyTitle.Bounds = new Rectangle(16, 6, width - 32, 20);
        _history = CreateTextLabel(MutedColor, 9F, FontStyle.Regular);
        _history.TextAlign = ContentAlignment.TopLeft;
        _history.Bounds = new Rectangle(16, 28, width - 32, 74);
        _historyCard.Controls.Add(historyTitle);
        _historyCard.Controls.Add(_history);
        Controls.Add(_historyCard);

        _settingsCard = CreateCard(0, 0, width, 96);
        AddSettingRow(_settingsCard, 12, "预留", _cushion);
        AddSettingRow(_settingsCard, 52, "释放阈值", _threshold);
        Controls.Add(_settingsCard);

        _startupCheck = CreateCheck("开机时启动");
        _hardLimitCheck = CreateCheck("编译硬上限");
        _pauseCheck = CreateCheck("到线时暂停编译");
        _launchButton = CreateButton("启动编译", primary: false);
        _guardNote = CreateTextLabel(MutedColor, 9F, FontStyle.Regular);
        _guardNote.BackColor = PageColor;
        _guardNote.AutoEllipsis = true;
        _optionsCard = CreateCard(0, 0, width, 140);
        _startupCheck.Bounds = new Rectangle(16, 8, width - 32, 24);
        _hardLimitCheck.Bounds = new Rectangle(16, 34, width - 32, 24);
        _pauseCheck.Bounds = new Rectangle(16, 60, width - 32, 24);
        _launchButton.Bounds = new Rectangle(16, 92, width - 32, 32);
        _optionsCard.Controls.Add(_startupCheck);
        _optionsCard.Controls.Add(_hardLimitCheck);
        _optionsCard.Controls.Add(_pauseCheck);
        _optionsCard.Controls.Add(_launchButton);
        Controls.Add(_optionsCard);
        Controls.Add(_guardNote);

        _hint = CreateTextLabel(MutedColor, 9F, FontStyle.Regular);
        _hint.BackColor = PageColor;
        _hint.AutoEllipsis = true;
        _hint.Text = $"释放后需连续 {CushionController.RecoverHoldSeconds} 秒高于「阈值 + 预留 + {CushionController.MarginMb} MB」，才会重新占用。";
        Controls.Add(_hint);

        _releaseButton = CreateButton("立即释放", primary: false);
        _acquireButton = CreateButton("重新预留", primary: true);
        _releaseButton.Click += (_, _) => _controller.ReleaseNow();
        _acquireButton.Click += (_, _) => _controller.AcquireNow();
        Controls.Add(_releaseButton);
        Controls.Add(_acquireButton);

        _controller.StateChanged += (_, _) => ApplyState();
        _controller.ReservationReleased += (_, _) =>
            ShowBalloon("已释放", $"已释放预留内存。剩余 {_controller.AvailableMb} MB", ToolTipIcon.Warning);
        _controller.SliceReleased += (_, _) =>
            ShowBalloon("已放开一部分", $"已放开 256 MB，仍预留 {_controller.HeldMb} MB。剩余 {_controller.AvailableMb} MB", ToolTipIcon.Info);
        _controller.ProcessPaused += (_, name) =>
        {
            ShowBalloon("已暂停", $"{name} 已暂停，可在托盘选择继续或结束。", ToolTipIcon.Warning);
            UpdatePauseMenu();
        };
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
        _resumeItem = new ToolStripMenuItem("继续") { Enabled = false };
        _endItem = new ToolStripMenuItem("结束") { Enabled = false };
        _resumeItem.Click += (_, _) => _controller.ResumePaused();
        _endItem.Click += (_, _) => _controller.KillPaused();
        menu.Items.Add(_resumeItem);
        menu.Items.Add(_endItem);
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => _controller.Tick();

        _cushion.ValueChanged += (_, _) => OnCushionChanged();
        _threshold.ValueChanged += (_, _) => OnThresholdChanged();
        _startupCheck.Checked = StartupRegistration.IsEnabled();
        _hardLimitCheck.Checked = _controller.HardLimit;
        _pauseCheck.Checked = settings.PauseOnThreshold;
        _startupCheck.CheckedChanged += (_, _) => OnStartupChanged();
        _hardLimitCheck.CheckedChanged += (_, _) => OnHardLimitChanged();
        _pauseCheck.CheckedChanged += (_, _) => OnPauseChanged();
        _launchButton.Click += (_, _) => LaunchCompile();
        FormClosing += OnFormClosing;
        _loading = false;
        ApplyState();
    }

    bool _creatingHandle;
    bool _started;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        StartWorking();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (SynchronizationContext.Current != null)
            _controller.UseUiContext(SynchronizationContext.Current);
    }

    void StartWorking()
    {
        if (_started || _exiting)
            return;

        _started = true;
        if (SynchronizationContext.Current != null)
            _controller.UseUiContext(SynchronizationContext.Current);

        _timer.Start();
        _lowMemory.Signaled += () =>
        {
            if (!IsHandleCreated || IsDisposed || _exiting)
                return;

            try
            {
                BeginInvoke(() => _controller.Tick());
            }
            catch (InvalidOperationException)
            {
            }
        };
        _lowMemory.Start();
        _controller.Tick();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (_startInTray && !_allowShow)
        {
            if (!IsHandleCreated && !_creatingHandle)
            {
                _creatingHandle = true;
                CreateHandle();
                _creatingHandle = false;
            }

            value = false;
            ShowInTaskbar = false;
        }

        base.SetVisibleCore(value);
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
        _lowMemory.Dispose();
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
        _history.Text = FormatReleases();
        _guardNote.Text = _controller.GuardNote;
        Arrange();
        bool acquiring = _controller.Phase == CushionPhase.Acquiring;
        _cushion.Enabled = !acquiring;
        _threshold.Enabled = !acquiring;
        _releaseButton.Enabled = _controller.Phase == CushionPhase.Holding;
        _acquireButton.Enabled = !acquiring && _controller.Phase != CushionPhase.Holding;
        _launchButton.Enabled = _hardLimitCheck.Checked;
        UpdatePauseMenu();
        _tray.Text = LimitTip($"{_controller.StatusText}，剩余 {_controller.AvailableMb} MB");
    }

    void Arrange()
    {
        const int pad = 14;
        const int gap = 6;
        int width = _statusCard.Width;
        int y = pad;

        void Place(Control control, int height, int after = gap)
        {
            control.Visible = height > 0;
            if (height <= 0)
                return;

            control.SetBounds(pad, y, width, height);
            y += height + after;
        }

        Place(_statusCard, _statusCard.Height);
        Place(_detail, string.IsNullOrEmpty(_detail.Text) ? 0 : 22, 4);
        Place(_historyCard, _historyCard.Height);
        Place(_settingsCard, _settingsCard.Height);
        Place(_optionsCard, _optionsCard.Height);
        Place(_guardNote, string.IsNullOrEmpty(_guardNote.Text) ? 0 : 22, 4);
        Place(_hint, 22, 8);

        int buttonWidth = (width - 8) / 2;
        _releaseButton.SetBounds(pad, y, buttonWidth, 34);
        _acquireButton.SetBounds(pad + buttonWidth + 8, y, buttonWidth, 34);
        y += _releaseButton.Height + pad;
        ClientSize = new Size(pad * 2 + width, y);
    }

    string FormatReleases()
    {
        IReadOnlyList<ReleaseNote> notes = _controller.RecentReleases;
        if (notes.Count == 0)
            return "还没有放开记录";

        var lines = new string[notes.Count];
        for (int i = 0; i < notes.Count; i++)
        {
            ReleaseNote note = notes[i];
            lines[i] = $"{note.Time:HH:mm:ss}  {note.ProcessName}  剩余 {note.AvailableMb:N0} MB";
        }

        return string.Join(Environment.NewLine, lines);
    }

    void UpdatePauseMenu()
    {
        bool paused = _controller.HasPausedProcess;
        _resumeItem.Enabled = paused;
        _endItem.Enabled = paused;
    }

    void OnStartupChanged()
    {
        if (_loading)
            return;

        try
        {
            StartupRegistration.SetEnabled(_startupCheck.Checked);
        }
        catch (Exception ex)
        {
            _loading = true;
            _startupCheck.Checked = StartupRegistration.IsEnabled();
            _loading = false;
            _guardNote.Text = ex.Message;
        }

        SaveSettings();
    }

    void OnHardLimitChanged()
    {
        if (_loading)
            return;

        if (!_controller.SetHardLimit(_hardLimitCheck.Checked, out string? error))
        {
            _loading = true;
            _hardLimitCheck.Checked = false;
            _loading = false;
            _guardNote.Text = error ?? "无法打开硬上限。";
        }

        SaveSettings();
    }

    void OnPauseChanged()
    {
        if (_loading)
            return;

        _controller.SetPauseOnThreshold(_pauseCheck.Checked);
        SaveSettings();
        UpdatePauseMenu();
    }

    void LaunchCompile()
    {
        using var dialog = new Form
        {
            Text = "启动编译",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(420, 124),
            Font = Font
        };
        var label = new Label { Text = "命令行", AutoSize = true, Location = new Point(16, 16) };
        var box = new TextBox { Location = new Point(16, 40), Width = 388 };
        var ok = new Button { Text = "启动", DialogResult = DialogResult.OK, Location = new Point(236, 80), Width = 80 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(324, 80), Width = 80 };
        dialog.Controls.Add(label);
        dialog.Controls.Add(box);
        dialog.Controls.Add(ok);
        dialog.Controls.Add(cancel);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        if (!_controller.TryLaunchCompile(box.Text, out string? error))
            MessageBox.Show(this, error, "启动编译", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    void ShowWindow()
    {
        _allowShow = true;
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

    static CheckBox CreateCheck(string text)
    {
        return new CheckBox
        {
            Text = text,
            AutoSize = false,
            BackColor = Color.White,
            ForeColor = TextColor,
            Font = new Font("Microsoft YaHei UI", 9F)
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
                ReleaseThresholdMb = _controller.ThresholdMb,
                LaunchAtStartup = _startupCheck.Checked,
                HardLimit = _hardLimitCheck.Checked,
                PauseOnThreshold = _pauseCheck.Checked
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
    public bool LaunchAtStartup { get; set; }
    public bool HardLimit { get; set; }
    public bool PauseOnThreshold { get; set; }
}
