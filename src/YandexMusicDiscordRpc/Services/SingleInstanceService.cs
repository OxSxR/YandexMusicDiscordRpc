using System.IO.Pipes;
using System.Text;
using YandexMusicDiscordRpc.Constants;

namespace YandexMusicDiscordRpc.Services;

public sealed class SingleInstanceService : IDisposable
{
    private const string ActivateCommand = "SHOW";
    private readonly AppLogger _logger;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();
    private Task? _serverTask;
    private bool _disposed;

    public SingleInstanceService(AppLogger logger, Action onActivate)
    {
        _logger = logger;
        OnActivate = onActivate;
        _mutex = new Mutex(initiallyOwned: true, AppConstants.SingleInstanceMutexName, out var createdNew);
        IsPrimary = createdNew;

        if (IsPrimary)
        {
            _serverTask = Task.Run(() => RunActivationServerAsync(_cts.Token));
        }
    }

    public bool IsPrimary { get; }
    private Action OnActivate { get; }

    public bool TryActivateExistingInstance()
    {
        if (IsPrimary)
            return true;

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                AppConstants.ActivationPipeName,
                PipeDirection.Out,
                PipeOptions.None);
            client.Connect(1500);
            var bytes = Encoding.UTF8.GetBytes(ActivateCommand);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Не удалось активировать уже запущенный экземпляр: {ex.Message}");
            return false;
        }
    }

    private async Task RunActivationServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    AppConstants.ActivationPipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.Equals(command, ActivateCommand, StringComparison.Ordinal))
                {
                    OnActivate();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Ошибка канала активации: {ex.Message}");
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();

        try
        {
            _serverTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Игнорируем.
        }

        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Не владеем мьютексом — ничего страшного.
            }
        }

        _mutex.Dispose();
        _cts.Dispose();
    }
}
