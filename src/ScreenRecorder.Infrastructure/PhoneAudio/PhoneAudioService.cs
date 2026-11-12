using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ScreenRecorder.Core.Interfaces;
using ScreenRecorder.Core.Models;

namespace ScreenRecorder.Infrastructure.PhoneAudio;

/// <summary>
/// Связка «телефон пишет звук — ПК пишет видео»:
/// StartAsync поднимает приёмник файла, подключается к телефону, отправляет start и ждёт ready;
/// StopAndReceiveAsync отправляет stop и забирает .mp3.
///
/// Основной путь получения файла — PULL по control-каналу (ПК инициирует исходящее соединение,
/// поэтому Windows Firewall не мешает). Пассивный приём на UploadPort остаётся как быстрая
/// оппортунистическая попытка, а последний резерв — `adb pull` из каталога записей телефона.
/// Любая ошибка только логируется — вызывающий код продолжает обычную запись видео.
/// </summary>
public sealed class PhoneAudioService : IDisposable
{
    /// <summary>Каталог записей на телефоне — для резервного adb pull.</summary>
    public const string PhoneRecordingsDir = "/sdcard/Android/data/com.phoneaudio.recorderapp/files/recordings";

    private readonly ILoggerService _logger;
    private PhoneAudioLink? _link;
    private PhoneUploadServer? _server;
    private string? _baseName;
    private string? _outputDir;

    public PhoneAudioService(ILoggerService logger) => _logger = logger;

    /// <summary>true между успешным StartAsync и StopAndReceiveAsync.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Путь к последнему принятому .mp3 (или null).</summary>
    public string? ReceivedPath { get; private set; }

    /// <summary>
    /// Поднимает приёмник на UploadPort, подключается к телефону и отправляет start.
    /// Возвращает true только если телефон подтвердил готовность (ok/ready) — тогда и только тогда можно начинать видео.
    /// </summary>
    public async Task<bool> StartAsync(PhoneAudioSettings settings, string baseName, string outputDirectory, CancellationToken ct = default)
    {
        if (settings is null || !settings.Enabled) return false;

        var s = settings.Clone();
        s.Normalize();

        if (string.IsNullOrWhiteSpace(s.Host))
        {
            _logger.Log("PhoneAudio: Host не задан — запись аудио с телефона пропущена.");
            return false;
        }

        try
        {
            _baseName = baseName;
            _outputDir = outputDirectory;
            Directory.CreateDirectory(outputDirectory);

            // Пассивный приёмник — оппортунистический путь (обычно блокируется файрволом, см. StopAndReceiveAsync).
            _server = new PhoneUploadServer(s.UploadPort, outputDirectory, _logger.Log);
            _server.Start();

            _link = new PhoneAudioLink(s.Host, s.ControlPort);
            _link.EventReceived += ev => _logger.Log($"[phone] {ev.ToJsonString()}");

            await _link.ConnectAsync(TimeSpan.FromSeconds(s.ReadyTimeoutSec), ct).ConfigureAwait(false);
            _logger.Log($"PhoneAudio: подключено к {s.Host}:{s.ControlPort}, запускаю запись «{baseName}».");

            var reply = await _link.StartAsync(baseName, s.UploadPort, ct: ct).ConfigureAwait(false);
            var ready = reply["ready"]?.GetValue<bool>() == true || reply["ok"]?.GetValue<bool>() == true;
            if (!ready)
                throw new InvalidOperationException($"телефон не подтвердил готовность: {reply.ToJsonString()}");

            IsActive = true;
            _logger.Log("PhoneAudio: телефон готов (ready) — запускаю видео.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Log($"PhoneAudio: старт не удался: {ex.Message}. Продолжаю запись видео без телефона.");
            Cleanup();
            return false;
        }
    }

    /// <summary>
    /// Отправляет stop, затем забирает .mp3: короткая попытка пассивного приёма, основной путь — pull
    /// по control-каналу (3 попытки, ожидание state==idle), последний резерв — adb pull.
    /// Возвращает путь к сохранённому .mp3 или null. Никогда не бросает исключение наружу.
    /// </summary>
    public async Task<string?> StopAndReceiveAsync(PhoneAudioSettings settings, CancellationToken ct = default)
    {
        if (!IsActive || _link == null || _server == null || _baseName == null || _outputDir == null)
        {
            Cleanup();
            return null;
        }

        var s = (settings ?? new PhoneAudioSettings()).Clone();
        s.Normalize();
        var fileName = _baseName + ".mp3";

        try
        {
            var stopReply = await _link.StopAsync(ct).ConfigureAwait(false);
            _logger.Log($"[phone] stop: {stopReply.ToJsonString()}");
        }
        catch (Exception ex)
        {
            _logger.Log($"PhoneAudio: ответ на stop не получен: {ex.Message}");
        }

        string? path = null;

        // 1) Оппортунистически: вдруг входящий push всё-таки прошёл (короткое окно, не задерживаем Stop).
        try
        {
            path = await _server.WaitForFileAsync(fileName, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            if (path != null) _logger.Log("PhoneAudio: файл получен входящим push-соединением.");
        }
        catch (Exception ex)
        {
            _logger.Log($"PhoneAudio: пассивный приём не удался: {ex.Message}");
        }

        // 2) Основной путь: pull по control-каналу (исходящее соединение ПК — файрвол не мешает).
        if (path == null)
        {
            _logger.Log("PhoneAudio: входящий push недоступен — забираю файл через pull по control-каналу.");
            path = await FetchAudioAsync(_link, _baseName, _outputDir, TimeSpan.FromSeconds(s.UploadTimeoutSec), ct).ConfigureAwait(false);
        }

        if (path != null)
        {
            ReceivedPath = path;
            _logger.Log($"PhoneAudio: аудио на ПК: {path}");
        }
        else
        {
            _logger.Log("PhoneAudio: аудио получить не удалось (ни push, ни pull, ни adb pull).");
        }

        IsActive = false;
        Cleanup();
        return path;
    }

    /// <summary>
    /// Ждёт, пока телефон вернётся в состояние idle (закончил кодирование), затем до 3 раз пытается
    /// вытянуть файл по control-каналу. При неудаче — adb pull. Повторяет логику PhoneAudioRecorder.Program.FetchAudioAsync.
    /// </summary>
    private async Task<string?> FetchAudioAsync(PhoneAudioLink link, string baseName, string dir, TimeSpan timeout, CancellationToken ct)
    {
        try { if (!link.Connected) await link.ConnectAsync(ct: ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.Log("[fetch] connect failed: " + ex.Message); }

        // Ждём завершения кодирования на телефоне (state снова idle).
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var st = await link.StatusAsync(ct).ConfigureAwait(false);
                if ((st["state"]?.GetValue<string>() ?? "") == "idle") break;
            }
            catch { }
            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        var dest = Path.Combine(dir, baseName + ".mp3");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await link.PullFileAsync(baseName + ".mp3", dest, ct).ConfigureAwait(false);
                _logger.Log($"[fetch] ok: {dest} ({new FileInfo(dest).Length} bytes)");
                return dest;
            }
            catch (Exception ex)
            {
                _logger.Log($"[fetch] attempt {attempt} failed: {ex.Message}");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }

        _logger.Log("[fetch] pull не сработал — перехожу на adb pull.");
        return AdbPull(baseName, dir);
    }

    private string? AdbPull(string baseName, string outputDir)
    {
        try
        {
            var remote = $"{PhoneRecordingsDir}/{baseName}.mp3";
            var local = Path.Combine(outputDir, baseName + ".mp3");
            var psi = new ProcessStartInfo("adb", $"pull \"{remote}\" \"{local}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            var tail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            _logger.Log($"[adb] pull {baseName}.mp3 -> exit {p.ExitCode}: {tail.Trim()}");
            return p.ExitCode == 0 && File.Exists(local) ? local : null;
        }
        catch (Exception ex)
        {
            _logger.Log($"PhoneAudio: adb pull не удался: {ex.Message}");
            return null;
        }
    }

    private void Cleanup()
    {
        try { _link?.Dispose(); } catch { }
        try { _server?.Dispose(); } catch { }
        _link = null;
        _server = null;
    }

    public void Dispose() => Cleanup();
}
