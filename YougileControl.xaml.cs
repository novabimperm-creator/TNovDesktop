using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Path = System.IO.Path;

namespace TNovDesktop
{
    public partial class YougileControl : UserControl
    {
        private string _pendingUrl;

        private string _networkPath = @"\\fs-nova\Distr\0.For Admin\_TNov";
        private readonly string _projectFolderPath = @"\\fs-nova\NOVA\01_ПРОЕКТИРОВАНИЕ";

        private string MessengerUrl = "https://ru.yougile.com/team/";
        private string cacheFolder;
        private CoreWebView2Environment _webViewEnvironment;

        // _isInitialized – true, если WebView2 был успешно инициализирован (CoreWebView2 != null)
        private bool _isInitialized = false;
        // _isSubscribed – true, если подписки на события CoreWebView2 активны
        private bool _isSubscribed = false;

        public YougileControl()
        {
            InitializeComponent();
            Loaded += YougileControl_Loaded;
            Unloaded += YougileControl_Unloaded;
        }

        private async void YougileControl_Loaded(object sender, RoutedEventArgs e)
        {
            await WebView.EnsureCoreWebView2Async();

            // Инициализация завершена, можно установить флаг и подписаться на события навигации
            if (WebView.CoreWebView2 != null)
            {
                _isInitialized = true;
                // Если ещё не подписаны на навигацию
                if (!_isSubscribed)
                {
                    WebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
                    WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                    _isSubscribed = true;
                }
                // Если есть отложенный URL – выполнить навигацию
                if (!string.IsNullOrEmpty(_pendingUrl))
                {
                    WebView.CoreWebView2.Navigate(_pendingUrl);
                    _pendingUrl = null;
                }
            }
            else
            {
                ShowError("Не удалось инициализировать WebView2");
            }

            CheckNetworkFolder();
            UpdateUserName(); // здесь вызовет UpdateWebViewUrl() и сделает ещё одну навигацию (если нужно)
        }

        private void CheckNetworkFolder()
        {
            try
            {
                if (Directory.Exists(_networkPath))
                {
                    StatusText.Text = "✅ Доступ есть";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0, 150, 0));
                    string[] roles = File.ReadAllLines(_networkPath+"/roles.txt");
                    foreach (var role in roles)
                    {
                        if (role.Contains(UserNameTextBlock.Text))
                        {
                            string[] line = role.Split(',');
                            MessengerUrl = line[3];
                            break;
                        }
                    }
                    StatusPanel.Visibility = Visibility.Collapsed;
                    HeadPanel.Visibility = Visibility.Visible;
                    //OpenProjectFolderButton.Visibility = Visibility.Visible;
                    TableContainer.Visibility = Visibility.Visible;
                    
                }
                else
                {
                    StatusText.Text = "❌ Проверьте подключение к корпоративной сети";
                    StatusText.Foreground = new SolidColorBrush(Colors.Red);
                    HintText.Text = "Убедитесь, что вы подключены к сети предприятия.";
                    StatusPanel.Visibility = Visibility.Visible;
                    HeadPanel.Visibility = Visibility.Collapsed;
                    //OpenProjectFolderButton.Visibility = Visibility.Collapsed;
                    TableContainer.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                StatusText.Text = "❌ Ошибка доступа";
                StatusText.Foreground = new SolidColorBrush(Colors.Red);
                HintText.Text = "Возникла непредвиденная ошибка\nпри проверке подключения к корпоративной сети.";
                //OpenProjectFolderButton.Visibility = Visibility.Collapsed;
                TableContainer.Visibility = Visibility.Collapsed;
                StatusPanel.Visibility = Visibility.Visible;
                HeadPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void OpenProjectFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start("explorer.exe", _projectFolderPath);
            }
            catch
            {
                MessageBox.Show("Не удалось открыть папку.", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateUserName()
        {
            if (Directory.Exists(_networkPath))
            {
                bool revitExists = false;
                string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string iniPath = Path.Combine(appDataPath, "Autodesk", "Revit", "Autodesk Revit 2022", "Revit.ini");
                if (File.Exists(iniPath)) revitExists = true;
                string userName = UserNameHelper.GetCurrentUserName(revitExists);
                UserNameTextBlock.Text = userName;
                if (revitExists) UserNamePrefixTextBlock.Text = "Ваше имя в Revit:";
                else UserNamePrefixTextBlock.Text = "Ваше имя пользователя:";

                string userDepartment = UserRoleService.GetDepartmentCode(userName);
                UserRoleTextBlock.Text = UserRoleService.GetDepartmentLabel(userDepartment);
                UserRolePrefixTextBlock.Text = string.IsNullOrEmpty(userDepartment) ? "" : "Ваша роль:";

                UpdateWebViewUrl();
            }
            else
            {
                UserNamePrefixTextBlock.Text = string.Empty;
                UserNameTextBlock.Text = string.Empty;
                UserRolePrefixTextBlock.Text = string.Empty;
                UserRoleTextBlock.Text = string.Empty;

                WebView.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Visible;
            }
        }


        private static T FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T element && element.Name == name)
                    return element;
                var result = FindVisualChild<T>(child, name);
                if (result != null)
                    return result;
            }
            return null;
        }

        private void YougileControl_Unloaded(object sender, RoutedEventArgs e)
        {
            // Отключаем подписки, но НЕ сбрасываем _isInitialized и окружение
            if (_isSubscribed && WebView.CoreWebView2 != null)
            {
                WebView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
                WebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                WebView.CoreWebView2InitializationCompleted -= OnWebViewInitializationCompleted;
                _isSubscribed = false;
            }

            // Останавливаем навигацию, чтобы не грузила ресурсы в фоне
            WebView.CoreWebView2?.Stop();
        }

        private void UpdateWebViewUrl()
        {
            if (string.IsNullOrEmpty(UserNameTextBlock.Text))
                return;

            // Формируем URL с параметром user (пример)
            string user = UserNameTextBlock.Text;
            string newUrl = $"{MessengerUrl}?user={Uri.EscapeDataString(user)}";

            // Если WebView2 уже инициализирован – переходим
            if (_isInitialized && WebView.CoreWebView2 != null)
            {
                WebView.CoreWebView2.Navigate(newUrl);
            }
            else
            {
                // Иначе подписываемся на событие инициализации (если ещё не подписаны)
                if (!_isSubscribed)
                {
                    WebView.CoreWebView2InitializationCompleted += OnWebViewInitializationCompleted;
                    _isSubscribed = true;
                }
                // Сохраняем URL для последующей навигации
                _pendingUrl = newUrl;
            }
        }

        private void OnWebViewInitializationCompleted(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                _isInitialized = true;
                WebView.CoreWebView2InitializationCompleted -= OnWebViewInitializationCompleted;
                _isSubscribed = false;

                // Подписываемся на события навигации
                WebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
                WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                _isSubscribed = true;

                // Если есть отложенный URL – переходим
                if (!string.IsNullOrEmpty(_pendingUrl))
                {
                    WebView.CoreWebView2.Navigate(_pendingUrl);
                    _pendingUrl = null;
                }
            }
            else
            {
                ShowError("Не удалось инициализировать WebView2");
            }
        }

        #region Обработчики навигации

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                LoadingProgress.Visibility = Visibility.Visible;
                ErrorPanel.Visibility = Visibility.Collapsed;
                WebView.Visibility = Visibility.Visible;
            });
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                LoadingProgress.Visibility = Visibility.Collapsed;
                if (!e.IsSuccess)
                    ShowError($"Не удалось загрузить страницу: {e.WebErrorStatus}");
            });

        }

        private void ShowError(string message)
        {
            ErrorDetails.Text = message;
            ErrorPanel.Visibility = Visibility.Visible;
            WebView.Visibility = Visibility.Collapsed;
        }

        private void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Visible;
            WebView.CoreWebView2?.Reload();
        }

        #endregion

    }


}