using System.IO;

namespace TNovDesktop
{
    /// <summary>
    /// Минимальный файловый логгер. Пишет в %LOCALAPPDATA%\TNovDesktop\logs\app.log.
    /// Используется, в частности, для диагностики звонков (доступ к микрофону/камере).
    /// </summary>
    internal static class Log
    {
        private static readonly object _lock = new();

        private static readonly string _file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TNovDesktop", "logs", "app.log");

        public static string FilePath => _file;

        public static void Write(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                lock (_lock)
                {
                    File.AppendAllText(_file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
                }
            }
            catch { /* логирование не должно ронять приложение */ }
        }
    }
}
