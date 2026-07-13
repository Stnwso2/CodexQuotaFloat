using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CodexQuotaFloat;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\CodexQuotaFloat.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new QuotaApplicationContext());
    }
}

internal sealed class QuotaApplicationContext : ApplicationContext
{
    private readonly QuotaForm _form = new();
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _lifecycleTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 30_000 };
    private readonly HttpClient _http = NetworkClientFactory.Create();
    private ToolStripMenuItem? _topMostMenuItem;
    private bool _refreshing;
    private bool _codexRunning;
    private bool _hasSuccessfulQuota;
    private int _notRunningChecks;
    private int _consecutiveRefreshFailures;

    public QuotaApplicationContext()
    {
        _trayIcon = new NotifyIcon
        {
            Icon = IconFactory.Create(),
            Text = "Codex 额度悬浮窗（等待 Codex）",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _trayIcon.DoubleClick += (_, _) =>
        {
            if (_codexRunning)
            {
                _form.Show();
                DockToCodex();
            }
            _ = RefreshQuotaAsync();
        };

        _form.RefreshRequested += (_, _) => _ = RefreshQuotaAsync();
        _form.MenuRequested += (_, _) => _trayIcon.ContextMenuStrip?.Show(Cursor.Position);
        _form.AlwaysOnTopChanged += enabled =>
        {
            if (_topMostMenuItem is not null) _topMostMenuItem.Checked = enabled;
        };

        _lifecycleTimer.Tick += (_, _) => CheckCodexLifecycle();
        _refreshTimer.Tick += (_, _) =>
        {
            if (_codexRunning)
            {
                _ = RefreshQuotaAsync();
            }
        };
        _lifecycleTimer.Start();
        _refreshTimer.Start();
        CheckCodexLifecycle();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()),
            ShowImageMargin = false,
            ForeColor = Color.FromArgb(55, 54, 49)
        };
        menu.Items.Add("立即刷新", null, (_, _) => _ = RefreshQuotaAsync());

        _topMostMenuItem = new ToolStripMenuItem("保持最前") { Checked = false, CheckOnClick = true };
        _topMostMenuItem.CheckedChanged += (_, _) => _form.SetAlwaysOnTop(_topMostMenuItem.Checked);
        menu.Items.Add(_topMostMenuItem);
        menu.Items.Add("恢复自动贴靠", null, (_, _) =>
        {
            _form.ResetToAutoDock();
            DockToCodex();
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出悬浮窗", null, (_, _) => ExitThread());
        return menu;
    }

    private void CheckCodexLifecycle()
    {
        var state = CodexLocator.Find();
        if (state.Running)
        {
            _notRunningChecks = 0;
            if (!_codexRunning)
            {
                _codexRunning = true;
                _trayIcon.Text = "Codex 额度悬浮窗（自动更新中）";
                _form.SetWaiting("正在读取当前账户额度…");
                _form.Show();
                _ = RefreshQuotaAsync();
            }

            if (_form.Visible && state.WindowHandle != IntPtr.Zero)
            {
                _form.AttachToCodexWindow(state.WindowHandle);
            }

            if (_form.Visible && !_form.HasManualPositionOrIsDragging())
            {
                DockToCodex(state.WindowHandle);
            }
            return;
        }

        _notRunningChecks++;
        if (_notRunningChecks < 2 || !_codexRunning)
        {
            return;
        }

        _codexRunning = false;
        _form.Hide();
        _form.DetachFromCodexWindow();
        _trayIcon.Text = "Codex 额度悬浮窗（等待 Codex）";
    }

    private void DockToCodex(IntPtr knownWindow = default)
    {
        if (_form.HasManualPositionOrIsDragging())
        {
            return;
        }

        var window = knownWindow != IntPtr.Zero ? knownWindow : CodexLocator.Find().WindowHandle;
        Rectangle workArea;
        Rectangle codexRect;

        if (window != IntPtr.Zero && NativeMethods.GetWindowRect(window, out var nativeRect) && nativeRect.Left > -10_000)
        {
            codexRect = Rectangle.FromLTRB(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);
            workArea = Screen.FromRectangle(codexRect).WorkingArea;
        }
        else
        {
            workArea = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
            codexRect = workArea;
        }

        const int gap = 12;
        var x = codexRect.Right + gap;
        var y = codexRect.Top + 52;

        if (x + _form.Width > workArea.Right)
        {
            x = Math.Max(workArea.Left + gap, codexRect.Right - _form.Width - 18);
        }
        if (window == IntPtr.Zero)
        {
            x = workArea.Right - _form.Width - 18;
            y = workArea.Top + 72;
        }

        y = Math.Clamp(y, workArea.Top + gap, workArea.Bottom - _form.Height - gap);
        x = Math.Clamp(x, workArea.Left + gap, workArea.Right - _form.Width - gap);
        if (_form.Left != x || _form.Top != y)
        {
            _form.Location = new Point(x, y);
        }
    }

    private async Task RefreshQuotaAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var authPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
            if (!File.Exists(authPath))
            {
                _form.SetError("尚未找到 Codex 登录信息");
                return;
            }

            string accessToken;
            string accountId;
            try
            {
                using var authDocument = JsonDocument.Parse(await File.ReadAllTextAsync(authPath));
                var tokens = authDocument.RootElement.GetProperty("tokens");
                accessToken = tokens.GetProperty("access_token").GetString() ?? string.Empty;
                accountId = tokens.GetProperty("account_id").GetString() ?? string.Empty;
            }
            catch
            {
                _form.SetError("Codex 登录信息正在更新，稍后重试");
                return;
            }

            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(accountId))
            {
                _form.SetError("请先在 Codex 中登录 ChatGPT");
                return;
            }

            using var response = await SendUsageRequestWithRetryAsync(accessToken, accountId);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _form.SetError("等待 Codex 刷新登录状态…");
                return;
            }
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var usageDocument = await JsonDocument.ParseAsync(stream);
            var snapshot = QuotaParser.Parse(usageDocument.RootElement);
            _form.SetSnapshot(snapshot);
            _trayIcon.Text = snapshot.TooltipText;
            _hasSuccessfulQuota = true;
            _consecutiveRefreshFailures = 0;
        }
        catch (TaskCanceledException)
        {
            ReportRefreshFailure("额度服务响应较慢，正在重试…");
        }
        catch (HttpRequestException)
        {
            ReportRefreshFailure("正在重新连接额度服务…");
        }
        catch
        {
            ReportRefreshFailure("正在重新读取额度数据…");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task<HttpResponseMessage> SendUsageRequestWithRetryAsync(string accessToken, string accountId)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, NetworkClientFactory.UsageEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
                request.Headers.UserAgent.ParseAdd("codex-quota-float/0.3.3");

                var response = await _http.SendAsync(request);
                var shouldRetry = response.StatusCode == HttpStatusCode.TooManyRequests ||
                                  (int)response.StatusCode >= 500;
                if (!shouldRetry || attempt == 2)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                lastError = error;
                if (attempt == 2) throw;
            }

            await Task.Delay(700 * (attempt + 1));
        }

        throw lastError ?? new HttpRequestException("Codex usage request failed.");
    }

    private void ReportRefreshFailure(string firstLoadMessage)
    {
        _consecutiveRefreshFailures++;
        if (!_hasSuccessfulQuota)
        {
            _form.SetError(firstLoadMessage);
        }
        else if (_consecutiveRefreshFailures >= 3)
        {
            _form.SetError("网络波动，当前显示上次成功数据");
        }
    }

    protected override void ExitThreadCore()
    {
        _lifecycleTimer.Stop();
        _refreshTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _http.Dispose();
        _form.Dispose();
        base.ExitThreadCore();
    }
}

internal static class CodexLocator
{
    public static CodexState Find()
    {
        var running = false;
        var window = IntPtr.Zero;

        foreach (var processName in new[] { "ChatGPT", "codex" })
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch { continue; }

            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName ?? string.Empty;
                        if (!path.Contains("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) &&
                            !path.Contains(@"OpenAI\Codex", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        running = true;
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            window = process.MainWindowHandle;
                        }
                    }
                    catch
                    {
                        // Some helper processes disallow MainModule inspection; ignore those.
                    }
                }
            }
        }

        return new CodexState(running, window);
    }
}

internal readonly record struct CodexState(bool Running, IntPtr WindowHandle);

internal sealed record QuotaWindow(string Label, double RemainingPercent, DateTimeOffset? ResetsAt);

internal sealed record QuotaSnapshot(string Plan, IReadOnlyList<QuotaWindow> Windows, string CreditText, DateTimeOffset FetchedAt)
{
    public string TooltipText
    {
        get
        {
            var remaining = Windows.Count > 0 ? string.Format("{0:0}% 剩余", Windows[0].RemainingPercent) : "额度已更新";
            var text = string.Format("Codex {0} · {1}", Plan, remaining);
            return text.Length <= 63 ? text : text[..63];
        }
    }
}

internal static class QuotaParser
{
    public static QuotaSnapshot Parse(JsonElement root)
    {
        if (root.TryGetProperty("rate_limits", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
        {
            root = wrapped;
        }

        var plan = GetString(root, "plan_type") ?? "Codex";
        plan = plan switch
        {
            "plus" => "Plus",
            "pro" => "Pro",
            "free" => "Free",
            "team" => "Team",
            "business" => "Business",
            "enterprise" => "Enterprise",
            "edu" or "education" => "Edu",
            _ => string.IsNullOrWhiteSpace(plan) ? "Codex" : char.ToUpperInvariant(plan[0]) + plan[1..]
        };

        var windows = new List<QuotaWindow>();
        if (root.TryGetProperty("rate_limit", out var rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
        {
            AddWindow(windows, rateLimit, "primary_window", null);
            AddWindow(windows, rateLimit, "secondary_window", null);
        }

        if (root.TryGetProperty("additional_rate_limits", out var additional) && additional.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in additional.EnumerateArray())
            {
                var prefix = GetString(item, "limit_name") ?? GetString(item, "metered_feature");
                if (item.TryGetProperty("rate_limit", out var extraLimit) && extraLimit.ValueKind == JsonValueKind.Object)
                {
                    AddWindow(windows, extraLimit, "primary_window", FriendlyFeature(prefix));
                    AddWindow(windows, extraLimit, "secondary_window", FriendlyFeature(prefix));
                }
            }
        }

        if (root.TryGetProperty("spend_control", out var spendControl) && spendControl.ValueKind == JsonValueKind.Object &&
            spendControl.TryGetProperty("individual_limit", out var individual) && individual.ValueKind == JsonValueKind.Object &&
            TryGetDouble(individual, "remaining_percent", out var spendRemaining))
        {
            var reset = TryGetLong(individual, "reset_at", out var resetAt) ? SafeUnixTime(resetAt) : null;
            windows.Add(new QuotaWindow("月度使用上限", Math.Clamp(spendRemaining, 0, 100), reset));
        }

        var creditText = "未启用加购额度";
        if (root.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
        {
            var unlimited = TryGetBool(credits, "unlimited", out var isUnlimited) && isUnlimited;
            var balance = GetStringOrNumber(credits, "balance");
            var hasCredits = TryGetBool(credits, "has_credits", out var has) && has;
            creditText = unlimited ? "加购额度：不限" : hasCredits ? string.Format("加购余额：{0}", balance ?? "0") : "未启用加购额度";
        }

        return new QuotaSnapshot(plan, windows.Take(3).ToList(), creditText, DateTimeOffset.Now);
    }

    private static void AddWindow(List<QuotaWindow> output, JsonElement parent, string property, string? prefix)
    {
        if (!parent.TryGetProperty(property, out var window) || window.ValueKind != JsonValueKind.Object ||
            !TryGetDouble(window, "used_percent", out var used))
        {
            return;
        }

        var seconds = TryGetLong(window, "limit_window_seconds", out var rawSeconds) ? rawSeconds : 0;
        var label = string.IsNullOrWhiteSpace(prefix) ? FriendlyDuration(seconds) : prefix!;
        var reset = TryGetLong(window, "reset_at", out var resetAt) ? SafeUnixTime(resetAt) : null;
        output.Add(new QuotaWindow(label, Math.Clamp(100 - used, 0, 100), reset));
    }

    private static string FriendlyDuration(long seconds)
    {
        if (seconds is >= 16_200 and <= 19_800) return "5 小时额度";
        if (seconds is >= 518_400 and <= 691_200) return "周额度";
        if (seconds >= 86_400) return string.Format("{0:0.#} 天额度", seconds / 86_400d);
        if (seconds >= 3_600) return string.Format("{0:0.#} 小时额度", seconds / 3_600d);
        return "当前额度";
    }

    private static string FriendlyFeature(string? feature)
    {
        if (string.IsNullOrWhiteSpace(feature)) return "其他额度";
        if (feature.Contains("review", StringComparison.OrdinalIgnoreCase)) return "代码审查额度";
        return feature.Replace('_', ' ');
    }

    private static DateTimeOffset? SafeUnixTime(long seconds)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime(); }
        catch { return null; }
    }

    private static string? GetString(JsonElement parent, string property)
    {
        return parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? GetStringOrNumber(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static bool TryGetDouble(JsonElement parent, string property, out double value)
    {
        value = 0;
        return parent.TryGetProperty(property, out var element) &&
               (element.ValueKind == JsonValueKind.Number ? element.TryGetDouble(out value) :
                element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), out value));
    }

    private static bool TryGetLong(JsonElement parent, string property, out long value)
    {
        value = 0;
        return parent.TryGetProperty(property, out var element) &&
               (element.ValueKind == JsonValueKind.Number ? element.TryGetInt64(out value) :
                element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out value));
    }

    private static bool TryGetBool(JsonElement parent, string property, out bool value)
    {
        value = false;
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        value = element.GetBoolean();
        return true;
    }
}

internal sealed class QuotaForm : Form
{
    private readonly Font _brandFont = new("Microsoft YaHei UI", 8.2f, FontStyle.Bold);
    private readonly Font _timeFont = new("Segoe UI Light", 31f);
    private readonly Font _secondsFont = new("Segoe UI", 12f);
    private readonly Font _dateFont = new("Microsoft YaHei UI", 8.4f);
    private readonly Font _verticalFont = new("KaiTi", 14.5f);
    private readonly Font _labelFont = new("Microsoft YaHei UI", 8.6f);
    private readonly Font _valueFont = new("Segoe UI Semibold", 17f);
    private readonly Font _smallFont = new("Microsoft YaHei UI", 7.5f);
    private readonly System.Windows.Forms.Timer _clockTimer = new() { Interval = 1000 };
    private QuotaSnapshot? _snapshot;
    private string _message = "等待 Codex 启动…";
    private string? _error;
    private bool _dragging;
    private bool _dragMoved;
    private bool _layerTogglePressed;
    private bool _alwaysOnTop;
    private bool _savedPositionLoaded;
    private IntPtr _codexOwner;
    private Point _dragStartCursor;
    private Point _dragStartWindow;

    public event EventHandler? RefreshRequested;
    public event EventHandler? MenuRequested;
    public event Action<bool>? AlwaysOnTopChanged;
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool UserPositioned { get; private set; }

    public QuotaForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(227, 232, 226);
        ClientSize = new Size(342, 230);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        Opacity = 0.99;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = false;
        DoubleBuffered = true;
        Cursor = Cursors.SizeAll;

        try { UserPositioned = HasSavedManualPosition(); }
        catch { UserPositioned = false; }

        _clockTimer.Tick += (_, _) => Invalidate(new Rectangle(18, 32, Width - 100, 86));
        _clockTimer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_savedPositionLoaded) return;
        _savedPositionLoaded = true;
        LoadSavedPosition();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x00020000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void SetWaiting(string message)
    {
        _message = message;
        _error = null;
        _snapshot = null;
        UpdateHeight();
        Invalidate();
    }

    public void SetSnapshot(QuotaSnapshot snapshot)
    {
        _snapshot = snapshot;
        _error = null;
        UpdateHeight();
        Invalidate();
    }

    public void SetError(string error)
    {
        _error = error;
        Invalidate();
    }

    public void SetAlwaysOnTop(bool enabled)
    {
        ApplyAlwaysOnTop(enabled, persist: true);
    }

    internal void AttachToCodexWindow(IntPtr ownerHandle)
    {
        if (ownerHandle == IntPtr.Zero || !IsHandleCreated) return;
        if (_codexOwner != ownerHandle)
        {
            NativeMethods.SetWindowOwner(Handle, ownerHandle);
            _codexOwner = ownerHandle;
            ApplyZOrderMode();
        }
    }

    internal void DetachFromCodexWindow()
    {
        if (!IsHandleCreated || _codexOwner == IntPtr.Zero) return;
        NativeMethods.SetWindowOwner(Handle, IntPtr.Zero);
        _codexOwner = IntPtr.Zero;
    }

    public void ResetToAutoDock()
    {
        UserPositioned = false;
        SaveSettings();
    }

    internal bool HasManualPositionOrIsDragging() => UserPositioned || _dragging;

    private void ApplyAlwaysOnTop(bool enabled, bool persist)
    {
        var changed = _alwaysOnTop != enabled;
        _alwaysOnTop = enabled;
        ApplyZOrderMode();
        if (persist) SaveSettings();
        Invalidate(LayerToggleRect);
        if (changed) AlwaysOnTopChanged?.Invoke(enabled);
    }

    private void ApplyZOrderMode()
    {
        TopMost = _alwaysOnTop;
        if (!IsHandleCreated) return;
        var flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
        if (_alwaysOnTop)
        {
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags);
        }
        else
        {
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOP, 0, 0, 0, 0, flags);
        }
    }

    private Rectangle LayerToggleRect => new(Width - 69, Height - 82, 42, 60);

    private void UpdateHeight()
    {
        var count = Math.Max(1, _snapshot?.Windows.Count ?? 0);
        Height = 180 + Math.Min(3, count) * 64;
        if (UserPositioned) Location = ClampToScreen(Location);
        UpdateRegion();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRegion();
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        using var path = RoundedRect(new Rectangle(0, 0, Width, Height), 18);
        Region?.Dispose();
        Region = new Region(path);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (LayerToggleRect.Contains(e.Location))
        {
            _layerTogglePressed = true;
            Capture = true;
            Invalidate(LayerToggleRect);
            return;
        }
        _dragging = true;
        _dragMoved = false;
        _dragStartCursor = Cursor.Position;
        _dragStartWindow = Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            Cursor = LayerToggleRect.Contains(e.Location) ? Cursors.Hand : Cursors.SizeAll;
        }
        if (_layerTogglePressed) return;
        if (!_dragging) return;
        var deltaX = Cursor.Position.X - _dragStartCursor.X;
        var deltaY = Cursor.Position.Y - _dragStartCursor.Y;
        if (Math.Abs(deltaX) + Math.Abs(deltaY) >= 3) _dragMoved = true;
        Location = new Point(_dragStartWindow.X + deltaX, _dragStartWindow.Y + deltaY);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            MenuRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (e.Button == MouseButtons.Left && _layerTogglePressed)
        {
            _layerTogglePressed = false;
            Capture = false;
            if (LayerToggleRect.Contains(e.Location)) SetAlwaysOnTop(!_alwaysOnTop);
            Invalidate(LayerToggleRect);
            return;
        }
        if (e.Button != MouseButtons.Left || !_dragging) return;

        _dragging = false;
        Capture = false;
        if (_dragMoved)
        {
            Location = ClampToScreen(Location);
            UserPositioned = true;
            SaveSettings();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (LayerToggleRect.Contains(e.Location)) return;
        if (!_dragMoved) RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        DrawBackground(g);
        DrawTime(g);
        DrawVerticalTitle(g);

        if (_snapshot is null || _snapshot.Windows.Count == 0)
        {
            using var messageBrush = new SolidBrush(Color.FromArgb(111, 108, 99));
            g.DrawString(_error ?? _message, _labelFont, messageBrush, new RectangleF(22, 140, Width - 112, 42));
        }
        else
        {
            var y = 132;
            foreach (var window in _snapshot.Windows)
            {
                DrawQuotaWindow(g, window, y);
                y += 64;
            }
        }

        DrawFooter(g);
    }

    private void DrawBackground(Graphics g)
    {
        using var outer = new SolidBrush(Color.FromArgb(226, 232, 226));
        using var outerPath = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 18);
        g.FillPath(outer, outerPath);

        var cardRect = new Rectangle(10, 10, Width - 82, Height - 20);
        using var cardPath = RoundedRect(cardRect, 14);
        using var card = new SolidBrush(Color.FromArgb(250, 249, 244));
        using var cardBorder = new Pen(Color.FromArgb(223, 217, 204));
        g.FillPath(card, cardPath);
        g.DrawPath(cardBorder, cardPath);

        using var border = new Pen(Color.FromArgb(196, 201, 193));
        g.DrawPath(border, outerPath);
    }

    private void DrawTime(Graphics g)
    {
        var now = DateTime.Now;
        var plan = _snapshot?.Plan?.ToUpperInvariant() ?? "LIVE";
        using var accent = new SolidBrush(Color.FromArgb(174, 132, 66));
        using var ink = new SolidBrush(Color.FromArgb(45, 45, 41));
        using var muted = new SolidBrush(Color.FromArgb(122, 119, 109));

        g.DrawString(string.Format("CODEX  ·  {0}", plan), _brandFont, accent, 22, 20);

        var hourMinute = now.ToString("HH:mm");
        using var typographic = (StringFormat)StringFormat.GenericTypographic.Clone();
        g.DrawString(hourMinute, _timeFont, ink, new PointF(18, 34), typographic);
        var timeWidth = g.MeasureString(hourMinute, _timeFont, int.MaxValue, typographic).Width;
        g.DrawString(now.ToString(":ss"), _secondsFont, accent, new PointF(20 + timeWidth, 59), typographic);

        var dateText = string.Format("{0:MM 月 dd 日}  ·  {1}", now, ChineseWeekday(now.DayOfWeek));
        g.DrawString(dateText, _dateFont, muted, 22, 103);
        using var line = new Pen(Color.FromArgb(222, 216, 203));
        g.DrawLine(line, 22, 123, Width - 94, 123);
    }

    private void DrawVerticalTitle(Graphics g)
    {
        var x = Width - 48;
        var y = 28;
        using var ink = new SolidBrush(Color.FromArgb(52, 54, 49));
        using var format = new StringFormat { Alignment = StringAlignment.Center };
        foreach (var character in "额度余量")
        {
            g.DrawString(character.ToString(), _verticalFont, ink, new RectangleF(x - 14, y, 28, 25), format);
            y += 27;
        }
        using var seal = new SolidBrush(Color.FromArgb(176, 66, 53));
        g.FillEllipse(seal, x - 3, y + 8, 6, 6);
        DrawLayerToggle(g);
    }

    private void DrawLayerToggle(Graphics g)
    {
        var rect = LayerToggleRect;
        using var outerPath = RoundedRect(rect, 12);
        using var outer = new SolidBrush(Color.FromArgb(244, 242, 234));
        using var border = new Pen(Color.FromArgb(202, 201, 191));
        g.FillPath(outer, outerPath);
        g.DrawPath(border, outerPath);

        var selected = _alwaysOnTop
            ? new Rectangle(rect.X + 2, rect.Y + rect.Height / 2, rect.Width - 4, rect.Height / 2 - 2)
            : new Rectangle(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height / 2 - 2);
        using var selectedPath = RoundedRect(selected, 10);
        using var selectedBrush = new SolidBrush(_alwaysOnTop
            ? Color.FromArgb(177, 137, 70)
            : Color.FromArgb(100, 117, 104));
        g.FillPath(selectedBrush, selectedPath);

        using var divider = new Pen(Color.FromArgb(218, 214, 203));
        g.DrawLine(divider, rect.X + 7, rect.Y + rect.Height / 2, rect.Right - 7, rect.Y + rect.Height / 2);

        using var selectedText = new SolidBrush(Color.FromArgb(255, 253, 246));
        using var normalText = new SolidBrush(Color.FromArgb(104, 103, 96));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var topRect = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height / 2f);
        var bottomRect = new RectangleF(rect.X, rect.Y + rect.Height / 2f, rect.Width, rect.Height / 2f);
        g.DrawString("同层", _smallFont, _alwaysOnTop ? normalText : selectedText, topRect, format);
        g.DrawString("最前", _smallFont, _alwaysOnTop ? selectedText : normalText, bottomRect, format);
    }

    private void DrawQuotaWindow(Graphics g, QuotaWindow window, int y)
    {
        using var labelBrush = new SolidBrush(Color.FromArgb(103, 101, 94));
        using var valueBrush = new SolidBrush(Color.FromArgb(48, 47, 43));
        using var accentBrush = new SolidBrush(QuotaColor(window.RemainingPercent));
        g.DrawString(window.Label, _labelFont, labelBrush, 22, y + 2);

        var value = string.Format("{0:0}%", window.RemainingPercent);
        var valueSize = g.MeasureString(value, _valueFont);
        g.DrawString(value, _valueFont, valueBrush, Width - 94 - valueSize.Width, y - 5);
        g.DrawString("余", _smallFont, accentBrush, Width - 90, y + 8);

        var track = new Rectangle(22, y + 29, Width - 116, 6);
        using var trackPath = RoundedRect(track, 3);
        using var trackBrush = new SolidBrush(Color.FromArgb(226, 220, 208));
        g.FillPath(trackBrush, trackPath);

        var fillWidth = Math.Max(6, (int)Math.Round(track.Width * window.RemainingPercent / 100d));
        var fillRect = new Rectangle(track.X, track.Y, Math.Min(track.Width, fillWidth), track.Height);
        using var fillPath = RoundedRect(fillRect, 3);
        g.FillPath(accentBrush, fillPath);

        var resetText = window.ResetsAt.HasValue
            ? string.Format("{0:MM月dd日 HH:mm} 重置", window.ResetsAt.Value)
            : "重置时间暂不可用";
        using var resetBrush = new SolidBrush(Color.FromArgb(139, 135, 124));
        g.DrawString(resetText, _smallFont, resetBrush, 22, y + 41);
    }

    private void DrawFooter(Graphics g)
    {
        var left = _snapshot?.CreditText ?? "每 30 秒自动更新";
        if (!string.IsNullOrEmpty(_error)) left = _error;

        using var linePen = new Pen(Color.FromArgb(222, 216, 203));
        g.DrawLine(linePen, 22, Height - 40, Width - 94, Height - 40);
        using var footerBrush = new SolidBrush(_error is null ? Color.FromArgb(126, 122, 112) : Color.FromArgb(176, 86, 53));
        g.DrawString(left, _smallFont, footerBrush, 22, Height - 31);

        const string action = "双击刷新";
        var size = g.MeasureString(action, _smallFont);
        g.DrawString(action, _smallFont, footerBrush, Width - 94 - size.Width, Height - 31);
    }

    private void LoadSavedPosition()
    {
        try
        {
            if (!File.Exists(PositionFilePath)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(PositionFilePath));
            var root = document.RootElement;
            var hasPosition = root.TryGetProperty("hasPosition", out var hasPositionValue)
                ? hasPositionValue.GetBoolean()
                : root.TryGetProperty("x", out _) && root.TryGetProperty("y", out _);
            if (hasPosition)
            {
                var point = new Point(root.GetProperty("x").GetInt32(), root.GetProperty("y").GetInt32());
                Location = ClampToScreen(point);
                UserPositioned = true;
            }
            else
            {
                UserPositioned = false;
            }

            var alwaysOnTop = root.TryGetProperty("alwaysOnTop", out var topValue) && topValue.ValueKind == JsonValueKind.True;
            ApplyAlwaysOnTop(alwaysOnTop, persist: false);
        }
        catch
        {
            UserPositioned = false;
            ApplyAlwaysOnTop(false, persist: false);
        }
    }

    private void SaveSettings()
    {
        try
        {
            var directory = Path.GetDirectoryName(PositionFilePath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(PositionFilePath, JsonSerializer.Serialize(new
            {
                x = Left,
                y = Top,
                hasPosition = UserPositioned,
                alwaysOnTop = _alwaysOnTop
            }));
        }
        catch { }
    }

    private static bool HasSavedManualPosition()
    {
        if (!File.Exists(PositionFilePath)) return false;
        using var document = JsonDocument.Parse(File.ReadAllText(PositionFilePath));
        var root = document.RootElement;
        if (root.TryGetProperty("hasPosition", out var hasPosition)) return hasPosition.GetBoolean();
        return root.TryGetProperty("x", out _) && root.TryGetProperty("y", out _);
    }

    private Point ClampToScreen(Point point)
    {
        var screen = Screen.FromPoint(new Point(point.X + Width / 2, point.Y + Height / 2));
        var work = screen.WorkingArea;
        return new Point(
            Math.Clamp(point.X, work.Left + 8, work.Right - Width - 8),
            Math.Clamp(point.Y, work.Top + 8, work.Bottom - Height - 8));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clockTimer.Stop();
            _clockTimer.Dispose();
            _brandFont.Dispose();
            _timeFont.Dispose();
            _secondsFont.Dispose();
            _dateFont.Dispose();
            _verticalFont.Dispose();
            _labelFont.Dispose();
            _valueFont.Dispose();
            _smallFont.Dispose();
        }
        base.Dispose(disposing);
    }

    private static string ChineseWeekday(DayOfWeek day)
    {
        return day switch
        {
            DayOfWeek.Monday => "星期一",
            DayOfWeek.Tuesday => "星期二",
            DayOfWeek.Wednesday => "星期三",
            DayOfWeek.Thursday => "星期四",
            DayOfWeek.Friday => "星期五",
            DayOfWeek.Saturday => "星期六",
            _ => "星期日"
        };
    }

    private static Color QuotaColor(double remaining)
    {
        if (remaining >= 50) return Color.FromArgb(177, 137, 70);
        if (remaining >= 20) return Color.FromArgb(190, 103, 52);
        return Color.FromArgb(174, 61, 50);
    }

    private static string PositionFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexQuotaFloat",
        "window-position.json");

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class DarkColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Color.FromArgb(250, 249, 244);
    public override Color MenuItemSelected => Color.FromArgb(232, 226, 214);
    public override Color MenuItemBorder => Color.FromArgb(200, 191, 174);
    public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
    public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
    public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
    public override Color SeparatorDark => Color.FromArgb(214, 207, 194);
    public override Color SeparatorLight => Color.FromArgb(214, 207, 194);
}

internal static class IconFactory
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var background = new SolidBrush(Color.FromArgb(25, 30, 41));
        graphics.FillEllipse(background, 1, 1, 30, 30);
        using var pen = new Pen(Color.FromArgb(94, 229, 198), 3.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawLine(pen, 10, 10, 22, 22);
        graphics.DrawLine(pen, 22, 10, 10, 22);
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { NativeMethods.DestroyIcon(handle); }
    }
}

internal static class NativeMethods
{
    internal static readonly IntPtr HWND_TOP = IntPtr.Zero;
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal static readonly IntPtr HWND_NOTOPMOST = new(-2);
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOACTIVATE = 0x0010;
    private const int GWLP_HWNDPARENT = -8;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    internal static IntPtr SetWindowOwner(IntPtr windowHandle, IntPtr ownerHandle)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, GWLP_HWNDPARENT, ownerHandle)
            : new IntPtr(SetWindowLong32(windowHandle, GWLP_HWNDPARENT, ownerHandle.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr newValue);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int index, int newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr handle);
}
