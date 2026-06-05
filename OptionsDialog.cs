using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Virinco.WATS.Interface;

namespace Virinco.WATS.Converter.KohYoung
{
    public class OptionsDialog : Form
    {
        // Dark theme colors
        private static readonly Color C_BgMain = Color.FromArgb(0x25, 0x25, 0x26);
        private static readonly Color C_BgPanel = Color.FromArgb(0x2d, 0x2d, 0x30);
        private static readonly Color C_BgInput = Color.FromArgb(0x3c, 0x3c, 0x3c);
        private static readonly Color C_Text = Color.FromArgb(0xd4, 0xd4, 0xd4);
        private static readonly Color C_Muted = Color.FromArgb(0x85, 0x85, 0x85);
        private static readonly Color C_Accent = Color.FromArgb(0xe8, 0xa0, 0x20);
        private static readonly Color C_Border = Color.FromArgb(0x3d, 0x3d, 0x3d);
        private static readonly Color C_BtnBg = Color.FromArgb(0x37, 0x37, 0x38);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int v, int sz);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int v = 1; DwmSetWindowAttribute(Handle, 20, ref v, sizeof(int)); } catch { }
        }

        private readonly AppConfig _config;
        private TabControl _tabControl = null!;

        // Database tab
        private TextBox _txtServer = null!;
        private TextBox _txtDatabase = null!;
        private TextBox _txtUser = null!;
        private TextBox _txtPassword = null!;
        private CheckBox _chkTrustCert = null!;

        // Polling tab
        private NumericUpDown _nudPollInterval = null!;
        private NumericUpDown _nudBatchSize = null!;
        private TextBox _txtCheckpointFile = null!;

        // WATS tab
        private CheckBox _chkOffline = null!;
        private Label _lblWatsStatus = null!;
        private Label _lblWatsUrl = null!;

        public string? DetectedWatsUrl { get; private set; }

        // Process Codes tab
        private TextBox _txtProcessAoiTop = null!;
        private TextBox _txtProcessAoiBottom = null!;
        private TextBox _txtProcessRepair = null!;

        // Advanced tab
        private NumericUpDown _nudTimestampOffset = null!;
        private CheckBox _chkAutoStart = null!;

        public OptionsDialog(AppConfig config)
        {
            _config = config;
            InitializeComponent();
            LoadConfig();
        }

        private void InitializeComponent()
        {
            Text = "Options";
            Size = new Size(480, 400);
            MinimumSize = new Size(420, 350);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);
            BackColor = C_BgMain;
            ForeColor = C_Text;

            _tabControl = new DarkTabControl { Dock = DockStyle.Fill };

            // === Database Tab ===
            var tabDb = new TabPage("Database");
            var dbLayout = CreateFormLayout();

            _txtServer = new TextBox { Dock = DockStyle.Fill };
            _txtDatabase = new TextBox { Dock = DockStyle.Fill };
            _txtUser = new TextBox { Dock = DockStyle.Fill };
            _txtPassword = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
            _chkTrustCert = new CheckBox { Text = "Trust Server Certificate", AutoSize = true };

            AddFormRow(dbLayout, "Server:", _txtServer, 0);
            AddFormRow(dbLayout, "Database:", _txtDatabase, 1);
            AddFormRow(dbLayout, "User:", _txtUser, 2);
            AddFormRow(dbLayout, "Password:", _txtPassword, 3);
            dbLayout.Controls.Add(_chkTrustCert, 1, 4);

            tabDb.Controls.Add(dbLayout);
            _tabControl.TabPages.Add(tabDb);

            // === Polling Tab ===
            var tabPoll = new TabPage("Polling");
            var pollLayout = CreateFormLayout();

            _nudPollInterval = new NumericUpDown { Minimum = 5, Maximum = 3600, Width = 100 };
            _nudBatchSize = new NumericUpDown { Minimum = 1, Maximum = 10000, Width = 100 };
            _txtCheckpointFile = new TextBox { Dock = DockStyle.Fill };

            AddFormRow(pollLayout, "Poll Interval (s):", _nudPollInterval, 0);
            AddFormRow(pollLayout, "Batch Size:", _nudBatchSize, 1);
            AddFormRow(pollLayout, "Checkpoint File:", _txtCheckpointFile, 2);

            tabPoll.Controls.Add(pollLayout);
            _tabControl.TabPages.Add(tabPoll);

            // === WATS Tab ===
            var tabWats = new TabPage("WATS");
            var watsLayout = CreateFormLayout();

            _lblWatsStatus = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(380, 0),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            watsLayout.Controls.Add(_lblWatsStatus, 0, 0);
            watsLayout.SetColumnSpan(_lblWatsStatus, 2);

            _lblWatsUrl = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(380, 0),
                Font = new Font("Segoe UI", 9f)
            };
            watsLayout.Controls.Add(_lblWatsUrl, 0, 1);
            watsLayout.SetColumnSpan(_lblWatsUrl, 2);

            _chkOffline = new CheckBox { Text = "Offline Mode (no WATS submit)", AutoSize = true };
            watsLayout.Controls.Add(_chkOffline, 1, 2);

            var watsHint = new Label
            {
                Text = "Server URL and authentication are managed by the\n" +
                       "installed WATS Client. Make sure WATS Client is\n" +
                       "running and configured before starting.",
                ForeColor = Color.DimGray,
                AutoSize = true,
                MaximumSize = new Size(350, 0)
            };
            watsLayout.Controls.Add(watsHint, 1, 3);

            tabWats.Controls.Add(watsLayout);
            _tabControl.TabPages.Add(tabWats);

            // === Process Codes Tab ===
            var tabProcess = new TabPage("Process Codes");
            var processLayout = CreateFormLayout();

            _txtProcessAoiTop = new TextBox { Width = 100 };
            _txtProcessAoiBottom = new TextBox { Width = 100 };
            _txtProcessRepair = new TextBox { Width = 100 };

            AddFormRow(processLayout, "AOI Top:", _txtProcessAoiTop, 0);
            AddFormRow(processLayout, "AOI Bottom:", _txtProcessAoiBottom, 1);
            AddFormRow(processLayout, "Repair Operation:", _txtProcessRepair, 2);

            var processHint = new Label
            {
                Text = "WATS operation type codes used when submitting reports.\n" +
                       "These must match the process codes configured in WATS.",
                ForeColor = Color.DimGray,
                AutoSize = true,
                MaximumSize = new Size(350, 0)
            };
            processLayout.Controls.Add(processHint, 1, 3);

            tabProcess.Controls.Add(processLayout);
            _tabControl.TabPages.Add(tabProcess);

            // === Advanced Tab ===
            var tabAdv = new TabPage("Advanced");
            var advLayout = CreateFormLayout();

            _nudTimestampOffset = new NumericUpDown
            {
                Minimum = -24,
                Maximum = 24,
                DecimalPlaces = 1,
                Increment = 0.5m,
                Width = 100
            };

            AddFormRow(advLayout, "Timestamp Offset (h):", _nudTimestampOffset, 0);

            var offsetHint = new Label
            {
                Text = "Hours to add to all timestamps from the AOI database.\n" +
                       "Set to 0 if the AOI machine and WATS server are in the same timezone.\n" +
                       "Set to -1 if the machine clock is one hour ahead of the server.",
                ForeColor = Color.DimGray,
                AutoSize = true,
                MaximumSize = new Size(350, 0)
            };
            advLayout.Controls.Add(offsetHint, 1, 1);

            _chkAutoStart = new CheckBox { Text = "Auto-start processing on application launch", AutoSize = true };
            advLayout.Controls.Add(_chkAutoStart, 1, 2);

            tabAdv.Controls.Add(advLayout);
            _tabControl.TabPages.Add(tabAdv);

            // === Buttons ===
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 45,
                BackColor = C_BgMain,
                Padding = new Padding(8)
            };

            var btnCancel = new Button { Text = "Cancel", Width = 80, DialogResult = DialogResult.Cancel };
            var btnOK = new Button { Text = "OK", Width = 80, DialogResult = DialogResult.OK };
            btnOK.Click += BtnOK_Click;

            buttonPanel.Controls.Add(btnCancel);
            buttonPanel.Controls.Add(btnOK);

            AcceptButton = btnOK;
            CancelButton = btnCancel;

            Controls.Add(_tabControl);
            Controls.Add(buttonPanel);

            ApplyDarkTheme(this);
        }

        private static TableLayoutPanel CreateFormLayout()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(10),
                AutoScroll = true
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return layout;
        }

        private static void AddFormRow(TableLayoutPanel layout, string label, Control control, int row)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, row);
            layout.Controls.Add(control, 1, row);
        }

        private void LoadConfig()
        {
            _txtServer.Text = _config.SqlServer;
            _txtDatabase.Text = _config.SqlDatabase;
            _txtUser.Text = _config.SqlUser;
            _txtPassword.Text = _config.SqlPassword;
            _chkTrustCert.Checked = _config.TrustServerCertificate;

            _nudPollInterval.Value = Math.Clamp(_config.PollIntervalSeconds, 5, 3600);
            _nudBatchSize.Value = Math.Clamp(_config.BatchSize, 1, 10000);
            _txtCheckpointFile.Text = _config.CheckpointFile;

            _chkOffline.Checked = _config.OfflineMode;

            // Detect WATS Client
            DetectWatsClient();

            _txtProcessAoiTop.Text = _config.ProcessCodeAoiTop;
            _txtProcessAoiBottom.Text = _config.ProcessCodeAoiBottom;
            _txtProcessRepair.Text = _config.ProcessCodeRepair;

            _nudTimestampOffset.Value = (decimal)Math.Clamp(_config.TimestampOffsetHours, -24, 24);
            _chkAutoStart.Checked = _config.AutoStart;
        }

        private void BtnOK_Click(object? sender, EventArgs e)
        {
            _config.SqlServer = _txtServer.Text.Trim();
            _config.SqlDatabase = _txtDatabase.Text.Trim();
            _config.SqlUser = _txtUser.Text.Trim();
            _config.SqlPassword = _txtPassword.Text;
            _config.TrustServerCertificate = _chkTrustCert.Checked;

            _config.PollIntervalSeconds = (int)_nudPollInterval.Value;
            _config.BatchSize = (int)_nudBatchSize.Value;
            _config.CheckpointFile = _txtCheckpointFile.Text.Trim();

            _config.OfflineMode = _chkOffline.Checked;

            _config.ProcessCodeAoiTop = _txtProcessAoiTop.Text.Trim();
            _config.ProcessCodeAoiBottom = _txtProcessAoiBottom.Text.Trim();
            _config.ProcessCodeRepair = _txtProcessRepair.Text.Trim();

            _config.TimestampOffsetHours = (double)_nudTimestampOffset.Value;
            _config.AutoStart = _chkAutoStart.Checked;

            _config.Save();
        }

        private void DetectWatsClient()
        {
            try
            {
                var tdm = new TDM();
                tdm.InitializeAPI(true);
                DetectedWatsUrl = tdm.TargetURL;
                _lblWatsStatus.Text = "\u2705  WATS Client detected and available.";
                _lblWatsStatus.ForeColor = Color.FromArgb(0x4e, 0xc9, 0x4e);
                _lblWatsUrl.Text = !string.IsNullOrWhiteSpace(DetectedWatsUrl)
                    ? $"Server:  {DetectedWatsUrl}"
                    : "Server:  (unknown)";
                _lblWatsUrl.ForeColor = C_Text;
            }
            catch
            {
                DetectedWatsUrl = null;
                _lblWatsStatus.Text = "\u274C  WATS Client not found.\n" +
                                      "Install and configure WATS Client to submit reports.";
                _lblWatsStatus.ForeColor = Color.FromArgb(0xf4, 0x47, 0x47);
                _lblWatsUrl.Text = "";
            }
        }

        private void ApplyDarkTheme(Control root)
        {
            foreach (Control c in root.Controls)
            {
                switch (c)
                {
                    case DarkTabControl dtc:
                        // Painting handled entirely by DarkTabControl
                        break;
                    case TabControl tc:
                        tc.BackColor = C_BgMain;
                        tc.ForeColor = C_Text;
                        break;
                    case TabPage tp:
                        tp.BackColor = C_BgPanel;
                        tp.ForeColor = C_Text;
                        tp.UseVisualStyleBackColor = false;
                        break;
                    case FlowLayoutPanel flp:
                        flp.BackColor = Color.Transparent;
                        break;
                    case TableLayoutPanel tlp:
                        tlp.BackColor = Color.Transparent;
                        break;
                    case Panel p:
                        p.BackColor = Color.Transparent;
                        break;
                    case TextBox tb:
                        tb.BackColor = C_BgInput;
                        tb.ForeColor = C_Text;
                        tb.BorderStyle = BorderStyle.FixedSingle;
                        break;
                    case NumericUpDown nud:
                        nud.BackColor = C_BgInput;
                        nud.ForeColor = C_Text;
                        break;
                    case ComboBox cb:
                        cb.BackColor = C_BgInput;
                        cb.ForeColor = C_Text;
                        cb.FlatStyle = FlatStyle.Flat;
                        break;
                    case CheckBox chk:
                        chk.BackColor = Color.Transparent;
                        chk.ForeColor = C_Text;
                        chk.FlatStyle = FlatStyle.Flat;
                        chk.FlatAppearance.BorderColor = C_Border;
                        chk.FlatAppearance.CheckedBackColor = C_BgInput;
                        break;
                    case Button btn:
                        btn.BackColor = C_BtnBg;
                        btn.ForeColor = C_Text;
                        btn.FlatStyle = FlatStyle.Flat;
                        btn.FlatAppearance.BorderColor = C_Border;
                        break;
                    case Label lbl:
                        lbl.BackColor = Color.Transparent;
                        if (lbl.ForeColor == Color.DimGray || lbl.ForeColor == SystemColors.ControlText)
                            lbl.ForeColor = C_Muted;
                        else
                            lbl.ForeColor = C_Text;
                        break;
                    default:
                        c.BackColor = C_BgPanel;
                        c.ForeColor = C_Text;
                        break;
                }
                ApplyDarkTheme(c);
            }
        }

        /// <summary>
        /// TabControl subclass that completely owns all painting to eliminate
        /// native white chrome that cannot be suppressed with style properties.
        /// </summary>
        private class DarkTabControl : TabControl
        {
            private static readonly Color BgMain = Color.FromArgb(0x25, 0x25, 0x26);
            private static readonly Color BgPanel = Color.FromArgb(0x2d, 0x2d, 0x30);
            private static readonly Color FgText = Color.FromArgb(0xd4, 0xd4, 0xd4);
            private static readonly Color FgMuted = Color.FromArgb(0x85, 0x85, 0x85);
            private static readonly Color Border = Color.FromArgb(0x3d, 0x3d, 0x3d);

            public DarkTabControl()
            {
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.DoubleBuffer |
                    ControlStyles.ResizeRedraw,
                    true);
                Appearance = TabAppearance.FlatButtons;
                DrawMode = TabDrawMode.OwnerDrawFixed;
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                using var brush = new SolidBrush(BgMain);
                e.Graphics.FillRectangle(brush, ClientRectangle);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                // Fill entire background dark
                using var bgBrush = new SolidBrush(BgMain);
                e.Graphics.FillRectangle(bgBrush, ClientRectangle);

                // Fill the tab page area with panel color
                if (TabCount > 0)
                {
                    var page = GetTabRect(0);
                    var pageArea = new Rectangle(0, page.Bottom, Width, Height - page.Bottom);
                    using var panelBrush = new SolidBrush(BgPanel);
                    e.Graphics.FillRectangle(panelBrush, pageArea);

                    // Separator line below tabs
                    using var borderPen = new Pen(Border);
                    e.Graphics.DrawLine(borderPen, 0, page.Bottom, Width, page.Bottom);
                }

                // Draw tab headers
                for (int i = 0; i < TabCount; i++)
                {
                    var bounds = GetTabRect(i);
                    bool selected = SelectedIndex == i;
                    using var tabBg = new SolidBrush(selected ? BgPanel : BgMain);
                    e.Graphics.FillRectangle(tabBg, bounds);
                    using var fg = new SolidBrush(selected ? FgText : FgMuted);
                    var sf = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
                    };
                    e.Graphics.DrawString(TabPages[i].Text, Font, fg, bounds, sf);
                }
            }
        }
    }
}
