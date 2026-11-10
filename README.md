# TrayAlways

Keeps **every** Windows 11 notification-area (tray) icon visible on the taskbar — including
icons from apps you install or update later. A single ~120 KB exe, no installer, no admin rights,
no runtime to install (uses .NET Framework 4.8, which ships with Windows 10/11). 35 languages.

### [⬇ Download TrayAlways.exe](https://github.com/holopig/TrayAlways/releases/latest/download/TrayAlways.exe)

Run it, done. Click its tray icon for options; turn on **Start with Windows** to keep it running.
Windows may warn that the app is unrecognized, because it isn't code-signed yet. Click **More info → Run anyway**.
The exe is built from this source by [GitHub Actions](../../actions); each release lists its SHA-256.

*Русская версия ниже · [Скачать TrayAlways.exe](https://github.com/holopig/TrayAlways/releases/latest/download/TrayAlways.exe)*

## Why this exists

Windows 10 had "Always show all icons in the notification area". Windows 11 removed it.
Now every new tray icon lands in the `^` overflow flyout, and you have to switch it on by hand in
Settings → Personalization → Taskbar → Other system tray icons. When an app updates, its path
changes, Windows treats it as a new icon, and it gets hidden again.

The old trick `explorer shell:::{05d7b0f4-2121-4eff-bf6b-ed3f69b894d9}` still opens the
Windows 10 dialog, but since 2024 the checkbox does nothing. Windows 11 no longer reads that
setting.

## How it works

* Windows 11 keeps one subkey per tray icon under `HKCU\Control Panel\NotifyIconSettings`.
  The DWORD `IsPromoted` decides where the icon goes: `1` puts it on the taskbar, `0` or a missing value puts it in the overflow.
* TrayAlways waits on registry change notifications for that key (`RegNotifyChangeKeyValue`,
  no polling, about 0% CPU). When a new icon shows up, it sets `IsPromoted=1` and then writes and deletes a
  temporary value, so Explorer's per-icon watcher picks up the change right away.
* **Your choices still count.** If you switch an icon *off* in Settings while TrayAlways is running,
  it stays hidden. Switching it back *on* undoes that. The "Kept hidden" menu lists these icons.
* If Explorer restarts, TrayAlways runs another pass.

## Usage

Run `TrayAlways.exe`. It puts its own small icon in the tray. Click it for the menu:

| Menu item | What it does |
|---|---|
| Always show all icons | Pause or resume |
| Respect icons hidden in Windows Settings | The "your choices still count" behaviour above |
| Kept hidden (N) | Icons you hid. Click one to show it again |
| Apply now | Force a pass |
| Restart Explorer | Fallback if an icon didn't move on its own |
| Diagnostics… | Shows what moved, for bug reports |
| Start with Windows | Adds an `HKCU\...\Run` entry |
| Language | Follows the Windows display language by default; pick any of 35 languages |
| Hide this icon | Hides TrayAlways' own icon. It keeps working; press **Ctrl+Alt+Shift+T** (or start the exe again) to bring the icon back |
| Shortcut to show the icon | Change that shortcut (must include Ctrl or Alt) |

When the icon is visible, the shortcut opens the menu. If another program has taken the shortcut
when TrayAlways starts, the icon is shown again, so you are never left without a way back.

Command line: `--once`, `--status`, `--diag`, `--restart-explorer`, `--autostart on|off`,
`--check-languages [culture…]`.

## Translations

TrayAlways ships with the 35 languages that have full Windows 11 language packs: Arabic, Bulgarian, Chinese
(Simplified and Traditional), Croatian, Czech, Danish, Dutch, English, Estonian, Finnish, French,
German, Greek, Hebrew, Hungarian, Italian, Japanese, Korean, Latvian, Lithuanian, Norwegian,
Polish, Portuguese (Brazil and Portugal), Romanian, Russian, Serbian (Latin), Slovak, Slovenian,
Spanish, Swedish, Thai, Turkish and Ukrainian. Arabic and Hebrew use a right-to-left menu.

Each language is a plain `lang/<culture>.txt` file (`key = value`, UTF-8) that gets built into the exe.
To fix a translation or add a language, edit or copy a file, run `build.cmd`, then run
`TrayAlways.exe --check-languages` to find missing keys or mismatched `{0}` placeholders.
Translations were written with AI help, so corrections from native speakers are very welcome.
Log: `%LOCALAPPDATA%\TrayAlways\TrayAlways.log`. Settings: `HKCU\Software\TrayAlways`.

## Build

```bat
build.cmd
```

This builds with the `csc.exe` that ships with .NET Framework 4.x, so there is no SDK or Visual Studio to install.
The output is `bin\TrayAlways.exe`.

## Caveats

`NotifyIconSettings` is undocumented Windows behaviour and may change in a future update.
TrayAlways only touches `IsPromoted` (plus its own temporary refresh value) under your own
user's key. Deleting the exe and its `HKCU\Software\TrayAlways` key removes it completely.

## License

[MIT](LICENSE)

---

## По-русски

**TrayAlways** держит все значки трея Windows 11 на панели задач, включая значки новых и
обновлённых программ. Это один exe примерно на 120 КБ: без установки и без прав администратора.

**[Скачать TrayAlways.exe](https://github.com/holopig/TrayAlways/releases/latest/download/TrayAlways.exe)** и запустить. Если Windows
предупредит о неизвестном приложении (exe пока не подписан), нажмите «Подробнее → Выполнить в любом случае».

* Windows 11 хранит видимость каждого значка в `HKCU\Control Panel\NotifyIconSettings\<id>\IsPromoted`.
  У новых значков этого значения нет, поэтому они всегда уходят в меню `^`.
* TrayAlways ждёт уведомлений об изменениях этого ключа (без опроса). Каждому новому значку он ставит
  `IsPromoted=1` и «пинает» Explorer, чтобы значок переместился сразу.
* Рецепт `shell:::{05d7b0f4-...}` открывает старое окно, но в Windows 11 галочка там ни на что не влияет.
* Если выключить значок в «Параметрах → Панель задач → Другие значки», TrayAlways оставит его
  скрытым. Если включить обратно, значок снова будет показываться.
* Меню открывается по клику на значок TrayAlways. Пункт «Перезапустить Проводник» нужен на случай, если
  какой-то значок не переместился сам.
* Интерфейс переведён на 35 языков Windows. Язык выбирается автоматически по языку системы, а в меню
  «Язык (Language)» его можно сменить.
* Собственный значок TrayAlways можно скрыть: программа продолжит работать. Вернуть значок можно
  сочетанием **Ctrl+Alt+Shift+T** (его можно поменять в меню) или повторным запуском exe.
