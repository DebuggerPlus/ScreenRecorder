# ScreenRecorder WPF — техническое задание для Hermes DeveloperAgent

## 1. Роль агента и цель

Ты — Hermes DeveloperAgent. Собери полноценный WPF-проект для Windows 10: универсальный экранный рекордер на C#.

Работай последовательно: сначала проверь окружение и создай архитектурный каркас, затем реализуй MVP, собери проект и устрани ошибки. Не останавливайся на псевдокоде или макетах UI. Если важное техническое решение невозможно подтвердить в установленном SDK/пакете, проверь его документацию и выбери поддерживаемый вариант.

**Цель MVP:** пользователь может выбрать экран/область/окно, выбрать системный звук и/или микрофон, начать запись, поставить на паузу, продолжить, остановить и получить MP4-файл.

## 2. Базовые ограничения и платформа

- ОС: Windows 10, x64.
- Framework: `net8.0-windows` (если проект уже использует более новую совместимую версию .NET, не понижай её без причины).
- UI: WPF, XAML.
- Язык: C#.
- Архитектура: MVVM.
- Nullable reference types: включить.
- Включить Windows targeting и `UseWPF`.
- Начальная целевая архитектура — один WPF-процесс с отдельными слоями/проектами. Не вводить сервисы, облако, базу данных или отдельный recorder process в MVP.
- Не требовать администраторских прав для обычного запуска.
- UI должен быть пригоден для Windows 10, включая масштабирование DPI.

## 3. Технологические решения

### 3.1 Захват видео

Использовать Windows Graphics Capture (WGC) через поддерживаемые Windows API / WinRT interop. Проверить совместимость выбранного способа с целевым Windows 10 SDK.

Поддержать три режима:
1. `Display` — полный выбранный монитор.
2. `Region` — прямоугольная область на мониторе.
3. `Window` — выбранное окно приложения.

Рекомендуемый путь:
- WGC для захвата выбранного дисплея/окна.
- Для области — выбрать прямоугольник overlay-инструментом и захватывать соответствующую область. Если API не поддерживает crop непосредственно, выполнить crop в pipeline обработки кадра.
- Перед записью проверить, что выбранный источник ещё существует и доступен.
- Обрабатывать изменение размера/закрытие окна, отключение монитора и потерю источника без падения приложения.

Для MVP допустимо использовать Windows Graphics Capture и Direct3D interop через проверенные NuGet-библиотеки. Не писать собственную реализацию Direct3D/WinRT interop с нуля, если подходящая поддерживаемая библиотека доступна.

### 3.2 Аудиозахват

Использовать WASAPI:
- System audio: loopback capture выбранного устройства вывода.
- Microphone: capture выбранного input-устройства.
- Поддержать системный звук отдельно, микрофон отдельно или оба вместе.
- Если выбраны оба источника, синхронизировать их по временным меткам и свести в итоговую аудиодорожку.
- Список устройств получать динамически; обрабатывать отключение/переключение устройства.
- Если пользователь не выбрал аудио — видео записывается без аудиодорожки.
- Не записывать звук скрыто: выбранные источники должны быть видны в UI.

Рассмотреть NAudio для WASAPI. Проверить текущую совместимость и API установленной версии до реализации.

### 3.3 Кодирование и контейнер

Использовать FFmpeg для кодирования и мультиплексирования в MP4:
- Видео: H.264 (libx264, если доступен в поставляемой сборке FFmpeg; иначе выбрать доступный совместимый encoder).
- Аудио: AAC.
- FPS: 30 по умолчанию, выбор 30/60; допускается добавление 24/25.
- Размер: исходный размер источника по умолчанию.
- Контейнер: MP4.
- При паузе остановить добавление кадров/аудиоданных в итоговый timeline; после Resume запись продолжается без включения времени паузы в содержимое видео.
- Финализировать контейнер при Stop; корректно завершать процесс FFmpeg и освобождать pipe/stream.

Варианты интеграции:
- Предпочтительно использовать FFmpeg через stdin/raw frame pipe и отдельный аудиопуть с синхронизацией.
- Можно применить FFmpeg CLI как дочерний процесс, если это делает реализацию стабильнее. Все аргументы процесса должны передаваться безопасно; не строить команду путём небезопасной конкатенации пользовательского текста.
- Не полагаться на наличие FFmpeg в PATH. Предусмотреть проверку bundled/runtime executable и понятную ошибку, если бинарник отсутствует.
- Лицензирование FFmpeg и используемой сборки должно быть проверено; записать сведения в `THIRD-PARTY-NOTICES.md`. Не включать сборку с GPL-компонентами без явного решения.

## 4. Структура solution

Создать solution с проектами:

```text
ScreenRecorder/
├── ScreenRecorder.sln
├── src/
│   ├── ScreenRecorder.App/
│   │   ├── App.xaml
│   │   ├── MainWindow.xaml
│   │   ├── Views/
│   │   │   ├── MainWindow.xaml
│   │   │   ├── CaptureSourcePickerWindow.xaml
│   │   │   └── RegionSelectionWindow.xaml
│   │   ├── ViewModels/
│   │   │   ├── MainViewModel.cs
│   │   │   ├── CaptureSourcePickerViewModel.cs
│   │   │   └── RegionSelectionViewModel.cs
│   │   ├── Services/
│   │   │   ├── DialogService.cs
│   │   │   ├── HotkeyService.cs
│   │   │   └── SettingsService.cs
│   │   ├── Resources/
│   │   └── appsettings.json
│   ├── ScreenRecorder.Core/
│   │   ├── Models/
│   │   ├── Enums/
│   │   ├── Interfaces/
│   │   └── Recording/
│   ├── ScreenRecorder.Infrastructure/
│   │   ├── Capture/
│   │   ├── Audio/
│   │   ├── Encoding/
│   │   ├── Devices/
│   │   └── Settings/
│   └── ScreenRecorder.Tests/
├── Docs/
│   ├── Architecture.md
│   └── BuildAndRun.md
├── THIRD-PARTY-NOTICES.md
└── README.md
```

Названия/папки можно адаптировать к принятому шаблону, но не смешивать UI, доменные модели и системные реализации.

### Зависимости

- `ScreenRecorder.App` → Core, Infrastructure.
- `ScreenRecorder.Infrastructure` → Core.
- `ScreenRecorder.Core` не зависит от WPF, WinRT, NAudio или FFmpeg.
- `ScreenRecorder.Tests` → Core и тестируемые сервисы.
- Dependency Injection — через Microsoft.Extensions.DependencyInjection или эквивалентный совместимый контейнер.
- MVVM Toolkit (CommunityToolkit.Mvvm) рекомендуется для ObservableObject и RelayCommand.
- Не добавлять пакеты, если их функциональность не используется.

## 5. Domain model и интерфейсы

Определи модели и enum:

```csharp
public enum CaptureMode
{
    Display,
    Region,
    Window
}

public enum AudioSourceMode
{
    None,
    System,
    Microphone,
    SystemAndMicrophone
}

public enum RecordingState
{
    Idle,
    Preparing,
    Recording,
    Paused,
    Stopping,
    Error
}

public sealed record CaptureSource(
    string Id,
    string Name,
    CaptureMode Mode,
    int Width,
    int Height);

public sealed record AudioDevice(
    string Id,
    string Name,
    bool IsInput);

public sealed record RecordingOptions(
    CaptureSource Source,
    AudioSourceMode AudioMode,
    string? SystemAudioDeviceId,
    string? MicrophoneDeviceId,
    string OutputPath,
    int FramesPerSecond,
    int VideoBitrateKbps);
```

Названия и детали можно корректировать, если нужно для реализации; смысл и разделение ответственности сохранить.

Создать интерфейсы Core:

```csharp
public interface ICaptureSourceProvider
{
    Task<IReadOnlyList<CaptureSource>> GetSourcesAsync(
        CaptureMode mode,
        CancellationToken cancellationToken);
}

public interface IVideoCapture
{
    Task StartAsync(CaptureSource source, CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IAudioCapture
{
    Task StartAsync(AudioSourceMode mode,
        string? systemDeviceId,
        string? microphoneDeviceId,
        CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IRecordingSession
{
    RecordingState State { get; }
    TimeSpan Elapsed { get; }
    Task StartAsync(RecordingOptions options, CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IRecordingSessionFactory
{
    IRecordingSession Create();
}
```

Можно уточнить интерфейсы после проверки технической схемы pipeline. Не размещать WPF types (Window, BitmapSource, Dispatcher) в Core.

## 6. Компоненты и ответственность

### App / Presentation
- Главное окно, настройки, выбор источника, overlay выбора области.
- ViewModels управляют UI state и командами.
- UI не вызывает FFmpeg, WASAPI или WGC напрямую.
- Все операции записи — async, без блокирования UI thread.
- Кнопки/команды недоступны в несовместимых состояниях.
- Показывать индикатор записи и прошедшее время.

### Core
- Модели, enums, контракты.
- Правила состояния recording session.
- Валидация recording options.
- Независим от конкретных технологий захвата.

### Infrastructure
- WGC capture adapter.
- Region crop adapter.
- WASAPI/NAudio capture.
- FFmpeg process/encoding pipeline.
- Audio mixing and synchronization.
- Device enumeration.
- Output path handling and runtime dependency checks.

### RecordingCoordinator / Session
- Оркестрирует видео, аудио, encoder.
- Имеет единый state machine.
- Не допускает двух одновременных записей в MVP.
- Гарантирует остановку всех потоков при Stop, отмене и ошибке.
- Не теряет исключения: ошибки логируются и передаются UI как понятное сообщение.

## 7. State machine

Разрешённые переходы:

```text
Idle -> Preparing -> Recording
Preparing -> Error
Recording -> Paused
Paused -> Recording
Recording -> Stopping -> Idle
Paused -> Stopping -> Idle
Stopping -> Error
Error -> Idle
```

Требования:
- Start доступен только в Idle.
- Pause доступен только в Recording.
- Resume доступен только в Paused.
- Stop доступен в Recording и Paused.
- Во время Preparing/Stopping нельзя повторно запускать команды.
- Любая ошибка останавливает ресурсы и отображает диагностическое сообщение.
- Реализовать безопасную обработку повторных/одновременных вызовов команд.

## 8. UI/UX требования

Создать аккуратный, простой, современный Windows desktop интерфейс. Не копировать чужой бренд или продукт.

Главное окно должно содержать:

1. Режим захвата: Display / Region / Window.
2. Кнопку выбора источника и краткое имя выбранного источника.
3. Настройку FPS: 30/60.
4. Настройку качества/битрейта.
5. Audio:
   - None / System / Microphone / System + Microphone.
   - Устройства ввода/вывода, если режим их требует.
6. Output folder и кнопку Browse.
7. Запись: Start, Pause/Resume, Stop.
8. Таймер и заметный, но ненавязчивый статус.
9. Ссылку/кнопку открыть последнюю запись или папку.

Начальные значения:
- Capture mode: Display.
- FPS: 30.
- Audio mode: None.
- Output directory: `%Videos%\\ScreenRecorder`.
- Video bitrate: подобрать разумный дефолт для 1080p, например 8 Mbps, и дать пользователю возможность менять.

Требования к UI:
- Состояния и ошибки отображать понятным языком.
- Длинные пути не должны ломать layout.
- Поддерживать DPI scaling и размеры окна.
- Не использовать placeholder-кнопки без реализации.
- Все интерактивные контролы должны быть подключены к командам и состоянию.

## 9. Горячие клавиши

Добавить global hotkeys через Win32 RegisterHotKey, с возможностью настройки позже:
- Start/Stop — например, Ctrl+Alt+R.
- Pause/Resume — например, Ctrl+Alt+P.

Проверять ошибки регистрации (в частности конфликт с другой программой) и показывать сообщение. Горячие клавиши не должны срабатывать после закрытия приложения. Для MVP комбинации можно фиксировать в настройках, но не хардкодить в нескольких местах.

## 10. Settings и логирование

- Сохранять пользовательские настройки в JSON в `%AppData%\\ScreenRecorder`.
- Не сохранять секреты: приложение не использует их.
- При повреждённом JSON — восстановить дефолты, сохранить диагностический лог.
- Логи в `%LocalAppData%\\ScreenRecorder\\Logs`.
- Логировать начало/конец сессии, параметры, выбранные устройства, ошибки capture/encoding и exit code FFmpeg.
- Не логировать содержимое звука/видео.
- Ротация логов или ограничение размера.

## 11. Безопасность и надёжность

- Не запускать команды оболочки через `cmd.exe`.
- Не вставлять путь к файлу в FFmpeg command line небезопасной строковой конкатенацией; использовать ProcessStartInfo.ArgumentList, где доступно.
- Проверять права/доступность каталога назначения и свободное место перед записью.
- Проверять наличие FFmpeg и необходимые encoder capabilities при запуске или перед первой записью.
- Не удалять и не перезаписывать существующие файлы без подтверждения/уникального имени.
- На закрытие приложения во время записи запросить подтверждение и корректно завершить pipeline.
- Отключение аудиоустройства или источника захвата не должно приводить к необработанному crash.
- Соблюдать лицензии всех NuGet пакетов и FFmpeg distribution.

## 12. Acceptance criteria (критерии готовности MVP)

MVP считается готовым, когда:

1. Solution успешно собирается в Release x64.
2. Приложение запускается на Windows 10 без администраторских прав.
3. Пользователь может выбрать монитор и записать экран в MP4.
4. Пользователь может выбрать окно и записать его.
5. Пользователь может выбрать прямоугольную область и записать её.
6. Системный звук записывается отдельно выбранным режимом.
7. Микрофон записывается отдельно выбранным режимом.
8. System + Microphone записываются одновременно и синхронно.
9. Audio None создаёт корректное видео без аудиодорожки.
10. Pause/Resume работают без записи паузы в видеоряд.
11. Stop корректно финализирует MP4, который открывается в обычном видеоплеере.
12. Ошибка устройства/FFmpeg отображается в UI, приложение остаётся работоспособным.
13. Start/Pause/Resume/Stop нельзя вызвать в недопустимом состоянии.
14. Настройки сохраняются между запусками.
15. Hotkeys работают и корректно освобождаются при закрытии.
16. Нет неиспользуемых кнопок, TODO вместо ключевой функциональности или заглушек в основном сценарии.
17. Добавлены unit tests для state transitions, options validation и других чистых Core-правил.
18. `README.md`, `Docs/Architecture.md`, `Docs/BuildAndRun.md` и `THIRD-PARTY-NOTICES.md` содержат актуальные инструкции и сведения.

## 13. Тестирование

Unit tests:
- Все допустимые/недопустимые state transitions.
- Валидация FPS, bitrate, source, output path.
- Audio mode selection.
- Pause/Resume/Stop sequence.
- Ошибка во время Preparing/Recording и переход к безопасному состоянию.

Manual integration tests на Windows 10:
- Один монитор.
- Несколько мониторов, если доступно.
- Window capture и закрытие выбранного окна.
- Region capture на DPI-scaled display.
- System audio only.
- Microphone only.
- System + Microphone.
- Нет устройств audio.
- Pause на несколько секунд и Resume.
- Stop и открытие результата в плеере.
- Запуск с отсутствующим FFmpeg.
- Закрытие приложения во время записи.
- Конфликт горячих клавиш.

Не утверждай, что integration tests выполнены, если фактически нет Windows 10 среды/оборудования для проверки. Отметь такие пункты как требующие ручной проверки.

## 14. Порядок реализации

### Phase 0 — Inspect and plan
- Проверить repository, существующие правила/документы, SDK и структуру.
- Если проект уже существует — не удалять и не перезаписывать файлы без необходимости.
- Составить краткий технический план и список пакетов/API с обоснованием.

### Phase 1 — Buildable skeleton
- Создать solution/projects.
- Добавить DI, MVVM, Core interfaces, MainWindow.
- Сборка должна проходить.
- Написать `Docs/Architecture.md`.

### Phase 2 — Video-only vertical slice
- Выбор Display.
- Захват кадров.
- FFmpeg encoding в MP4.
- Start/Stop и elapsed timer.
- Проверить реальный файл.
- Только после работающего vertical slice идти дальше.

### Phase 3 — Capture selection
- Window selection.
- Region selection overlay.
- DPI/coordinates correctness.

### Phase 4 — Audio
- WASAPI system loopback.
- Microphone.
- Combined audio and sync.
- UI selection and device handling.

### Phase 5 — Pause/resume, hotkeys, settings
- State machine.
- Pause/resume timeline.
- Hotkey registration/unregistration.
- JSON settings and logs.

### Phase 6 — Hardening and delivery
- Error handling, resource disposal, integration testing.
- Release x64 build.
- Update docs and third-party notices.
- Provide build/run instructions and a concise completion report.

Если пользователь требует строго пошаговой работы с подтверждением — остановись после Phase 1 и доложи результат. В противном случае продолжай по этапам, сохраняя рабочую сборку.

## 15. Отчёт после завершения

В финальном отчёте сообщи:
- Что реализовано.
- Архитектурные решения и использованные пакеты.
- Как собрать и запустить.
- Где сохраняются видео, настройки и логи.
- Результат `dotnet build` и тестов (реальные выводы, не предположения).
- Какие проверки остались ручными на Windows 10.
- Известные ограничения и следующий разумный шаг.

Не заявляй о функции как завершённой, пока она не реализована и не проверена. Если отдельная функция технически не реализована, укажи это явно и не маскируй кнопкой-заглушкой.
