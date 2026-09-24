using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TNovDesktop
{
    /// <summary>
    /// Единая точка создания CoreWebView2Environment для всего приложения.
    /// Все вкладки обязаны использовать одно и то же окружение (и одну папку профиля):
    /// несколько окружений с разными опциями на одной папке приводят к ошибкам
    /// инициализации WebView2, а разъезжающиеся опции ломают, в частности, медиа в звонках.
    /// </summary>
    internal static class WebViewEnvironment
    {
        private static readonly SemaphoreSlim _gate = new(1, 1);
        private static CoreWebView2Environment? _shared;

        // Флаги передаются базовому браузеру Edge WebView2 при старте процесса.
        //  --autoplay-policy=no-user-gesture-required — иначе входящее аудио в звонках
        //    не воспроизводится без явного клика пользователя.
        //  --disable-features=AudioServiceSandbox,AudioServiceOutOfProcess — лечит
        //    NotReadableError ("Could not start audio source"): песочница/изоляция
        //    аудио-сервиса Chromium в хост-приложении WebView2 не может открыть
        //    микрофон. Известная проблема, аналогичная Electron.
        private const string BrowserArguments =
            "--autoplay-policy=no-user-gesture-required " +
            "--disable-features=AudioServiceSandbox,AudioServiceOutOfProcess";

        public static async Task<CoreWebView2Environment> GetAsync()
        {
            if (_shared != null)
                return _shared;

            await _gate.WaitAsync();
            try
            {
                if (_shared == null)
                {
                    string userDataFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "TNovDesktop",
                        "WebView2UserData");
                    Directory.CreateDirectory(userDataFolder);

                    var options = new CoreWebView2EnvironmentOptions(BrowserArguments);

                    _shared = await CoreWebView2Environment.CreateAsync(
                        browserExecutableFolder: null,
                        userDataFolder: userDataFolder,
                        options: options);

                    _shared.BrowserProcessExited += OnBrowserProcessExited;
                    Log.Write($"[WEBVIEW2] Окружение создано. Runtime={_shared.BrowserVersionString}; args={BrowserArguments}");
                }
            }
            finally
            {
                _gate.Release();
            }

            return _shared;
        }

        /// <summary>
        /// Сбрасывает кэш окружения после смерти browser-процесса, чтобы вкладки
        /// могли создать новое через GetAsync.
        /// </summary>
        public static void Reset()
        {
            _shared = null;
        }

        private static void OnBrowserProcessExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e)
        {
            Log.Write($"[WEBVIEW2] BrowserProcessExited kind={e.BrowserProcessExitKind} pid={e.BrowserProcessId}");
            _shared = null;
        }
    }
}
