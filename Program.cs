// VoskKimiBridge — фоновый голосовой ввод для Kimi Work (C#, без окна).
//
//   Insert (с любыми модификаторами) — включить/выключить микрофон
//   Win+Insert или Win+F12           — выход
//
// (F12 не используется: на этом ПК она уже занята другой программой.)
//
// Распознанный текст копируется в буфер обмена (вставка — Ctrl+V).
// Статус пишется в лог: %USERPROFILE%\vosk-kimi\bridge.log
// Работает полностью офлайн.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;
using Vosk;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace VoskKimiBridge
{
    internal static class Program
    {
        // Пути к моделям: сначала как на основном ПК, иначе — в
        // %USERPROFILE%\vosk-kimi\models, иначе — в папке models рядом с exe.
        // Так проект работает на любом компьютере без правки кода.
        private static string ResolveModelPath(string preferred, string fileName)
        {
            if (File.Exists(preferred) || Directory.Exists(preferred)) return preferred;
            var userPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "vosk-kimi", "models", fileName);
            if (File.Exists(userPath) || Directory.Exists(userPath)) return userPath;
            var localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", fileName);
            if (File.Exists(localPath) || Directory.Exists(localPath)) return localPath;
            return preferred; // пусть упадёт с понятным исключением о пути
        }

        private static readonly string ModelPath =
            ResolveModelPath(@"C:\Users\vm1\vosk-kimi\models\vosk-model-ru-0.42", "vosk-model-ru-0.42");
        internal static readonly string WhisperModelPath =
            ResolveModelPath(@"C:\Users\vm1\vosk-kimi\models\ggml-large-v3.bin", "ggml-large-v3.bin");
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "vosk-kimi", "bridge.log");

        [STAThread]
        private static void Main()
        {
            // Логируем необработанные исключения (управляемые; нативный
            // краш libvosk они не спасут, но диагностику дают).
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Log($"НЕОБРАБОТАННОЕ ИСКЛЮЧЕНИЕ: {args.ExceptionObject}");
            Application.ThreadException += (_, args) =>
                Log($"Исключение UI-потока: {args.Exception}");

            // Защита от двойного запуска.
            using var mutex = new Mutex(true, @"Global\VoskKimiBridgeCSharpMutex", out bool createdNew);
            if (!createdNew)
                return;

            Log("=== Старт моста ===");
            Log("Загрузка модели (1-3 минуты)...");
            Vosk.Vosk.SetLogLevel(-1);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var model = new Model(ModelPath);
            sw.Stop();
            Log($"Модель загружена за {sw.Elapsed.TotalSeconds:F1} сек.");

            ApplicationConfiguration.Initialize();
            Application.Run(new BridgeForm(model));
        }

        internal static void Log(string message)
        {
            var line = $"{DateTime.Now:HH:mm:ss} {message}";
            try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
        }
    }

    // Скрытая форма: глобальный low-level хук клавиатуры (WH_KEYBOARD_LL).
    // Надёжнее RegisterHotKey: регистрации не «отмирают» и не отбираются
    // другими программами, а саму клавишу можно поглощать.
    internal class BridgeForm : Form
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int VK_INSERT = 0x2D;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        private HookProc? _proc;
        private IntPtr _hook = IntPtr.Zero;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private readonly Model _model;
        private NotifyIcon? _tray;
        private Button? _micButton;
        private WaveInEvent? _waveIn;
        private VoskRecognizer? _recognizer;
        private bool _listening;
        private readonly StringBuilder _sessionText = new();
        private readonly System.Windows.Forms.Timer _topmostTimer = new() { Interval = 1000 };
        // Блокировка доступа к распознавателю: аудио-колбэк (поток NAudio)
        // и выключение микрофона (UI-поток) не должны пересекаться —
        // иначе Dispose во время AcceptWaveform роняет libvosk.
        private readonly object _recLock = new();

        // === Whisper: основной движок финального распознавания ===
        // Vosk слушает в реальном времени и даёт промежуточный текст
        // (для индикации), но в буфер обмена попадает результат Whisper:
        // он заметно точнее (large-v3, GPU Vulkan). Инициализация ленивая —
        // в фоне после старта формы.
        private WhisperFactory? _whisperFactory;
        private readonly object _whisperLock = new();
        private MemoryStream _sessionPcm = new();
        private int _sessionId;
        private bool _whisperTranscribing;
        // Была ли в сессии речь по мнению Vosk. Whisper на чистом шуме/
        // тишине склонен к галлюцинациям («Продолжение следует…») —
        // если Vosk ничего не слышал, Whisper не вызываем вообще.
        private bool _sessionHeardSpeech;

        // Константы SetWindowPos: окно всегда поверх остальных.
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        public BridgeForm(Model model)
        {
            _model = model;

            // Плавающая мини-панель с кнопкой — работает даже когда
            // клавиатурный ввод не доходит до системы (VM-режимы).
            Text = "VoskKimiBridge";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(210, 62);
            var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
            Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);

            _micButton = new Button
            {
                Dock = DockStyle.Fill,
                Text = "🎤 Микрофон: ВЫКЛ",
                Font = new Font("Segoe UI", 12, FontStyle.Bold)
            };
            _micButton.Click += (_, _) => ToggleMicrophone();
            Controls.Add(_micButton);

            // Обычный TopMost иногда проигрывает другим поверх-окнам.
            // Раз в секунду принудительно возвращаем кнопку на самый верх.
            _topmostTimer.Tick += (_, _) => KeepOnTop();
            _topmostTimer.Start();

            Load += (_, _) => { CreateTrayIcon(); InstallHook(); KeepOnTop(); InitWhisperInBackground(); };
            FormClosed += (_, _) => Cleanup();
        }

        // Whisper грузится в фоне: не задерживает старт окна, а к первой
        // диктовке почти наверняка уже готова.
        private void InitWhisperInBackground()
        {
            Task.Run(() =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    lock (_whisperLock)
                    {
                        RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary>
                        {
                            RuntimeLibrary.Vulkan,
                            RuntimeLibrary.Cuda,
                            RuntimeLibrary.Cpu
                        };
                        _whisperFactory = WhisperFactory.FromPath(Program.WhisperModelPath);
                    }
                    Program.Log($"Whisper готов за {sw.Elapsed.TotalSeconds:F0} с, рантайм: {RuntimeOptions.LoadedLibrary}");
                }
                catch (Exception ex)
                {
                    Program.Log($"Whisper НЕДОСТУПЕН, остаёмся на Vosk: {ex.Message}");
                    _whisperFactory = null;
                }
            });
        }

        private void KeepOnTop()
        {
            if (IsDisposed || Disposing) return;
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        private void UpdateMicButton()
        {
            if (_micButton == null) return;
            _micButton.Text = _listening ? "🎤 Микрофон: ВКЛ — выкл." : "🎤 Микрофон: ВЫКЛ — вкл.";
            _micButton.BackColor = _listening ? Color.MistyRose : Color.LightGreen;
        }

        // Значок в трее: уведомления + переключение мышью (двойной клик),
        // на случай когда клавиатурный ввод не доходит до системы (VM и т.п.).
        private void CreateTrayIcon()
        {
            var menu = new ContextMenuStrip();
            var toggleItem = new ToolStripMenuItem("Включить микрофон");
            toggleItem.Click += (_, _) => ToggleMicrophone();
            var exitItem = new ToolStripMenuItem("Выход");
            exitItem.Click += (_, _) =>
            {
                Program.Log("Выход через меню трея.");
                Cleanup();
                Application.Exit();
            };
            menu.Items.Add(toggleItem);
            menu.Items.Add(exitItem);

            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Visible = true,
                Text = "VoskKimiBridge — микрофон выкл",
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (_, _) => ToggleMicrophone();
            UpdateTrayMenuText(toggleItem);
            _toggleMenuItem = toggleItem;
        }

        private ToolStripMenuItem? _toggleMenuItem;

        private void UpdateTrayMenuText(ToolStripMenuItem? item = null)
        {
            item ??= _toggleMenuItem;
            if (item != null)
                item.Text = _listening ? "Выключить микрофон" : "Включить микрофон";
        }

        private void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, int ms = 4000)
        {
            try { _tray?.ShowBalloonTip(ms, title, text, icon); } catch { }
        }

        private void InstallHook()
        {
            _proc = HookCallback;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
            Program.Log(_hook != IntPtr.Zero
                ? "Хук клавиатуры установлен. Insert — вкл/выкл микрофона, Win+Insert — выход."
                : "ОШИБКА: не удалось установить хук клавиатуры.");
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam.ToInt32() == WM_KEYDOWN)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (kb.vkCode == VK_INSERT && (kb.flags & 0x4000) == 0) // без автоповтора
                {
                    bool win = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0
                            || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;
                    if (win)
                    {
                        Program.Log("Выход по Win+Insert.");
                        BeginInvoke(() => { Cleanup(); Application.Exit(); });
                    }
                    else
                    {
                        // ВАЖНО: только через UI-поток! Прямой вызов из потока
                        // хука гонялся с аудио-колбэком и ронял libvosk
                        // (access violation, краш процесса).
                        BeginInvoke(new Action(ToggleMicrophone));
                    }
                    return (IntPtr)1; // поглощаем клавишу, в приложения она не уходит
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private void ToggleMicrophone()
        {
            if (_listening)
            {
                StopMicrophone();
                Program.Log("Микрофон: ВЫКЛ");
                if (_tray != null) _tray.Text = "VoskKimiBridge — микрофон выкл";
                UpdateTrayMenuText();
                UpdateMicButton();
                BeepSignal(false);
                return;
            }

            _recognizer = new VoskRecognizer(_model, 16000f);
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100
            };
            _waveIn.DataAvailable += OnAudioData;
            _waveIn.StartRecording();

            _listening = true;
            _sessionText.Clear(); // новая сессия — буфер собирается заново
            _sessionPcm = new MemoryStream(); // и сырой PCM для Whisper тоже
            _sessionHeardSpeech = false;
            _sessionId++;
            Program.Log("Микрофон: ВКЛ — говорите");
            if (_tray != null) _tray.Text = "VoskKimiBridge — микрофон ВКЛ";
            UpdateTrayMenuText();
            UpdateMicButton();
            Notify("VoskKimiBridge", "Микрофон включён — говорите", ToolTipIcon.Info, 2000);
            BeepSignal(true);
        }

        private void StopMicrophone()
        {
            _listening = false;
            if (_waveIn != null)
            {
                _waveIn.DataAvailable -= OnAudioData;
                _waveIn.StopRecording(); // ждёт завершения потока записи
                _waveIn.Dispose();
                _waveIn = null;
            }
            // Сбрасываем накопленное: если речь шла до самого выключения
            // и паузы не было — фраза иначе потерялась бы.
            // Блокировка: гарантирует, что аудио-колбэк уже не внутри
            // AcceptWaveform, когда мы освобождаем распознаватель.
            byte[] pcm;
            string? voskFinal = null;
            lock (_recLock)
            {
                if (_recognizer != null)
                {
                    try
                    {
                        voskFinal = ExtractText(_recognizer.FinalResult());
                        if (!string.IsNullOrWhiteSpace(voskFinal))
                        {
                            _sessionHeardSpeech = true;
                            Program.Log($"Финал (Vosk, индикация): {voskFinal}");
                        }
                    }
                    catch (Exception ex) { Program.Log($"ОШИБКА финализации: {ex.Message}"); }
                    try { _recognizer.Dispose(); } catch { }
                    _recognizer = null;
                }
                // Забираем сырой PCM сессии — аудио-колбэк уже остановлен
                // (DataAvailable отписан до StopRecording), гонки нет.
                pcm = _sessionPcm.ToArray();
            }
            Program.Log($"Сессия завершена, аудиобуферов: {_bufferCount}, PCM: {pcm.Length / 32000.0:F1} с");
            _bufferCount = 0;

            // === Финальное распознавание — Whisper (точнее Vosk) ===
            // Не вызываем Whisper, если Vosk не слышал речи (тишина/шум —
            // Whisper галлюцинирует), и если речь слишком короткая.
            // Фолбэк на Vosk — только если Whisper совсем недоступен.
            var factory = _whisperFactory;
            if (factory == null || pcm.Length < 32000 || !_sessionHeardSpeech)
            {
                if (factory != null && !_sessionHeardSpeech)
                    Program.Log("Vosk не услышал речи — Whisper пропущен (анти-галлюцинация).");
                if (factory != null && _sessionHeardSpeech && pcm.Length < 32000)
                    Program.Log("Слишком короткая речь — Whisper пропущен.");
                TranscribeFallbackVosk(voskFinal);
                return;
            }

            var sid = _sessionId;
            _whisperTranscribing = true;
            Notify("VoskKimiBridge", "Распознаю (Whisper)…", ToolTipIcon.Info, 2000);
            Task.Run(async () =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    // PCM 16 бит signed → float [-1;1], моно 16 кГц.
                    var samples = new float[pcm.Length / 2];
                    for (int i = 0; i < samples.Length; i++)
                    {
                        short s = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8));
                        samples[i] = s / 32768f;
                    }

                    var prompt = BuildWhisperPrompt();
                    var b = factory.CreateBuilder().WithLanguage("ru");
                    if (!string.IsNullOrEmpty(prompt))
                        b = b.WithPrompt(prompt);

                    var sb = new StringBuilder();
                    using (var proc = b.Build())
                    {
                        await foreach (var seg in proc.ProcessAsync(samples))
                            sb.Append(seg.Text);
                    }

                    var text = sb.ToString().Trim();
                    Program.Log($"Whisper распознал за {sw.Elapsed.TotalSeconds:F1} с: {text}");

                    if (IsWhisperHallucination(text))
                    {
                        Program.Log("Результат Whisper отброшен как типичная галлюцинация.");
                        return;
                    }

                    // Только если за это время микрофон не включили заново —
                    // иначе устаревший результат испортил бы новую сессию.
                    if (sid == _sessionId && !string.IsNullOrWhiteSpace(text))
                        BeginInvoke(() => PushToClipboard(text));
                }
                catch (Exception ex)
                {
                    Program.Log($"ОШИБКА Whisper: {ex.Message}");
                    BeginInvoke(() => TranscribeFallbackVosk(voskFinal));
                }
                finally { _whisperTranscribing = false; }
            });
        }

        // Типичные галлюцинации Whisper на тишине/музыке/шуме.
        // Сравниваем «очищенный» текст: без знаков препинания и пробелов
        // в начале/конце, регистронезависимо.
        private static readonly string[] WhisperHallucinations =
        {
            "продолжение следует",
            "продолжение следует...",
            "субтитры создавал",
            "субтитры сделал",
            "подписи создавал",
            "спасибо за просмотр",
            "ставьте лайк",
        };

        private static bool IsWhisperHallucination(string text)
        {
            var clean = text.Trim().Trim('.', '!', '?', '…', ' ').ToLowerInvariant();
            foreach (var h in WhisperHallucinations)
                if (clean == h.Trim().Trim('.', '…', ' '))
                    return true;
            return false;
        }

        // Фолбэк, если Whisper не загрузился или упал: пушим в буфер
        // финальный текст Vosk (менее точный, но лучше чем ничего).
        private void TranscribeFallbackVosk(string? voskFinal)
        {
            if (string.IsNullOrWhiteSpace(voskFinal))
            {
                Notify("VoskKimiBridge", "Текст не распознан.", ToolTipIcon.Warning, 3000);
                return;
            }
            Program.Log("Фолбэк: в буфер обмена — финал Vosk.");
            PushToClipboard(voskFinal);
        }

        // Промпт Whisper: контекст диктования + словарь терминов.
        // Помогает модели писать «Revit», «Navisworks» и т.п. английскими
        // буквами ещё до пост-обработки FixTerms.
        private static string BuildWhisperPrompt()
        {
            try
            {
                var vals = Terms
                    .Select(kv => kv.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (vals.Count == 0) return "";
                var p = "Техническое диктование на русском языке, речь идёт о программах, разработке и строительном ПО. Термины: "
                        + string.Join(", ", vals);
                return p.Length > 1600 ? p.Substring(0, 1600) : p;
            }
            catch { return ""; }
        }

        private int _bufferCount;

        private void OnAudioData(object? sender, WaveInEventArgs e)
        {
            lock (_recLock)
            {
                if (!_listening || _recognizer == null)
                    return;

                try
                {
                    // Сырой PCM копируем под _recLock — StopMicrophone
                    // забирает его после того же лока, гонки нет.
                    _sessionPcm.Write(e.Buffer, 0, e.BytesRecorded);

                    _bufferCount++;
                    if (_bufferCount % 50 == 0)
                        Program.Log($"Аудио идёт: {_bufferCount} буферов"); // ~5 сек речи

                    if (_recognizer.AcceptWaveform(e.Buffer, e.BytesRecorded))
                    {
                        // Промежуточный текст Vosk — только индикация.
                        // В буфер обмена попадает финальный результат Whisper.
                        var text = ExtractText(_recognizer.Result());
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            _sessionHeardSpeech = true;
                            Program.Log($"Промежуточно (Vosk): {text}");
                            Notify("Слушаю…", text, ToolTipIcon.Info, 1500);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Управляемое исключение — логируем, процесс не роняем.
                    Program.Log($"ОШИБКА распознавания: {ex.Message}");
                }
            }
        }

        // Накапливаем текст всей сессии и кладём в буфер обмена.
        // Clipboard требует STA-потока — аудио-колбэк идёт с чужого
        // потока, поэтому вызов через BeginInvoke в UI-поток формы.
        private void PushToClipboard(string phrase)
        {
            phrase = FixTerms(phrase);
            _sessionText.Append(phrase).Append(' ');
            var all = _sessionText.ToString();
            BeginInvoke(() =>
            {
                try { Clipboard.SetText(all); }
                catch (Exception ex) { Program.Log($"ОШИБКА буфера: {ex.Message}"); }
            });
            Notify("Распознано", phrase);
        }

        // Словарь технических терминов: VosK пишет их русскими буквами
        // как слышит («гитхаб», «навис», «автокат»), а нам нужны точные
        // английские написания. Заменяем после распознавания.
        // Словарь грузится из terms.json рядом с exe. Файл можно править
        // в любой момент: бридж замечает изменение и перезагружает словарь
        // без перезапуска (проверка при каждой замене, по дате файла).
        // Ключ — как может услышать модель, значение — правильный термин.
        private static KeyValuePair<string, string>[] _terms = Array.Empty<KeyValuePair<string, string>>();
        private static DateTime _termsStamp;
        private static readonly object _termsLock = new();

        private static KeyValuePair<string, string>[] Terms
        {
            get
            {
                lock (_termsLock)
                {
                    try
                    {
                        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "terms.json");
                        var stamp = File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue;
                        if (_terms.Length == 0 || stamp != _termsStamp)
                        {
                            _terms = LoadTerms(path);
                            _termsStamp = stamp;
                            Program.Log($"Словарь терминов: {_terms.Length} форм.");
                        }
                    }
                    catch { }
                    return _terms;
                }
            }
        }

        private static KeyValuePair<string, string>[] LoadTerms(string path)
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                if (File.Exists(path))
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                    if (dict != null)
                        foreach (var kv in dict)
                            list.Add(new KeyValuePair<string, string>(kv.Key, kv.Value));
                }
            }
            catch (Exception ex) { Program.Log($"ОШИБКА загрузки terms.json: {ex.Message}"); }

            // Встроенный запасной минимум, если файла нет.
            if (list.Count == 0)
            {
                list.Add(new("гитхаб", "GitHub"));
                list.Add(new("навис", "Navisworks"));
                list.Add(new("автокад", "AutoCAD"));
                list.Add(new("ревит", "Revit"));
                list.Add(new("воск", "Vosk"));
            }

            // Сначала длинные ключи: «бим модель» должна замениться
            // раньше, чем отдельное «бим».
            list.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            return list.ToArray();
        }

        private static string FixTerms(string text)
        {
            foreach (var (from, to) in Terms)
            {
                // Границы слова, чтобы «бим» не ломало «бимера» и т.п.
                text = Regex.Replace(text, $@"(?<!\w){Regex.Escape(from)}(?!\w)", to,
                    RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            }
            return text;
        }

        private void Cleanup()
        {
            _topmostTimer.Stop();
            _topmostTimer.Dispose();
            StopMicrophone();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            _model.Dispose();
        }

        private static string? ExtractText(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("text", out var t))
                    return t.GetString();
            }
            catch { }
            return null;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hHook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hHook, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool MessageBeep(uint type);

        private static void BeepSignal(bool on) => MessageBeep(on ? 0x30u : 0x10u);
    }

    // (Автовставка текста клавиатурными событиями больше не используется:
    //  вместо неё — копирование распознанного текста в буфер обмена.)
}
