# Сборка и разработка

**Требуется:** Visual Studio 2022 (рабочая нагрузка «Разработка приложений для
универсальной платформы Windows», Windows 10/11 SDK), .NET 8 SDK, JDK 17, Android SDK,
`adb` в `PATH`.

| Компонент | Команда |
|---|---|
| Companion (APK) | `cd ScrcpyBar.Android && gradlew.bat assembleDebug` |
| Виджет (MSIX) | `powershell -ExecutionPolicy Bypass -File .\rebuild_and_deploy.ps1` |
| Bridge (EXE) | `dotnet publish ScrcpyBar.Bridge -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true` |

> Флаги single-file обязательны: без них получится каркасный exe на 0.1 МБ без
> рантайма. В самом `.csproj` их специально нет — иначе обычный `dotnet build`
> складывает сборку в побочную папку `win-x64\` и легко запустить устаревший exe.

Скрипт `rebuild_and_deploy.ps1` собирает виджет, регистрирует пакет и выдаёт
loopback-exemption. Готовые артефакты кладутся в `build/` — именно они и публикуются
в Releases.

<details>
<summary><b>Структура репозитория</b></summary>

```
ScrcpyBar.sln              решение Visual Studio 2022
ScrcpyBar.Android/         Kotlin-приложение на телефоне (com.scrcpybar.companion)
ScrcpyBar.Bridge/          фоновый сервис .NET 8 (один exe)
ScrcpyBar.Widget/          виджет Xbox Game Bar (UWP XAML)
build/                     готовые артефакты релиза
```

**Скрипты:**

| Файл | Назначение |
|---|---|
| `rebuild_and_deploy.ps1` / `.bat` | Пересборка виджета + регистрация + loopback-exemption. |
| `verify.ps1` | Сквозная проверка: companion → Bridge → HTTP-API → SMTC → виджет. Флаг `-Transport` добавляет нажатия кнопок. |
| `check_smtc_sessions.ps1` | Диагностика: все SMTC-сессии системы, метаданные, обложка. |
| `check_widget_log.ps1` | Лог событий указателя виджета. |
| `check_lyrics_flow.ps1` | Прогон сценария слов песни как в виджете. |
| `start_bridge.bat` | Запуск Bridge. |
| `open_in_vs.bat` | Открыть решение в Visual Studio 2022. |

**HTTP-API Bridge** (`127.0.0.1:39281`):

| Метод | Путь | Описание |
|---|---|---|
| `GET` | `/api/track` | Трек, позиция, длительность, обложка |
| `GET` | `/api/cover` | Обложка (JPEG/PNG) |
| `GET` | `/api/lyrics` | Слова песни (`linesText` — `timeMs<TAB>текст`, `version` — метка версии вида `(Clean), source — lrc.red/better-lyrics/lrcmux/synclrc/lrclib (провайдеры в случайном порядке) |
| `POST` | `/api/play-pause` | Пауза / воспроизведение |
| `POST` | `/api/next` · `/api/prev` | Следующий / предыдущий трек |
| `POST` | `/api/seek?seconds=±N` | Перемотка относительно (шаг ±300 с) |
| `POST` | `/api/seek-position?positionMs=&durationMs=` | Точная перемотка |
| `POST` | `/api/rewind` · `/api/fast-forward` | Короткая перемотка ±5 с |
| `POST` | `/api/vol-up` · `/api/vol-down` · `/api/volume?level=N` | Громкость |
| `POST` | `/api/like?value=true|false` | Лайк/анлайк (без value — тоггл по текущему состоянию) |
| `GET` | `/` | Веб-пульт |

</details>
