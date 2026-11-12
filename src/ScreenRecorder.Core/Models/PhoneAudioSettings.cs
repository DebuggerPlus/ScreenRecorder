using System;

namespace ScreenRecorder.Core.Models;

/// <summary>
/// Аудио пишется на телефоне (PhoneAudioRecorder), видео — на этом ПК.
/// ПК управляет телефоном по control-каналу (line-delimited JSON), ждёт ready-сигнал
/// и принимает готовый .mp3 отдельным TCP-соединением на UploadPort.
/// По умолчанию выключено, чтобы поведение приложения не менялось.
/// </summary>
public sealed class PhoneAudioSettings
{
    /// <summary>false — приложение пишет звук как раньше (микрофон ПК/система).</summary>
    public bool Enabled { get; set; }
    /// <summary>IP телефона в локальной сети/Wi-Fi.</summary>
    public string Host { get; set; } = "";
    /// <summary>Порт control-канала на телефоне.</summary>
    public int ControlPort { get; set; } = 47653;
    /// <summary>Порт, на котором ПК принимает готовый файл от телефона.</summary>
    public int UploadPort { get; set; } = 47654;
    /// <summary>Сколько секунд ждать подключения/ответа готовности перед стартом видео.</summary>
    public int ReadyTimeoutSec { get; set; } = 5;
    /// <summary>Сколько секунд ждать приёма .mp3 после stop.</summary>
    public int UploadTimeoutSec { get; set; } = 120;

    public PhoneAudioSettings Clone() => new()
    {
        Enabled = Enabled,
        Host = Host,
        ControlPort = ControlPort,
        UploadPort = UploadPort,
        ReadyTimeoutSec = ReadyTimeoutSec,
        UploadTimeoutSec = UploadTimeoutSec
    };

    /// <summary>Приводит значения к допустимым (на случай повреждённого JSON).</summary>
    public void Normalize()
    {
        Host = (Host ?? "").Trim();
        if (ControlPort is < 1 or > 65535) ControlPort = 47653;
        if (UploadPort is < 1 or > 65535) UploadPort = 47654;
        ReadyTimeoutSec = Math.Clamp(ReadyTimeoutSec, 1, 60);
        UploadTimeoutSec = Math.Clamp(UploadTimeoutSec, 5, 3600);
    }
}
