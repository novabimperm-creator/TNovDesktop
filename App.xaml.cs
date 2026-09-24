using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace TNovDesktop
{
    public partial class App : Application
    {
        /// <summary>
        /// Имя мьютекса нельзя менять: старые и новые сборки должны взаимно исключаться.
        /// Совпадает с прежним именем из MainWindow.
        /// </summary>
        internal const string SingleInstanceMutexName = "TNovDesktop_SingleInstance_1234";

        /// <summary>
        /// Именованное событие: TNovClient сигналит, чтобы корректно завершить процесс перед ClickOnce.
        /// </summary>
        internal const string ExitRequestEventName = "TNovDesktop_ExitRequest";

        internal static bool AllowExit { get; private set; }

        private static Mutex? _mutex;
        private static EventWaitHandle? _exitRequest;
        private Thread? _exitListener;

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SW_RESTORE = 9;

        protected override void OnStartup(StartupEventArgs e)
        {
            if (!TryAcquireSingleInstance())
            {
                Log.Write("[STARTUP] Обнаружен уже запущенный экземпляр — активирую его и выхожу.");
                ActivateExistingWindow();
                Shutdown();
                return;
            }

            Log.Write($"[STARTUP] exe={Environment.ProcessPath} primaryInstance=true");
            StartExitListener();

            base.OnStartup(e);

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AllowExit = true;
            try { _exitRequest?.Dispose(); } catch { }
            _exitRequest = null;
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            _mutex = null;
            base.OnExit(e);
        }

        public static void RequestExit()
        {
            AllowExit = true;
            Current?.Dispatcher.BeginInvoke(() =>
            {
                try { Current.Shutdown(); }
                catch (Exception ex) { Log.Write($"[EXIT] Shutdown failed: {ex.Message}"); }
            });
        }

        private static bool TryAcquireSingleInstance()
        {
            var mutex = new Mutex(false, SingleInstanceMutexName);
            try
            {
                if (!mutex.WaitOne(TimeSpan.Zero))
                {
                    mutex.Dispose();
                    return false;
                }

                _mutex = mutex;
                return true;
            }
            catch (AbandonedMutexException)
            {
                Log.Write("[STARTUP] Предыдущий экземпляр завершился некорректно (abandoned mutex).");
                _mutex = mutex;
                return true;
            }
        }

        private void StartExitListener()
        {
            try
            {
                _exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ExitRequestEventName);
            }
            catch (Exception ex)
            {
                Log.Write($"[STARTUP] Не удалось создать событие выхода: {ex.Message}");
                return;
            }

            _exitListener = new Thread(WaitForExitRequest)
            {
                IsBackground = true,
                Name = "TNovDesktop.ExitListener"
            };
            _exitListener.Start();
        }

        private static void WaitForExitRequest()
        {
            try
            {
                EventWaitHandle? handle = _exitRequest;
                if (handle == null)
                    return;

                handle.WaitOne();
                if (AllowExit)
                    return;

                Log.Write("[EXIT] Получен запрос на завершение от TNovClient.");
                RequestExit();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                Log.Write($"[EXIT] Слушатель завершения: {ex.Message}");
            }
        }

        private static void ActivateExistingWindow()
        {
            IntPtr hWnd = FindWindow(null, "TNovDesktop");
            if (hWnd != IntPtr.Zero)
            {
                ShowWindow(hWnd, SW_RESTORE);
                SetForegroundWindow(hWnd);
            }
        }
    }
}
