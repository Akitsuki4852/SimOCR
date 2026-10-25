using System.IO.Pipes;
using SimOCR.Diagnostics;

namespace SimOCR.Startup;

/// <summary>
/// 单实例守卫：命名 Mutex 判定首实例，命名管道把"再次启动"转成对首实例的命令
/// （例如用户再次双击 exe 时直接进入截图）。
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\SimOCR.SingleInstance.v1";

    private readonly LogService _log;
    private readonly string _pipeName;
    private readonly Mutex _mutex;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private bool _disposed;

    public SingleInstanceGuard(LogService log)
    {
        _log = log;
        _pipeName = $"SimOCR.Pipe.{Environment.UserName}";

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsFirstInstance = createdNew;
    }

    public bool IsFirstInstance { get; }

    /// <summary>收到来自后续实例的命令（"capture" 等）。</summary>
    public event Action<string>? CommandReceived;

    /// <summary>仅首实例调用：开始监听管道。</summary>
    public void StartServer()
    {
        if (!IsFirstInstance || _serverTask is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _serverTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync(token).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _log.Info($"收到来自新实例的命令: {line}");
                        CommandReceived?.Invoke(line.Trim());
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException ex)
                {
                    // 客户端提前断开：继续等下一条
                    _log.Debug($"管道 IO 异常（忽略）: {ex.Message}");
                }
                catch (Exception ex)
                {
                    _log.Warn("单实例管道监听异常", ex);
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
            }
        }, token);
    }

    /// <summary>后续实例调用：把命令发给首实例。返回是否成功投递。</summary>
    public static bool TrySend(string command, int timeoutMs = 3000)
    {
        try
        {
            var pipeName = $"SimOCR.Pipe.{Environment.UserName}";
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cts?.Cancel();
            _serverTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // ignore
        }
        finally
        {
            _cts?.Dispose();
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // 未持有则忽略
            }
            _mutex.Dispose();
        }
    }
}
