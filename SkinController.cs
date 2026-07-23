using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace CodexQuotaFloat;

internal enum SkinMode
{
    Unavailable,
    Off,
    Active
}

internal readonly record struct SkinStatus(SkinMode Mode, string Label)
{
    public bool Available => Mode != SkinMode.Unavailable;
    public bool IsActive => Mode == SkinMode.Active;
}

internal readonly record struct SkinCommandResult(bool Success, string Message, string? Details = null);

internal sealed class SkinController : IDisposable
{
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly string _startScript;
    private readonly string _restoreScript;
    private readonly string _statePath;
    private readonly string _powerShellPath;

    public SkinController()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var scriptRoot = Path.Combine(localAppData, "Programs", "CodexDreamSkin", "scripts");
        _startScript = Path.Combine(scriptRoot, "start-dream-skin.ps1");
        _restoreScript = Path.Combine(scriptRoot, "restore-dream-skin.ps1");
        _statePath = Path.Combine(localAppData, "CodexDreamSkin", "state.json");
        _powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    public SkinStatus GetStatus()
    {
        if (!File.Exists(_startScript) || !File.Exists(_restoreScript))
        {
            return new SkinStatus(SkinMode.Unavailable, "未安装皮肤");
        }

        if (!TryReadActiveState(out var injectorPid, out var port))
        {
            return new SkinStatus(SkinMode.Off, "启用皮肤");
        }

        try
        {
            using var injector = Process.GetProcessById(injectorPid);
            if (injector.HasExited || !IsLoopbackPortListening(port))
            {
                return new SkinStatus(SkinMode.Off, "启用皮肤");
            }
        }
        catch
        {
            return new SkinStatus(SkinMode.Off, "启用皮肤");
        }

        return new SkinStatus(SkinMode.Active, "热加载");
    }

    public Task<SkinCommandResult> EnableAsync(CancellationToken cancellationToken = default) =>
        RunScriptAsync(_startScript, "皮肤已启用", cancellationToken, "-RestartExisting");

    public Task<SkinCommandResult> HotReloadAsync(CancellationToken cancellationToken = default)
    {
        if (!GetStatus().IsActive)
        {
            return Task.FromResult(new SkinCommandResult(false, "皮肤当前未启用"));
        }

        return RunScriptAsync(_startScript, "样式已热加载", cancellationToken);
    }

    public Task<SkinCommandResult> RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!GetStatus().IsActive)
        {
            return Task.FromResult(new SkinCommandResult(false, "皮肤当前未启用"));
        }

        return RunScriptAsync(_restoreScript, "已恢复官方样式", cancellationToken, "-ForceRestart");
    }

    private async Task<SkinCommandResult> RunScriptAsync(
        string scriptPath,
        string successMessage,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        if (!File.Exists(scriptPath))
        {
            return new SkinCommandResult(false, "未找到皮肤组件");
        }

        if (!await _commandGate.WaitAsync(0, cancellationToken))
        {
            return new SkinCommandResult(false, "皮肤操作正在进行");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _powerShellPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new SkinCommandResult(false, "无法启动皮肤操作");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode == 0)
            {
                return new SkinCommandResult(true, successMessage);
            }

            return new SkinCommandResult(false, "皮肤操作失败", LastUsefulLine(error, output));
        }
        catch (OperationCanceledException)
        {
            return new SkinCommandResult(false, "皮肤操作已取消");
        }
        catch (Exception ex)
        {
            return new SkinCommandResult(false, "皮肤操作失败", ex.Message);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private bool TryReadActiveState(out int injectorPid, out int port)
    {
        injectorPid = 0;
        port = 0;
        try
        {
            if (!File.Exists(_statePath)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(_statePath));
            var root = document.RootElement;
            return root.TryGetProperty("injectorPid", out var injectorPidValue)
                && injectorPidValue.TryGetInt32(out injectorPid)
                && injectorPid > 0
                && root.TryGetProperty("port", out var portValue)
                && portValue.TryGetInt32(out port)
                && port is > 0 and <= 65535;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLoopbackPortListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port && IPAddress.IsLoopback(endpoint.Address));
        }
        catch
        {
            return false;
        }
    }

    private static string? LastUsefulLine(params string[] values)
    {
        return values
            .SelectMany(value => value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            .Select(line => line.Trim())
            .LastOrDefault(line => line.Length > 0);
    }

    public void Dispose() => _commandGate.Dispose();
}
