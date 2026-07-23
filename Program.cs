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
    // Process enumeration can briefly miss Store/Electron helper processes while the desktop app
    // is rebuilding its window. Do not hide the float based on a single transient observation.
    private const int MissingCodexChecksBeforeHiding = 4;
    private readonly QuotaForm _form = new();
    private readonly SkinController _skin = new();
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _lifecycleTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 30_000 };
    private HttpClient _http = NetworkClientFactory.Create();
    private ToolStripMenuItem? _topMostMenuItem;
    private ToolStripMenuItem? _skinStatusMenuItem;
    private ToolStripMenuItem? _skinEnableMenuItem;
    private ToolStripMenuItem? _skinReloadMenuItem;
    private ToolStripMenuItem? _skinRestoreMenuItem;
    private bool _refreshing;
    private bool _skinCommandRunning;
    private bool _codexRunning;
    private bool _hasSuccessfulQuota;
    private int _notRunningChecks;
    private int _consecutiveRefreshFailures;
    private bool _skillsRefreshing;

    public QuotaApplicationContext()
    {
        _trayIcon = new NotifyIcon
        {
            Icon = IconFactory.Create(SystemInformation.SmallIconSize),
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
        _form.SkinActionRequested += (_, _) => _ = RunPrimarySkinActionAsync();
        _form.SkillsToggleRequested += (_, _) =>
        {
            var expanded = _form.ToggleSkillsPanel();
            if (expanded) _ = RefreshSkillsAsync();
            DockToCodex();
        };
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
        menu.Items.Add("显示可用 Skills", null, (_, _) => _ = ShowSkillsPanelAsync());

        var skinMenu = new ToolStripMenuItem("界面皮肤");
        _skinStatusMenuItem = new ToolStripMenuItem("状态：检查中…") { Enabled = false };
        _skinEnableMenuItem = new ToolStripMenuItem("启用皮肤（Codex 重启一次）");
        _skinEnableMenuItem.Click += (_, _) => _ = RunSkinCommandAsync(SkinCommand.Enable);
        _skinReloadMenuItem = new ToolStripMenuItem("热加载样式");
        _skinReloadMenuItem.Click += (_, _) => _ = RunSkinCommandAsync(SkinCommand.HotReload);
        _skinRestoreMenuItem = new ToolStripMenuItem("恢复官方样式");
        _skinRestoreMenuItem.Click += (_, _) => _ = RunSkinCommandAsync(SkinCommand.Restore);
        skinMenu.DropDownItems.AddRange([
            _skinStatusMenuItem,
            new ToolStripSeparator(),
            _skinEnableMenuItem,
            _skinReloadMenuItem,
            _skinRestoreMenuItem
        ]);
        menu.Items.Add(skinMenu);

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
        UpdateSkinUi();
        var state = CodexLocator.Find();
        if (state.Running)
        {
            _notRunningChecks = 0;
            if (!_codexRunning)
            {
                _codexRunning = true;
                UpdateSkinUi();
                _trayIcon.Text = "Codex 额度悬浮窗（自动更新中）";
                _form.SetWaiting("正在读取当前账户额度…");
                _form.Show();
                _ = RefreshQuotaAsync();
            }

            // An owned WinForms window is hidden by Windows when the Codex owner is minimized.
            // It can remain hidden after the owner is restored or recreated because the lifecycle
            // state still says "running". Reassert the visible state whenever the Codex window is
            // usable; this is UI recovery only and never starts or restarts Codex.
            if (state.ShouldShowFloat)
            {
                if (!_form.Visible)
                {
                    _form.Show();
                }
            }
            else if (_form.Visible)
            {
                _form.Hide();
            }

            if (_form.Visible && state.CodexWindowHandle != IntPtr.Zero)
            {
                _form.AttachToCodexWindow(state.CodexWindowHandle);
            }
            else
            {
                _form.DetachFromCodexWindow();
            }

            if (_form.Visible && !_form.HasManualPositionOrIsDragging())
            {
                DockToCodex(state.DockWindowHandle);
            }
            return;
        }

        _notRunningChecks++;
        if (_notRunningChecks < MissingCodexChecksBeforeHiding || !_codexRunning)
        {
            return;
        }

        _codexRunning = false;
        UpdateSkinUi();
        _form.Hide();
        _form.DetachFromCodexWindow();
        _trayIcon.Text = "Codex 额度悬浮窗（等待 Codex）";
    }

    private async Task RunPrimarySkinActionAsync()
    {
        var status = _skin.GetStatus();
        if (!status.Available)
        {
            ShowSkinNotification(false, "未找到 Codex Dream Skin 安装组件");
            return;
        }

        await RunSkinCommandAsync(status.IsActive ? SkinCommand.HotReload : SkinCommand.Enable);
    }

    private async Task RunSkinCommandAsync(SkinCommand command)
    {
        if (_skinCommandRunning) return;

        var status = _skin.GetStatus();
        if (!status.Available)
        {
            ShowSkinNotification(false, "未找到 Codex Dream Skin 安装组件");
            return;
        }

        if ((command is SkinCommand.HotReload or SkinCommand.Restore) && !status.IsActive)
        {
            ShowSkinNotification(false, "皮肤当前未启用");
            UpdateSkinUi(status);
            return;
        }

        _skinCommandRunning = true;
        UpdateSkinUi(status);
        try
        {
            var result = command switch
            {
                SkinCommand.Enable => await _skin.EnableAsync(),
                SkinCommand.HotReload => await _skin.HotReloadAsync(),
                SkinCommand.Restore => await _skin.RestoreAsync(),
                _ => new SkinCommandResult(false, "未知的皮肤操作")
            };
            var message = string.IsNullOrWhiteSpace(result.Details)
                ? result.Message
                : $"{result.Message}：{result.Details}";
            ShowSkinNotification(result.Success, message);
        }
        finally
        {
            _skinCommandRunning = false;
            UpdateSkinUi();
        }
    }

    private void UpdateSkinUi(SkinStatus? knownStatus = null)
    {
        var status = knownStatus ?? _skin.GetStatus();
        var label = _skinCommandRunning ? "处理中…" : status.Label;
        _form.SetSkinState(label, status.IsActive, _skinCommandRunning);

        if (_skinStatusMenuItem is not null)
        {
            _skinStatusMenuItem.Text = _skinCommandRunning ? "状态：处理中…" : $"状态：{StatusText(status)}";
        }
        if (_skinEnableMenuItem is not null)
        {
            _skinEnableMenuItem.Enabled = !_skinCommandRunning && status.Available && !status.IsActive && _codexRunning;
        }
        if (_skinReloadMenuItem is not null)
        {
            _skinReloadMenuItem.Enabled = !_skinCommandRunning && status.IsActive;
        }
        if (_skinRestoreMenuItem is not null)
        {
            _skinRestoreMenuItem.Enabled = !_skinCommandRunning && status.IsActive;
        }
    }

    private void ShowSkinNotification(bool success, string message)
    {
        _trayIcon.BalloonTipTitle = success ? "Codex 皮肤" : "Codex 皮肤操作未完成";
        _trayIcon.BalloonTipText = message.Length <= 240 ? message : message[..240] + "…";
        _trayIcon.BalloonTipIcon = success ? ToolTipIcon.Info : ToolTipIcon.Warning;
        _trayIcon.ShowBalloonTip(3500);
    }

    private static string StatusText(SkinStatus status) => status.Mode switch
    {
        SkinMode.Unavailable => "组件未安装",
        SkinMode.Active => "已启用",
        _ => "未启用"
    };

    private void DockToCodex(IntPtr knownWindow = default)
    {
        if (_form.HasManualPositionOrIsDragging())
        {
            return;
        }

        var window = knownWindow != IntPtr.Zero ? knownWindow : CodexLocator.Find().DockWindowHandle;
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
            try
            {
                await WriteQuotaSnapshotAsync(snapshot);
            }
            catch
            {
                // Sharing the sanitized snapshot must never turn a successful quota refresh into a UI failure.
            }
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

    private async Task ShowSkillsPanelAsync()
    {
        if (!_form.SkillsExpanded)
        {
            _form.SetSkillsExpanded(true);
            DockToCodex();
        }

        await RefreshSkillsAsync();
    }

    private async Task RefreshSkillsAsync()
    {
        if (_skillsRefreshing)
        {
            return;
        }

        _skillsRefreshing = true;
        _form.SetSkillsLoading(true);
        try
        {
            var snapshot = await Task.Run(SkillCatalog.Discover);
            _form.SetSkillCatalog(snapshot);
        }
        catch
        {
            _form.SetSkillCatalog(new SkillCatalogSnapshot(DateTime.Now, Array.Empty<CodexSkill>()));
        }
        finally
        {
            _skillsRefreshing = false;
            _form.SetSkillsLoading(false);
        }
    }

    private static async Task WriteQuotaSnapshotAsync(QuotaSnapshot snapshot)
    {
        var weekly = snapshot.Windows
            .Where(window => window.WindowSeconds >= 5 * 24 * 60 * 60)
            .OrderByDescending(window => window.WindowSeconds)
            .FirstOrDefault();
        if (weekly is null)
        {
            return;
        }

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaFloat");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "quota-snapshot.json");
        var temporary = Path.Combine(directory, $"quota-snapshot-{Guid.NewGuid():N}.tmp");
        var payload = new
        {
            schemaVersion = 1,
            fetchedAt = snapshot.FetchedAt,
            plan = snapshot.Plan,
            weekly = new
            {
                remainingPercent = weekly.RemainingPercent,
                windowSeconds = weekly.WindowSeconds,
                resetAt = weekly.ResetsAt,
            },
        };
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(payload));
        File.Move(temporary, target, true);
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
                request.Headers.UserAgent.ParseAdd("codex-quota-float/0.4.0");

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
                ResetHttpClient();
                if (attempt == 2) throw;
            }

            await Task.Delay(700 * (attempt + 1));
        }

        throw lastError ?? new HttpRequestException("Codex usage request failed.");
    }

    private void ResetHttpClient()
    {
        var previous = _http;
        _http = NetworkClientFactory.Create();
        previous.Dispose();
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
        _skin.Dispose();
        _form.Dispose();
        base.ExitThreadCore();
    }
}

internal enum SkinCommand
{
    Enable,
    HotReload,
    Restore
}

internal static class CodexLocator
{
    public static CodexState Find()
    {
        var running = false;
        var codexWindow = IntPtr.Zero;
        var fallbackWindow = IntPtr.Zero;

        foreach (var processName in new[] { "ChatGPT", "codex", "codex-plus-plus-manager" })
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
                        // Store-installed Codex can deny MainModule inspection. The exact desktop process
                        // names are sufficient for ownership selection and let the real Codex window win.
                        var isCodexDesktop = processName is "ChatGPT" or "codex";
                        var path = isCodexDesktop ? string.Empty : process.MainModule?.FileName ?? string.Empty;
                        var isCodexPlusPlus = processName == "codex-plus-plus-manager" &&
                                               path.EndsWith(@"Codex++\codex-plus-plus-manager.exe", StringComparison.OrdinalIgnoreCase);
                        if (!isCodexDesktop && !isCodexPlusPlus)
                        {
                            continue;
                        }

                        running = true;
                        if (process.MainWindowHandle != IntPtr.Zero)
                        {
                            if (isCodexDesktop)
                            {
                                codexWindow = process.MainWindowHandle;
                            }
                            else if (fallbackWindow == IntPtr.Zero)
                            {
                                fallbackWindow = process.MainWindowHandle;
                            }
                        }
                    }
                    catch
                    {
                        // Some helper processes disallow MainModule inspection; ignore those.
                    }
                }
            }
        }

        return new CodexState(running, codexWindow, fallbackWindow);
    }
}

internal readonly record struct CodexState(bool Running, IntPtr CodexWindowHandle, IntPtr FallbackWindowHandle)
{
    public IntPtr DockWindowHandle => CodexWindowHandle != IntPtr.Zero ? CodexWindowHandle : FallbackWindowHandle;

    // No main window is common during app startup, so retain the prior behavior and show the
    // float. When a window is known to be minimized or hidden, keep the float hidden with Codex.
    public bool ShouldShowFloat => CodexWindowHandle == IntPtr.Zero ||
                                   (NativeMethods.IsWindowVisible(CodexWindowHandle) && !NativeMethods.IsIconic(CodexWindowHandle));
}

internal sealed record QuotaWindow(string Label, double RemainingPercent, DateTimeOffset? ResetsAt, long WindowSeconds = 0);

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
            windows.Add(new QuotaWindow("月度使用上限", Math.Clamp(spendRemaining, 0, 100), reset, 30 * 24 * 60 * 60));
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
        output.Add(new QuotaWindow(label, Math.Clamp(100 - used, 0, 100), reset, seconds));
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
    private readonly Font _skillTitleFont = new("Microsoft YaHei UI", 9f, FontStyle.Bold);
    private readonly Font _skillNameFont = new("Segoe UI Semibold", 8.6f);
    private readonly Font _skillDescriptionFont = new("Microsoft YaHei UI", 7.3f);
    private readonly System.Windows.Forms.Timer _clockTimer = new() { Interval = 1000 };
    private QuotaSnapshot? _snapshot;
    private string _message = "等待 Codex 启动…";
    private string? _error;
    private bool _dragging;
    private bool _dragMoved;
    private bool _layerTogglePressed;
    private bool _skinButtonPressed;
    private bool _skinActive;
    private bool _skinBusy;
    private string _skinLabel = "启用皮肤";
    private bool _alwaysOnTop;
    private bool _skillsExpanded;
    private bool _skillsTogglePressed;
    private bool _skillsLoading;
    private int _skillScrollIndex;
    private SkillCatalogSnapshot _skillCatalog = new(DateTime.MinValue, Array.Empty<CodexSkill>());
    private bool _savedPositionLoaded;
    private IntPtr _codexOwner;
    private Point _dragStartCursor;
    private Point _dragStartWindow;

    public event EventHandler? RefreshRequested;
    public event EventHandler? SkinActionRequested;
    public event EventHandler? SkillsToggleRequested;
    public event EventHandler? MenuRequested;
    public event Action<bool>? AlwaysOnTopChanged;
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool UserPositioned { get; private set; }
    internal bool SkillsExpanded => _skillsExpanded;

    public QuotaForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(227, 232, 226);
        ClientSize = new Size(342, 230);
        Text = "Codex 额度悬浮窗";
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        Opacity = 0.99;
        ShowInTaskbar = false;
        Icon = IconFactory.Create(new Size(32, 32));
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

    public bool ToggleSkillsPanel()
    {
        SetSkillsExpanded(!_skillsExpanded);
        return _skillsExpanded;
    }

    public void SetSkillsExpanded(bool expanded)
    {
        if (_skillsExpanded == expanded)
        {
            return;
        }

        _skillsExpanded = expanded;
        _skillScrollIndex = 0;
        UpdateHeight();
        SaveSettings();
        Invalidate();
    }

    public void SetSkillsLoading(bool loading)
    {
        if (_skillsLoading == loading)
        {
            return;
        }

        _skillsLoading = loading;
        Invalidate(SkillPanelRect);
    }

    public void SetSkillCatalog(SkillCatalogSnapshot catalog)
    {
        _skillCatalog = catalog;
        _skillScrollIndex = Math.Min(_skillScrollIndex, MaxSkillScrollIndex);
        Invalidate(SkillPanelRect);
    }

    public void SetError(string error)
    {
        _error = error;
        Invalidate();
    }

    public void SetSkinState(string label, bool active, bool busy)
    {
        if (_skinLabel == label && _skinActive == active && _skinBusy == busy) return;
        _skinLabel = label;
        _skinActive = active;
        _skinBusy = busy;
        Invalidate(SkinButtonRect);
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
    private Rectangle SkinButtonRect => new(Width - 150, Height - 35, 62, 22);
    private Rectangle SkillsToggleRect => new(Width - 69, 112, 42, 22);
    private Rectangle SkillPanelRect
    {
        get
        {
            var count = Math.Max(1, _snapshot?.Windows.Count ?? 0);
            var top = 132 + Math.Min(3, count) * 64 + 2;
            return new Rectangle(22, top, Width - 116, Math.Max(0, Height - top - 52));
        }
    }

    private int VisibleSkillRows => Math.Max(1, (SkillPanelRect.Height - 42) / 44);
    private int MaxSkillScrollIndex => Math.Max(0, _skillCatalog.Skills.Count - VisibleSkillRows);

    private void UpdateHeight()
    {
        var count = Math.Max(1, _snapshot?.Windows.Count ?? 0);
        var compactHeight = 180 + Math.Min(3, count) * 64;
        ClientSize = _skillsExpanded
            ? new Size(564, Math.Max(620, compactHeight + 370))
            : new Size(342, compactHeight);
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
        if (SkillsToggleRect.Contains(e.Location))
        {
            _skillsTogglePressed = true;
            Capture = true;
            Invalidate(SkillsToggleRect);
            return;
        }
        if (SkinButtonRect.Contains(e.Location) && !_skinBusy)
        {
            _skinButtonPressed = true;
            Capture = true;
            Invalidate(SkinButtonRect);
            return;
        }
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
            var overAction = LayerToggleRect.Contains(e.Location)
                || SkillsToggleRect.Contains(e.Location)
                || (SkinButtonRect.Contains(e.Location) && !_skinBusy);
            Cursor = overAction ? Cursors.Hand : Cursors.SizeAll;
        }
        if (_skinButtonPressed) return;
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
        if (e.Button == MouseButtons.Left && _skinButtonPressed)
        {
            _skinButtonPressed = false;
            Capture = false;
            if (SkinButtonRect.Contains(e.Location) && !_skinBusy)
            {
                SkinActionRequested?.Invoke(this, EventArgs.Empty);
            }
            Invalidate(SkinButtonRect);
            return;
        }
        if (e.Button == MouseButtons.Left && _skillsTogglePressed)
        {
            _skillsTogglePressed = false;
            Capture = false;
            if (SkillsToggleRect.Contains(e.Location))
            {
                SkillsToggleRequested?.Invoke(this, EventArgs.Empty);
            }

            Invalidate(SkillsToggleRect);
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
        if (LayerToggleRect.Contains(e.Location) || SkinButtonRect.Contains(e.Location) || SkillsToggleRect.Contains(e.Location)) return;
        if (!_dragMoved) RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!_skillsExpanded || !SkillPanelRect.Contains(e.Location) || _skillCatalog.Skills.Count == 0)
        {
            return;
        }

        var next = _skillScrollIndex - Math.Sign(e.Delta) * 3;
        _skillScrollIndex = Math.Clamp(next, 0, MaxSkillScrollIndex);
        Invalidate(SkillPanelRect);
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

        if (_skillsExpanded)
        {
            DrawSkillsPanel(g);
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
        var y = 42;
        using var ink = new SolidBrush(Color.FromArgb(52, 54, 49));
        using var format = new StringFormat { Alignment = StringAlignment.Center };
        foreach (var character in "额度")
        {
            g.DrawString(character.ToString(), _verticalFont, ink, new RectangleF(x - 14, y, 28, 25), format);
            y += 27;
        }
        using var seal = new SolidBrush(Color.FromArgb(176, 66, 53));
        g.FillEllipse(seal, x - 3, y + 5, 6, 6);
        DrawSkillsToggle(g);
        DrawLayerToggle(g);
    }

    private void DrawSkillsToggle(Graphics g)
    {
        var rect = SkillsToggleRect;
        var fillColor = _skillsExpanded ? Color.FromArgb(100, 117, 104) : Color.FromArgb(244, 238, 221);
        if (_skillsTogglePressed)
        {
            fillColor = _skillsExpanded ? Color.FromArgb(84, 100, 90) : Color.FromArgb(231, 218, 187);
        }

        using var path = RoundedRect(rect, 9);
        using var fill = new SolidBrush(fillColor);
        using var border = new Pen(_skillsExpanded ? Color.FromArgb(77, 99, 82) : Color.FromArgb(191, 165, 107));
        using var text = new SolidBrush(_skillsExpanded ? Color.FromArgb(255, 253, 246) : Color.FromArgb(104, 79, 39));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        g.DrawString(_skillsExpanded ? "收起" : "技能", _smallFont, text, rect, format);
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

    private void DrawSkillsPanel(Graphics g)
    {
        var panel = SkillPanelRect;
        if (panel.Height <= 0)
        {
            return;
        }

        using var panelPath = RoundedRect(panel, 11);
        using var panelFill = new SolidBrush(Color.FromArgb(244, 246, 239));
        using var panelBorder = new Pen(Color.FromArgb(210, 216, 205));
        using var titleBrush = new SolidBrush(Color.FromArgb(64, 76, 64));
        using var mutedBrush = new SolidBrush(Color.FromArgb(119, 124, 114));
        g.FillPath(panelFill, panelPath);
        g.DrawPath(panelBorder, panelPath);

        g.DrawString("最近安装的 Skills", _skillTitleFont, titleBrush, panel.X + 12, panel.Y + 9);
        var status = _skillsLoading
            ? "扫描中…"
            : _skillCatalog.RefreshedAt == DateTime.MinValue
                ? "点击刷新"
                : $"{_skillCatalog.Skills.Count} 项 · {_skillCatalog.RefreshedAt:HH:mm}";
        using var statusFormat = new StringFormat { Alignment = StringAlignment.Far };
        g.DrawString(status, _smallFont, mutedBrush, new RectangleF(panel.X + 110, panel.Y + 12, panel.Width - 122, 18), statusFormat);

        var rowTop = panel.Y + 33;
        if (_skillsLoading)
        {
            g.DrawString("正在读取本机已安装的个人、插件和系统 Skills…", _skillDescriptionFont, mutedBrush,
                new RectangleF(panel.X + 12, rowTop + 8, panel.Width - 24, 34));
            return;
        }

        if (_skillCatalog.Skills.Count == 0)
        {
            g.DrawString("未发现可显示的 Skills；可在托盘菜单中再次刷新。", _skillDescriptionFont, mutedBrush,
                new RectangleF(panel.X + 12, rowTop + 8, panel.Width - 24, 34));
            return;
        }

        var skills = _skillCatalog.Skills;
        var visible = Math.Min(VisibleSkillRows, skills.Count - _skillScrollIndex);
        using var descriptionFormat = new StringFormat(StringFormatFlags.LineLimit)
        {
            Trimming = StringTrimming.EllipsisCharacter
        };
        for (var row = 0; row < visible; row++)
        {
            var skill = skills[_skillScrollIndex + row];
            var y = rowTop + row * 44;
            if (row > 0)
            {
                using var divider = new Pen(Color.FromArgb(223, 226, 217));
                g.DrawLine(divider, panel.X + 10, y - 3, panel.Right - 10, y - 3);
            }

            using var nameBrush = new SolidBrush(Color.FromArgb(55, 60, 54));
            using var sourceBrush = new SolidBrush(skill.Source == "插件" ? Color.FromArgb(91, 116, 138) : Color.FromArgb(108, 120, 89));
            g.DrawString(skill.Name, _skillNameFont, nameBrush, panel.X + 12, y + 2);
            g.DrawString($"{skill.Source} · {skill.InstalledAt:MM-dd}", _smallFont, sourceBrush,
                new RectangleF(panel.Right - 88, y + 4, 76, 15), statusFormat);
            g.DrawString(skill.Description, _skillDescriptionFont, mutedBrush,
                new RectangleF(panel.X + 12, y + 19, panel.Width - 24, 19), descriptionFormat);
        }

        if (MaxSkillScrollIndex > 0)
        {
            var thumbHeight = Math.Max(16, (int)Math.Round((double)(panel.Height - 41) * VisibleSkillRows / skills.Count));
            var thumbTop = rowTop + (int)Math.Round((double)(panel.Height - 41 - thumbHeight) * _skillScrollIndex / MaxSkillScrollIndex);
            using var scrollBrush = new SolidBrush(Color.FromArgb(164, 176, 158));
            g.FillRectangle(scrollBrush, panel.Right - 7, thumbTop, 3, thumbHeight);
        }
    }

    private void DrawFooter(Graphics g)
    {
        var left = _snapshot?.CreditText ?? "每 30 秒自动更新";
        if (!string.IsNullOrEmpty(_error)) left = _error;

        using var linePen = new Pen(Color.FromArgb(222, 216, 203));
        g.DrawLine(linePen, 22, Height - 40, Width - 94, Height - 40);
        using var footerBrush = new SolidBrush(_error is null ? Color.FromArgb(126, 122, 112) : Color.FromArgb(176, 86, 53));
        g.DrawString(left, _smallFont, footerBrush, new RectangleF(22, Height - 31, SkinButtonRect.X - 30, 20));

        DrawSkinButton(g);
    }

    private void DrawSkinButton(Graphics g)
    {
        var rect = SkinButtonRect;
        var fillColor = _skinBusy
            ? Color.FromArgb(226, 223, 214)
            : _skinActive
                ? Color.FromArgb(105, 126, 107)
                : Color.FromArgb(236, 225, 199);
        if (_skinButtonPressed && !_skinBusy)
        {
            fillColor = _skinActive
                ? Color.FromArgb(85, 106, 89)
                : Color.FromArgb(222, 207, 172);
        }

        var borderColor = _skinActive
            ? Color.FromArgb(79, 103, 84)
            : Color.FromArgb(188, 158, 98);
        var textColor = _skinBusy
            ? Color.FromArgb(137, 133, 123)
            : _skinActive
                ? Color.FromArgb(255, 253, 246)
                : Color.FromArgb(100, 79, 42);

        using var path = RoundedRect(rect, 10);
        using var fill = new SolidBrush(fillColor);
        using var border = new Pen(borderColor);
        using var text = new SolidBrush(textColor);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        g.DrawString(_skinLabel, _smallFont, text, rect, format);
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
            _skillsExpanded = root.TryGetProperty("skillsExpanded", out var expandedValue) && expandedValue.ValueKind == JsonValueKind.True;
            UpdateHeight();
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
                alwaysOnTop = _alwaysOnTop,
                skillsExpanded = _skillsExpanded
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
            _skillTitleFont.Dispose();
            _skillNameFont.Dispose();
            _skillDescriptionFont.Dispose();
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
    private const string ResourceName = "CodexQuotaFloat.Assets.CodexQuotaFloat.ico";

    public static Icon Create(Size size)
    {
        try
        {
            using var stream = typeof(IconFactory).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is not null)
            {
                using var source = new Icon(stream, size);
                return (Icon)source.Clone();
            }
        }
        catch (ArgumentException)
        {
            // Keep the tray usable even if a future package loses the embedded icon.
        }

        return CreateFallback(size);
    }

    private static Icon CreateFallback(Size size)
    {
        var edge = Math.Max(16, Math.Max(size.Width, size.Height));
        using var bitmap = new Bitmap(edge, edge);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        var scale = edge / 32f;
        var inset = 2f * scale;
        var cardBounds = new RectangleF(inset, inset, edge - inset * 2, edge - inset * 2);
        using var card = RoundedRectangle(cardBounds, 7.5f * scale);
        using var ink = new LinearGradientBrush(
            cardBounds,
            Color.FromArgb(55, 54, 48),
            Color.FromArgb(22, 23, 22),
            135f);
        graphics.FillPath(ink, card);

        using var fanGold = new SolidBrush(Color.FromArgb(224, 184, 98));
        graphics.FillPie(fanGold, 4.48f * scale, 5.76f * scale, 23.04f * scale, 23.04f * scale, 202, 136);
        using var fanPaper = new SolidBrush(Color.FromArgb(247, 231, 193));
        graphics.FillPie(fanPaper, 6.4f * scale, 7.68f * scale, 19.2f * scale, 19.2f * scale, 205, 130);
        using var redLeaf = new SolidBrush(Color.FromArgb(182, 57, 43));
        graphics.FillPie(redLeaf, 8.32f * scale, 9.6f * scale, 15.36f * scale, 15.36f * scale, 248, 44);

        using var ribs = new Pen(Color.FromArgb(123, 74, 40), Math.Max(1f, 0.86f * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var pivot = new PointF(16f * scale, 23.36f * scale);
        foreach (var target in new[]
        {
            new PointF(6.72f * scale, 16f * scale),
            new PointF(10.24f * scale, 10.88f * scale),
            new PointF(16f * scale, 8.96f * scale),
            new PointF(21.76f * scale, 10.88f * scale),
            new PointF(25.28f * scale, 16f * scale)
        })
        {
            graphics.DrawLine(ribs, pivot, target);
        }
        using var pin = new SolidBrush(Color.FromArgb(235, 195, 107));
        graphics.FillEllipse(pin, 14.08f * scale, 21.44f * scale, 3.84f * scale, 3.84f * scale);

        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { NativeMethods.DestroyIcon(handle); }
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

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
