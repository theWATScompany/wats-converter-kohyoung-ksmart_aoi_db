using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Virinco.WATS.Converter.KohYoung
{
    public class MainForm : Form
    {
        private const string AppVersion = "1.0.0";
        private const string AppTitle = "Koh Young KSMART AOI DB Converter";

        // -- WATS Dark Theme Colors --
        private static readonly Color C_BgMain = Color.FromArgb(0x25, 0x25, 0x26);
        private static readonly Color C_BgSide = Color.FromArgb(0x1e, 0x1e, 0x1e);
        private static readonly Color C_BgPanel = Color.FromArgb(0x2d, 0x2d, 0x30);
        private static readonly Color C_BgInput = Color.FromArgb(0x3c, 0x3c, 0x3c);
        private static readonly Color C_TextMain = Color.FromArgb(0xd4, 0xd4, 0xd4);
        private static readonly Color C_TextMuted = Color.FromArgb(0x85, 0x85, 0x85);
        private static readonly Color C_Accent = Color.FromArgb(0xe8, 0xa0, 0x20);
        private static readonly Color C_Success = Color.FromArgb(0x4e, 0xc9, 0x4e);
        private static readonly Color C_Error = Color.FromArgb(0xf4, 0x47, 0x47);
        private static readonly Color C_Border = Color.FromArgb(0x3d, 0x3d, 0x3d);
        private static readonly Color C_BtnBg = Color.FromArgb(0x37, 0x37, 0x38);
        private static readonly Color C_NavHover = Color.FromArgb(0x2a, 0x2d, 0x2e);
        private static readonly Color C_NavActive = Color.FromArgb(0x37, 0x37, 0x38);

        // -- Win32 dark title bar --
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        // -- State --
        private AppConfig _config;
        private DatabasePoller? _poller;
        private CancellationTokenSource? _cts;
        private int _totalImported;
        private int _sessionImported;
        private int _sessionFailed;
        private DateTime _sessionStart;
        private System.Windows.Forms.Timer _dashboardTimer;

        // -- Navigation --
        private Label _lblNavDashboard = null!;
        private Label _lblNavConsole = null!;
        private Panel _pnlDashboard = null!;
        private Panel _pnlConsole = null!;

        // -- Dashboard controls --
        private Button _btnStart = null!;
        private Button _btnStop = null!;
        private Label _lblStatus = null!;
        private Label _lblSource = null!;
        private Label _lblDest = null!;
        private Label _lblConnStatus = null!;
        private Label _lblCurrentIndex = null!;
        private Label _lblSessionDuration = null!;
        private Label _lblTotalUploaded = null!;
        private Label _lblSessionUploaded = null!;
        private Label _lblFailed = null!;
        private Label _lblRate = null!;
        private NumericUpDown _nudStartAtId = null!;
        private Button _btnSetIndex = null!;
        private Button _btnTestConnection = null!;
        private Button _btnListDatabases = null!;

        // -- Console controls --
        private RichTextBox _txtLog = null!;
        private Button _btnClearLog = null!;
        private CheckBox _chkVerbose = null!;
        private CheckBox _chkStepDetails = null!;
        private CheckBox _chkMeasurements = null!;
        private CheckBox _chkSkipped = null!;
        private CheckBox _chkBatchSummary = null!;

        // -- Icons --
        private Icon? _iconOffline;
        private Icon? _iconOnline;

        public MainForm()
        {
            _config = AppConfig.Load();
            _dashboardTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _dashboardTimer.Tick += DashboardTimer_Tick;

            var offPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WBoffline.ico");
            var onPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WBonline.ico");
            if (File.Exists(offPath)) _iconOffline = new Icon(offPath);
            if (File.Exists(onPath)) _iconOnline = new Icon(onPath);

            InitializeComponent();
            UpdateConnectionDisplay();
            ShowPage("dashboard");
            Icon = _iconOnline ?? Icon;
            Shown += MainForm_Shown;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int v = 1; DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)); } catch { }
        }

        // ================================================================
        // Layout
        // ================================================================
        private void InitializeComponent()
        {
            Text = AppTitle;
            Size = new Size(720, 600);
            MinimumSize = new Size(600, 500);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = C_BgMain;
            ForeColor = C_TextMain;
            Font = new Font("Segoe UI", 9f);

            // Menu
            var menu = BuildMenuStrip();
            MainMenuStrip = menu;

            // Main panel (Dock=Fill, added first = back z-order)
            var pnlMain = new Panel { Dock = DockStyle.Fill, BackColor = C_BgMain };

            // Content area
            var pnlContent = new Panel { Dock = DockStyle.Fill, BackColor = C_BgMain };
            pnlMain.Controls.Add(pnlContent);
            // Sidebar
            pnlMain.Controls.Add(BuildSidebar());

            Controls.Add(pnlMain);
            Controls.Add(menu);

            // Content: banner + pages
            var pnlPageHolder = new Panel { Dock = DockStyle.Fill, BackColor = C_BgMain };
            var bannerSep = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = C_Border };
            var banner = BuildConnectionBanner();

            pnlContent.Controls.Add(pnlPageHolder);
            pnlContent.Controls.Add(bannerSep);
            pnlContent.Controls.Add(banner);

            // Pages
            _pnlDashboard = BuildDashboardPage();
            _pnlConsole = BuildConsolePage();
            _pnlDashboard.Dock = DockStyle.Fill;
            _pnlConsole.Dock = DockStyle.Fill;
            pnlPageHolder.Controls.Add(_pnlDashboard);
            pnlPageHolder.Controls.Add(_pnlConsole);
        }

        private MenuStrip BuildMenuStrip()
        {
            var ms = new MenuStrip
            {
                BackColor = C_BgSide,
                ForeColor = C_TextMain,
                RenderMode = ToolStripRenderMode.Professional,
                Renderer = new DarkMenuRenderer()
            };

            var fileMenu = new ToolStripMenuItem("File") { ForeColor = C_TextMain };
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("Options...", null, MenuOptions_Click)
            { ShortcutKeys = Keys.Control | Keys.O, ForeColor = C_TextMain });
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("Save Config", null, (s, e) => SaveConfig())
            { ShortcutKeys = Keys.Control | Keys.S, ForeColor = C_TextMain });
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("Exit", null, (s, e) => Close())
            { ShortcutKeys = Keys.Alt | Keys.F4, ForeColor = C_TextMain });

            var helpMenu = new ToolStripMenuItem("Help") { ForeColor = C_TextMain };
            helpMenu.DropDownItems.Add(new ToolStripMenuItem("About...", null, MenuAbout_Click)
            { ForeColor = C_TextMain });

            ms.Items.Add(fileMenu);
            ms.Items.Add(helpMenu);
            return ms;
        }

        private Panel BuildConnectionBanner()
        {
            var pnl = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = C_BgSide };

            // Row 1: SOURCE
            var srcCaption = Lbl("SOURCE:", 8f, C_TextMuted);
            srcCaption.Left = 10; srcCaption.Top = 6;
            pnl.Controls.Add(srcCaption);

            _lblSource = Lbl("Not configured", 9f, C_TextMain);
            _lblSource.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _lblSource.Left = 72; _lblSource.Top = 4;
            _lblSource.AutoSize = true;
            pnl.Controls.Add(_lblSource);

            // Row 2: WATS destination
            var destCaption = Lbl("WATS:", 8f, C_TextMuted);
            destCaption.Left = 10; destCaption.Top = 28;
            pnl.Controls.Add(destCaption);

            _lblDest = Lbl("-", 9f, C_TextMain);
            _lblDest.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _lblDest.Left = 72; _lblDest.Top = 26;
            _lblDest.AutoSize = true;
            pnl.Controls.Add(_lblDest);

            _lblConnStatus = Lbl("Disconnected", 9f, C_TextMuted);
            _lblConnStatus.Top = 15;
            pnl.Controls.Add(_lblConnStatus);
            pnl.Resize += (s, e) => _lblConnStatus.Left = pnl.Width - _lblConnStatus.Width - 12;

            return pnl;
        }

        private Panel BuildSidebar()
        {
            var pnl = new Panel { Width = 200, Dock = DockStyle.Left, BackColor = C_BgSide };

            _lblNavDashboard = BuildNavItem("  \u25A0  Dashboard");
            _lblNavConsole = BuildNavItem("  \u25A0  Console");

            _lblNavDashboard.Click += (s, e) => ShowPage("dashboard");
            _lblNavConsole.Click += (s, e) => ShowPage("console");

            // Version at bottom
            var lblVer = Lbl($"v{AppVersion}", 8f, C_TextMuted);
            lblVer.Dock = DockStyle.Bottom; lblVer.Height = 22;
            lblVer.Padding = new Padding(12, 0, 0, 4);

            var lblBrand = Lbl("WATS", 8.5f, C_Accent);
            lblBrand.Dock = DockStyle.Bottom; lblBrand.Height = 24;
            lblBrand.Padding = new Padding(12, 0, 0, 0);

            pnl.Controls.Add(lblVer);
            pnl.Controls.Add(lblBrand);
            pnl.Controls.Add(_lblNavConsole);
            pnl.Controls.Add(_lblNavDashboard);
            pnl.Controls.Add(new Panel { Height = 1, Dock = DockStyle.Top, BackColor = C_Border });

            return pnl;
        }

        private Label BuildNavItem(string text)
        {
            var lbl = new Label
            {
                Text = text,
                Dock = DockStyle.Top,
                Height = 38,
                ForeColor = C_TextMuted,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 10f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            lbl.MouseEnter += (s, e) => { if (lbl.BackColor != C_NavActive) lbl.BackColor = C_NavHover; };
            lbl.MouseLeave += (s, e) => { if (lbl.BackColor != C_NavActive) lbl.BackColor = Color.Transparent; };
            return lbl;
        }

        private void ShowPage(string page)
        {
            _pnlDashboard.Visible = page == "dashboard";
            _pnlConsole.Visible = page == "console";

            _lblNavDashboard.BackColor = page == "dashboard" ? C_NavActive : Color.Transparent;
            _lblNavDashboard.ForeColor = page == "dashboard" ? C_TextMain : C_TextMuted;
            _lblNavConsole.BackColor = page == "console" ? C_NavActive : Color.Transparent;
            _lblNavConsole.ForeColor = page == "console" ? C_TextMain : C_TextMuted;
        }

        // ================================================================
        // Dashboard Page
        // ================================================================
        private Panel BuildDashboardPage()
        {
            var page = new Panel { BackColor = C_BgMain, Padding = new Padding(20, 16, 20, 16) };

            // Control row
            var ctrlRow = BuildControlRow();
            // Index row
            var idxRow = BuildIndexRow();
            // Stats group
            var statsGrp = BuildStatsGroup();
            // Connection buttons
            var connRow = BuildConnectionButtonRow();

            page.Controls.Add(statsGrp);
            page.Controls.Add(connRow);
            page.Controls.Add(idxRow);
            page.Controls.Add(ctrlRow);
            return page;
        }

        private Panel BuildControlRow()
        {
            var pnl = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Color.Transparent };

            _btnStart = DarkBtn("\u25B6  Start", Color.FromArgb(46, 139, 87), Color.White, 100, 36);
            _btnStart.Left = 0; _btnStart.Top = 8;
            _btnStart.Click += BtnStart_Click;

            _btnStop = DarkBtn("\u25A0  Stop", Color.FromArgb(180, 60, 60), Color.White, 100, 36);
            _btnStop.Left = 110; _btnStop.Top = 8;
            _btnStop.Enabled = false;
            _btnStop.Click += BtnStop_Click;

            _lblStatus = Lbl("Stopped", 11f, Color.Gray);
            _lblStatus.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            _lblStatus.Left = 230; _lblStatus.Top = 16;

            pnl.Controls.AddRange(new Control[] { _btnStart, _btnStop, _lblStatus });
            return pnl;
        }

        private Panel BuildIndexRow()
        {
            var pnl = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.Transparent };

            var lbl = Lbl("Resume from ID:", 9.5f, C_TextMuted);
            lbl.Left = 0; lbl.Top = 9;

            _nudStartAtId = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 100_000_000,
                Width = 120,
                BackColor = C_BgInput,
                ForeColor = C_TextMain,
                BorderStyle = BorderStyle.FixedSingle,
                Left = 128,
                Top = 5
            };

            _btnSetIndex = DarkBtn("Set", C_BtnBg, C_TextMain, 56, 24);
            _btnSetIndex.Left = 256; _btnSetIndex.Top = 7;
            _btnSetIndex.Click += BtnSetIndex_Click;

            pnl.Controls.AddRange(new Control[] { lbl, _nudStartAtId, _btnSetIndex });
            return pnl;
        }

        private Panel BuildConnectionButtonRow()
        {
            var pnl = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.Transparent };

            _btnTestConnection = DarkBtn("Test Connection", C_BtnBg, C_TextMain, 130, 30);
            _btnTestConnection.Left = 0; _btnTestConnection.Top = 8;
            _btnTestConnection.Click += BtnTestConnection_Click;

            _btnListDatabases = DarkBtn("List Databases", C_BtnBg, C_TextMain, 130, 30);
            _btnListDatabases.Left = 140; _btnListDatabases.Top = 8;
            _btnListDatabases.Click += BtnListDatabases_Click;

            pnl.Controls.AddRange(new Control[] { _btnTestConnection, _btnListDatabases });
            return pnl;
        }

        private GroupBox BuildStatsGroup()
        {
            var grp = new GroupBox
            {
                Text = "Realtime Statistics",
                Dock = DockStyle.Top,
                Height = 120,
                ForeColor = C_TextMuted,
                BackColor = C_BgPanel
            };

            int spacing = 150;

            // Row 1
            int x = 20, y = 24;
            AddStatPair(grp, "Current Index", ref _lblCurrentIndex, x, y);
            AddStatPair(grp, "Session Duration", ref _lblSessionDuration, x + spacing, y);
            AddStatPair(grp, "Rate (/ min)", ref _lblRate, x + spacing * 2, y);

            // Row 2
            y = 66;
            AddStatPair(grp, "Total Uploaded", ref _lblTotalUploaded, x, y);
            AddStatPair(grp, "Session Uploaded", ref _lblSessionUploaded, x + spacing, y);
            AddStatPair(grp, "Failed", ref _lblFailed, x + spacing * 2, y);

            return grp;
        }

        private void AddStatPair(Control parent, string caption, ref Label valueLabel, int x, int y)
        {
            var cap = Lbl(caption, 8f, C_TextMuted);
            cap.Location = new Point(x, y);
            cap.AutoSize = true;

            valueLabel = Lbl("0", 14f, C_TextMain);
            valueLabel.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
            valueLabel.Location = new Point(x, y + 16);
            valueLabel.AutoSize = true;

            parent.Controls.Add(cap);
            parent.Controls.Add(valueLabel);
        }

        // ================================================================
        // Console Page
        // ================================================================
        private Panel BuildConsolePage()
        {
            var page = new Panel { BackColor = C_BgMain, Padding = new Padding(12) };

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(0)
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // ── Logging Options row ──
            var optionsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Padding = new Padding(0, 2, 0, 4),
                BackColor = Color.Transparent
            };

            var lblLogOpts = new Label
            {
                Text = "Log Options:",
                AutoSize = true,
                Margin = new Padding(0, 5, 8, 0),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = C_TextMain
            };
            optionsPanel.Controls.Add(lblLogOpts);

            _chkVerbose = new CheckBox { Text = "Verbose (per UUT)", AutoSize = true, Margin = new Padding(8, 3, 0, 0), ForeColor = C_TextMain, BackColor = Color.Transparent };
            _chkStepDetails = new CheckBox { Text = "Step Details", AutoSize = true, Margin = new Padding(8, 3, 0, 0), ForeColor = C_TextMain, BackColor = Color.Transparent };
            _chkMeasurements = new CheckBox { Text = "Measurements", AutoSize = true, Margin = new Padding(8, 3, 0, 0), ForeColor = C_TextMain, BackColor = Color.Transparent };
            _chkSkipped = new CheckBox { Text = "Skipped Records", AutoSize = true, Margin = new Padding(8, 3, 0, 0), ForeColor = C_TextMain, BackColor = Color.Transparent };
            _chkBatchSummary = new CheckBox { Text = "Batch Summary", AutoSize = true, Margin = new Padding(8, 3, 0, 0), ForeColor = C_TextMain, BackColor = Color.Transparent };

            _chkVerbose.CheckedChanged += LogOption_Changed;
            _chkStepDetails.CheckedChanged += LogOption_Changed;
            _chkMeasurements.CheckedChanged += LogOption_Changed;
            _chkSkipped.CheckedChanged += LogOption_Changed;
            _chkBatchSummary.CheckedChanged += LogOption_Changed;

            optionsPanel.Controls.Add(_chkVerbose);
            optionsPanel.Controls.Add(_chkStepDetails);
            optionsPanel.Controls.Add(_chkMeasurements);
            optionsPanel.Controls.Add(_chkSkipped);
            optionsPanel.Controls.Add(_chkBatchSummary);

            _btnClearLog = DarkBtn("Clear", C_BtnBg, C_TextMain, 60, 24);
            _btnClearLog.Margin = new Padding(20, 0, 0, 0);
            _btnClearLog.Click += (s, e) => _txtLog.Clear();
            optionsPanel.Controls.Add(_btnClearLog);

            mainLayout.Controls.Add(optionsPanel, 0, 0);

            // ── Log ──
            _txtLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(0x1e, 0x1e, 0x1e),
                ForeColor = C_TextMain,
                Font = new Font("Cascadia Mono", 9f, FontStyle.Regular, GraphicsUnit.Point, 0, false),
                BorderStyle = BorderStyle.None,
                WordWrap = true
            };
            if (!FontFamily.Families.Any(f => f.Name == "Cascadia Mono"))
                _txtLog.Font = new Font("Consolas", 9f);

            mainLayout.Controls.Add(_txtLog, 0, 1);

            page.Controls.Add(mainLayout);

            BindLoggingOptions();

            return page;
        }

        // ================================================================
        // Event Handlers
        // ================================================================
        private void MainForm_Shown(object? sender, EventArgs e)
        {
            if (_config.AutoStart && _config.IsConfigured)
            {
                AppendLog("AutoStart: configuration detected, starting processing...");
                BtnStart_Click(this, EventArgs.Empty);
            }
            else if (_config.AutoStart && !_config.IsConfigured)
            {
                AppendLog("AutoStart is enabled but server/database is not yet configured.");
                AppendLog("Go to File > Options to configure the connection.");
            }
        }

        private async void BtnStart_Click(object? sender, EventArgs e)
        {
            SetRunning(true);
            _sessionImported = 0;
            _sessionFailed = 0;
            _sessionStart = DateTime.Now;

            _cts = new CancellationTokenSource();
            _poller = new DatabasePoller(_config);
            _poller.OnLog += msg => BeginInvoke(() => AppendLog(msg));
            _poller.OnBatchCompleted += n =>
            {
                _totalImported += n;
                _sessionImported += n;
            };

            try
            {
                await Task.Run(() => _poller.RunAsync(_cts.Token));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppendLog($"FATAL: {ex.Message}");
            }

            SetRunning(false);
        }

        private void BtnStop_Click(object? sender, EventArgs e)
        {
            _cts?.Cancel();
            AppendLog("Stop requested...");
        }

        private void BtnSetIndex_Click(object? sender, EventArgs e)
        {
            var resolvedPath = _config.ResolveDataPath(_config.CheckpointFile);
            var cp = Checkpoint.Load(resolvedPath);
            cp.LastId = (long)_nudStartAtId.Value;
            cp.Save(resolvedPath);
            AppendLog($"Checkpoint manually set to ID {cp.LastId}");
        }

        private async void BtnTestConnection_Click(object? sender, EventArgs e)
        {
            _btnTestConnection.Enabled = false;
            AppendLog("Testing connection...");

            var poller = new DatabasePoller(_config);
            var err = await poller.TestConnectionAsync();

            if (err == null)
            {
                AppendLog("Connection OK");
                _lblConnStatus.Text = "Connected";
                _lblConnStatus.ForeColor = C_Success;
            }
            else
            {
                AppendLog($"Connection failed: {err}");
                _lblConnStatus.Text = "Failed";
                _lblConnStatus.ForeColor = C_Error;
            }

            _btnTestConnection.Enabled = true;
        }

        private async void BtnListDatabases_Click(object? sender, EventArgs e)
        {
            _btnListDatabases.Enabled = false;
            AppendLog("Listing databases...");

            try
            {
                var poller = new DatabasePoller(_config);
                var dbs = await poller.ListDatabasesAsync();
                AppendLog($"Found {dbs.Count} database(s):");
                foreach (var db in dbs)
                    AppendLog($"  - {db}");
            }
            catch (Exception ex)
            {
                AppendLog($"Error: {ex.Message}");
            }

            _btnListDatabases.Enabled = true;
        }

        private void SaveConfig()
        {
            _config.Save();
            AppendLog("Configuration saved.");
        }

        private void MenuOptions_Click(object? sender, EventArgs e)
        {
            using var dlg = new OptionsDialog(_config);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                UpdateConnectionDisplay();
                BindLoggingOptions();
                AppendLog("Options saved.");
            }
        }

        private void BindLoggingOptions()
        {
            _chkVerbose.Checked = _config.VerboseLogging;
            _chkStepDetails.Checked = _config.LogStepDetails;
            _chkMeasurements.Checked = _config.LogMeasurements;
            _chkSkipped.Checked = _config.LogSkippedRecords;
            _chkBatchSummary.Checked = _config.LogBatchSummary;
        }

        private void LogOption_Changed(object? sender, EventArgs e)
        {
            _config.VerboseLogging = _chkVerbose.Checked;
            _config.LogStepDetails = _chkStepDetails.Checked;
            _config.LogMeasurements = _chkMeasurements.Checked;
            _config.LogSkippedRecords = _chkSkipped.Checked;
            _config.LogBatchSummary = _chkBatchSummary.Checked;
        }

        private void MenuAbout_Click(object? sender, EventArgs e)
        {
            MessageBox.Show(
                $"{AppTitle}\n\nVersion {AppVersion}\n" +
                $"Source: {_config.SqlServer} / {_config.SqlDatabase}\n\n" +
                "\u00A9 2026 WATS",
                "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ================================================================
        // Helpers
        // ================================================================
        private void SetRunning(bool running)
        {
            _btnStart.Enabled = !running;
            _btnStop.Enabled = running;
            _btnTestConnection.Enabled = !running;
            _btnListDatabases.Enabled = !running;
            _lblStatus.Text = running ? "Running" : "Stopped";
            _lblStatus.ForeColor = running ? C_Success : Color.Gray;
            Icon = running ? (_iconOnline ?? Icon) : (_iconOffline ?? Icon);

            if (running) _dashboardTimer.Start();
            else _dashboardTimer.Stop();
        }

        private void DashboardTimer_Tick(object? sender, EventArgs e)
        {
            var elapsed = DateTime.Now - _sessionStart;
            _lblSessionDuration.Text = elapsed.ToString(@"hh\:mm\:ss");
            _lblSessionUploaded.Text = _sessionImported.ToString();
            _lblTotalUploaded.Text = _totalImported.ToString();
            _lblFailed.Text = _sessionFailed.ToString();

            double minutes = elapsed.TotalMinutes;
            _lblRate.Text = minutes > 0.1 ? (_sessionImported / minutes).ToString("0.0") : "0.0";

            // Update current index from checkpoint
            try
            {
                var cp = Checkpoint.Load(_config.CheckpointFile);
                _lblCurrentIndex.Text = cp.LastId.ToString("N0");
                _nudStartAtId.Value = Math.Clamp(cp.LastId, 0, 100_000_000);
            }
            catch { }
        }

        private void UpdateConnectionDisplay()
        {
            bool configured = !string.IsNullOrWhiteSpace(_config.SqlServer) &&
                              !string.IsNullOrWhiteSpace(_config.SqlDatabase);

            _lblSource.Text = configured
                ? $"{_config.SqlServer}  /  {_config.SqlDatabase}"
                : "Not configured";

            // Detect WATS server URL from installed WATS Client
            string watsUrl = "(no WATS Client)";
            try
            {
                var tdm = new Virinco.WATS.Interface.TDM();
                tdm.InitializeAPI(true);
                var url = tdm.TargetURL;
                if (!string.IsNullOrWhiteSpace(url)) watsUrl = url;
            }
            catch { }

            _lblDest.Text = watsUrl;

            _lblConnStatus.Text = configured ? "Configured" : "Not configured";
            _lblConnStatus.ForeColor = configured ? C_TextMuted : C_Error;
        }

        private void AppendLog(string message)
        {
            if (InvokeRequired) { BeginInvoke(() => AppendLog(message)); return; }
            _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            _txtLog.ScrollToCaret();
            if (_txtLog.TextLength > 200_000)
            {
                _txtLog.Select(0, _txtLog.TextLength / 2);
                _txtLog.SelectedText = "";
            }
        }

        private static Label Lbl(string text, float size, Color color) => new()
        {
            Text = text,
            AutoSize = true,
            ForeColor = color,
            Font = new Font("Segoe UI", size),
            BackColor = Color.Transparent
        };

        private static Button DarkBtn(string text, Color bg, Color fg, int w, int h) => new()
        {
            Text = text,
            Width = w,
            Height = h,
            BackColor = bg,
            ForeColor = fg,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderColor = Color.FromArgb(0x3d, 0x3d, 0x3d), BorderSize = 1 },
            Cursor = Cursors.Hand
        };

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _cts?.Cancel();
            _dashboardTimer.Stop();
            base.OnFormClosing(e);
        }

        // -- Dark menu renderer --
        private sealed class DarkColorTable : ProfessionalColorTable
        {
            private static readonly Color BgBar = Color.FromArgb(0x1e, 0x1e, 0x1e);
            private static readonly Color BgDrop = Color.FromArgb(0x2d, 0x2d, 0x30);
            private static readonly Color BgHover = Color.FromArgb(0x3c, 0x3c, 0x3c);
            private static readonly Color BgMargin = Color.FromArgb(0x25, 0x25, 0x26);
            private static readonly Color Border = Color.FromArgb(0x3d, 0x3d, 0x3d);
            public override Color MenuStripGradientBegin => BgBar;
            public override Color MenuStripGradientEnd => BgBar;
            public override Color ToolStripDropDownBackground => BgDrop;
            public override Color ImageMarginGradientBegin => BgMargin;
            public override Color ImageMarginGradientMiddle => BgMargin;
            public override Color ImageMarginGradientEnd => BgMargin;
            public override Color MenuItemSelected => BgHover;
            public override Color MenuItemSelectedGradientBegin => BgHover;
            public override Color MenuItemSelectedGradientEnd => BgHover;
            public override Color MenuItemPressedGradientBegin => BgHover;
            public override Color MenuItemPressedGradientEnd => BgHover;
            public override Color MenuItemPressedGradientMiddle => BgHover;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Border;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Border;
            public override Color CheckBackground => BgHover;
            public override Color CheckSelectedBackground => BgHover;
            public override Color CheckPressedBackground => BgHover;
        }

        private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            private static readonly Color TextColor = Color.FromArgb(0xd4, 0xd4, 0xd4);
            public DarkMenuRenderer() : base(new DarkColorTable()) { }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? TextColor : Color.FromArgb(0x60, 0x60, 0x60);
                base.OnRenderItemText(e);
            }
            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = TextColor;
                base.OnRenderArrow(e);
            }
        }
    }
}
