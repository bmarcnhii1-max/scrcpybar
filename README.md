<div align="center">

# ScrcpyBar

<img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT License">
<img src="https://img.shields.io/badge/platform-Windows%2010%2019041%2B-0078D6.svg" alt="Windows 10 19041+">
<img src="https://img.shields.io/badge/Android-3DDC84.svg" alt="Android">
<img src="https://img.shields.io/badge/.NET-8-512BD4.svg" alt=".NET 8">

**Музыка с телефона — прямо в оверлее Xbox Game Bar.**

</div>

---

Играешь на ПК, а музыка идёт с Android-телефона? ScrcpyBar выводит трек, обложку и
караоке-слова в виджет Xbox Game Bar (`Win + G`) поверх игры, а управление идёт прямо из
превью — перемотка мышью, пауза, громкость. Связь с телефоном **только по USB**, без
Wi-Fi и без интернета.

<div align="center">

## ⚡ Быстрый старт: один файл

### 1. Скачай `ScrcpyBar.Bridge.exe`

Из [Releases](https://github.com/bmarcnhii1-max/scrcpybar/releases/latest) — один
self-contained exe, внутри него .NET 8, виджет, companion и все зависимости.

### 2. Запусти его

Двойной клик. Появится значок в трее, окно и консоли не будет.

### 3. Окно первоначальной настройки сделает всё остальное

Само поставит виджет в Game Bar (один запрос UAC) и companion на телефон по USB.
Больше ничего настраивать не нужно.

</div>

<details>
<summary><b>Скриншот / гифка</b></summary>

<p>
  <img src="docs/screenshot.png" width="49%" alt="ScrcpyBar widget — плеер">
  <img src="docs/screenshot-lyrics.png" width="49%" alt="ScrcpyBar widget — синхронный текст песни">
</p>
<p align="center"><sub>Слева — плеер с обложкой и управлением телефоном. Справа — караоке-текст от lrclib.net с подсветкой текущей строки.</sub></p>

</details>

---

## Как это работает

```
┌──────────────────────────┐        USB / adb          ┌────────────────────────────┐
│  Android-телефон         │  ──────────────────────▶ │  ScrcpyBar.Bridge.exe      │
│  ScrcpyBar Companion      │   adb forward            │  один exe, работает в трее │
│  Notification Listener    │   tcp:39282 → tcp:38080  │  HTTP-API: 127.0.0.1:39281 │
│  MediaSessionManager      │                           └─────────────┬──────────────┘
└──────────────────────────┘                                         │ SMTC
                                                                       ▼
                                                          ┌────────────────────────────┐
                                                          │  Xbox Game Bar widget     │
                                                          │  «Scrcpy Music» (Win + G) │
                                                          │  обложка · трек · слова   │
                                                          └────────────────────────────┘
```

| Узел | Что делает | Технологии |
|---|---|---|
| **ScrcpyBar Companion** | Читает активную `MediaSession`, отдаёт метаданные, обложку и принимает команды. Требует только «Доступ к уведомлениям» — без root и без accessibility. | Kotlin, `NotificationListenerService` |
| **ScrcpyBar.Bridge** | Опрашивает companion по USB-туннелю, кеширует обложки и слова, публикует сессию в Windows. Работает в трее, конфигурации не требует. | .NET 8, self-contained single-file |
| **ScrcpyBar Widget** | Оверлей Game Bar: обложка, название, исполнитель, караоке-слова, перемотка мышью, ⏮ ⏯ ⏭, громкость. | UWP XAML, `Microsoft.Gaming.XboxGameBar` |

Трафик физически не покидает USB-кабель: companion слушает только `127.0.0.1` на телефоне,
а наружу порт пробрасывается через `adb forward`.

<details>
<summary><b>Подробности протокола</b></summary>

* Companion биндит HTTP-API на `127.0.0.1:38080` внутри телефона.
* Bridge поднимает `adb forward tcp:39282 tcp:38080` и слушает `127.0.0.1:39281`.
* Виджет ходит в Bridge по HTTP и запрашивает loopback-exemption
  (`CheckNetIsolation LoopbackExempt`) — без него UWP-виджет не достучится до локального порта.
* Метаданные публикуются через **SMTC** (`Windows.Media.MediaPlayback`), поэтому трек
  дополнительно виден в панели «Windows Media Controls» (`Win + ↑`) и на медиаклавишах.

</details>

### Возможности

| | |
|---|---|
| **Обложка** | Тянется из `MediaSession`, кешируется на диске. |
| **Караоке-слова** | Синхронные строки с [lrclib.net](https://lrclib.net) (без API-ключа), кешируются. Текущая строка подсвечивается, спетое — белым. |
| **Переход по клику** | Клик по любой строке песни перематывает трек на нужную секунду. |
| **Перемотка мышью** | Тяни полосу прогресса прямо в виджете. |
| **Управление** | Кнопки ⏮ ⏯ ⏭, громкость, пауза — всё из превью. |
| **Один телефон** | Выбор устройства закрепляется в `device.txt` или через `SCRCPYBAR_DEVICE`. |

---

## Сборка и разработка

Инструкции по сборке, структура репозитория, скрипты и HTTP-API вынесены в
[docs/BUILDING.md](docs/BUILDING.md).

---

## Требования

* **PC:** Windows 10 19041 (20H1) или новее, .NET 8 (внутри Bridge).
* **Телефон:** Android 5.0+ с включённой отладкой по USB.
* **Кабель:** USB, на котором телефон виден в `adb devices`.
* **Виджет:** ставится сам из окна первоначальной настройки (нужен UAC).

---

## Troubleshooting

Таблица «симптом → решение» вынесена в [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md).

---

## Поддерживаемые плееры

Любое приложение с активной `MediaSession`. Проверено на YouTube Music; поддерживаются
Spotify, Яндекс Музыка, VK Музыка, SoundCloud, Apple Music, Deezer и системные плееры Android.

---

## Лицензия

[MIT](LICENSE) © ScrcpyBar contributors
