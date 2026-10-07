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
    public partial class ProjectControl : UserControl
    {
        private readonly string _networkPath = @"\\fs-nova\Distr\0.For Admin\_TNov";
        private readonly string _projectFolderPath = @"\\fs-nova\NOVA\01_ПРОЕКТИРОВАНИЕ";
        private ObservableCollection<HoleGroupBaseItem> _holeGroups;
        private ICollectionView _holeGroupsView;
        private bool _dataLoaded = false;

        public ProjectControl()
        {
            InitializeComponent();
            InitializeGrid();
            SetupComboBoxFilters();
            Loaded += ProjectControl_Loaded;
            UpdateUserName();
        }

        private void ProjectControl_Loaded(object sender, RoutedEventArgs e)
        {
            CheckNetworkFolder();
            UpdateUserName();
        }

        private void CheckNetworkFolder()
        {
            try
            {
                if (Directory.Exists(_networkPath))
                {
                    StatusText.Text = "✅ Доступ есть";
                    StatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
                    //HintText.Text = "Вы подключены к корпоративной сети.\nСо временем здесь появятся новые инструменты!";
                    StatusPanel.Visibility = Visibility.Collapsed;
                    HeadPanel.Visibility = Visibility.Visible;
                    //OpenProjectFolderButton.Visibility = Visibility.Visible;
                    TableContainer.Visibility = Visibility.Visible;
                    // Автоматическая загрузка данных при первом успешном подключении
                    if (!_dataLoaded)
                    {
                        _dataLoaded = true;
                        RefreshData();
                    }
                }
                else
                {
                    StatusText.Text = "❌ Проверьте подключение к корпоративной сети";
                    StatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
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
                StatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush");
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
            }
            else
            {
                UserNamePrefixTextBlock.Text = string.Empty;
                UserNameTextBlock.Text = string.Empty;
                UserRolePrefixTextBlock.Text = string.Empty;
                UserRoleTextBlock.Text = string.Empty;
            }
        }

        private void InitializeGrid()
        {
            _holeGroups = new ObservableCollection<HoleGroupBaseItem>();
            _holeGroupsView = CollectionViewSource.GetDefaultView(_holeGroups);
            _holeGroupsView.Filter = FilterPredicate;
            _holeGroupsView.SortDescriptions.Add(new SortDescription("TaskDate", ListSortDirection.Descending));
            HoleGrid.ItemsSource = _holeGroupsView;
        }

        public void RefreshData()
        {
            List<HoleGroupBaseItem> items = GetHoleGroups();
            _holeGroups.Clear();
            foreach (var item in items)
            {
                _holeGroups.Add(item);
            }

            // Обновляем выпадающие списки
            ProjectComboBox.ItemsSource = _holeGroups.Select(x => x.ProjectName).Distinct().OrderBy(x => x).ToList();
            ModelComboBox.ItemsSource = _holeGroups.Select(x => x.ModelName).Distinct().OrderBy(x => x).ToList();

            _holeGroupsView.Refresh();
            TableContainer.Visibility = Visibility.Visible;
        }
        private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
        {
            // Сбрасываем текстовые поля в комбинированных списках
            ProjectComboBox.Text = string.Empty;
            ModelComboBox.Text = string.Empty;

            // Очищаем поля фильтров в заголовках столбцов
            ClearHeaderTextBox("FilterFileName");
            ClearHeaderTextBox("FilterHoleGroup");
            ClearHeaderTextBox("FilterInitiator");
            ClearHeaderTextBox("FilterSTStatus");

            // Обновляем представление данных
            _holeGroupsView?.Refresh();
        }

        private void ClearHeaderTextBox(string name)
        {
            var textBox = FindVisualChild<TextBox>(HoleGrid, name);
            if (textBox != null)
                textBox.Text = string.Empty;
        }
        private bool FilterPredicate(object obj)
        {
            if (obj is not HoleGroupBaseItem hole) return false;

            // Фильтр по проекту (текст из ComboBox)
            string projectFilter = ProjectComboBox.Text?.Trim();
            if (!string.IsNullOrEmpty(projectFilter))
            {
                if (hole.ProjectName?.IndexOf(projectFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            // Фильтр по модели
            string modelFilter = ModelComboBox.Text?.Trim();
            if (!string.IsNullOrEmpty(modelFilter))
            {
                if (hole.ModelName?.IndexOf(modelFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            // Текстовые фильтры в заголовках
            if (!PassTextFilter(GetFilterText("FilterFileName"), hole.ModelName)) return false;
            if (!PassTextFilter(GetFilterText("FilterHoleGroup"), hole.HoleGroupName)) return false;
            if (!PassTextFilter(GetFilterText("FilterInitiator"), hole.Initiator)) return false;
            if (!PassTextFilter(GetFilterText("FilterSTStatus"), hole.STStatus)) return false;

            return true;
        }

        private static bool PassTextFilter(string filterText, string fieldValue)
        {
            if (string.IsNullOrWhiteSpace(filterText)) return true;
            if (string.IsNullOrWhiteSpace(fieldValue)) return false;
            return fieldValue.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string GetFilterText(string filterName)
        {
            var textBox = FindVisualChild<TextBox>(HoleGrid, filterName);
            return textBox?.Text ?? string.Empty;
        }

        private void Filter_TextChanged(object sender, TextChangedEventArgs e)
        {
            _holeGroupsView?.Refresh();
        }

        private void SetupComboBoxFilters()
        {
            void Attach(ComboBox combo)
            {
                if (combo.Template?.FindName("PART_EditableTextBox", combo) is TextBox tb)
                {
                    tb.TextChanged += (s, e) => _holeGroupsView?.Refresh();
                }
            }
            ProjectComboBox.Loaded += (s, e) => Attach(ProjectComboBox);
            ModelComboBox.Loaded += (s, e) => Attach(ModelComboBox);
        }

        private static T FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T element && (name == null || element.Name == name))
                    return element;
                var result = FindVisualChild<T>(child, name);
                if (result != null)
                    return result;
            }
            return null;
        }

        public List<HoleGroupBaseItem> GetHoleGroups()
        {
            string taskFolder = "//fs-nova/Distr/0.For Admin/_TNov/tasks/";
            List<HoleGroupBaseItem> tasks = new List<HoleGroupBaseItem>();

            string projectListFile = File.ReadAllText("//fs-nova/Distr/0.For Admin/_TNov/CDE.txt");
            string[] lines = projectListFile.Split('\n');
            List<string> projects = new List<string>();
            foreach (string line in lines)
            {
                string[] elems = line.Split(',');
                projects.Add(elems[0]);
            }

            try
            {
                var jsonFiles = GetJsonFiles(taskFolder);
                foreach (var file in jsonFiles)
                {
                    string jsonContent = File.ReadAllText(file);
                    string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(file);
                    var existingItems = JsonConvert.DeserializeObject<List<HoleGroupBaseItem>>(jsonContent)
                                        ?? new List<HoleGroupBaseItem>();
                    foreach (var item in existingItems)
                    {
                        item.ModelName = fileNameWithoutExtension;
                        item.PrepareElements();
                        foreach (string p in projects)
                        {
                            if (fileNameWithoutExtension.Contains(p))
                            {
                                item.ProjectName = p;
                                break;
                            }
                        }
                        tasks.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка: {ex.Message}");
            }

            return tasks;
        }

        public static List<string> GetJsonFiles(string folderPath, bool recursive = false)
        {
            if (!Directory.Exists(folderPath))
                throw new DirectoryNotFoundException($"Папка не найдена: {folderPath}");

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var jsonFiles = Directory.GetFiles(folderPath, "*.json", searchOption).ToList();
            return jsonFiles;
        }
        /// <summary>
        /// Вложенная таблица элементов не должна «съедать» колесо: когда ей прокручивать
        /// некуда, прокрутка уходит внешней таблице журнала.
        /// </summary>
        private void NestedGrid_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (sender is not DependencyObject nested) return;
            var scroll = FindVisualChild<ScrollViewer>(nested, null);
            bool canScroll = scroll != null &&
                (e.Delta > 0 ? scroll.VerticalOffset > 0 : scroll.VerticalOffset < scroll.ScrollableHeight);
            if (canScroll) return;

            if (VisualTreeHelper.GetParent(nested) is not UIElement parent) return;
            e.Handled = true;
            parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            });
        }

        private void VersionHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is HoleGroupBaseItem item)
            {
                var historyWindow = new VersionHistoryWindow(item);
                historyWindow.Owner = Window.GetWindow(this);  // для модальности относительно основного окна
                historyWindow.ShowDialog();
            }
        }
    }

    public static class UserNameHelper
    {
        public static string GetCurrentUserName(bool revitExists)
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string iniPath = Path.Combine(appDataPath, "Autodesk", "Revit", "Autodesk Revit 2022", "Revit.ini");

            if (revitExists)
            {
                try
                {
                    string[] lines = File.ReadAllLines(iniPath);
                    bool inPartitionsSection = false;
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        if (trimmed == "[Partitions]")
                            inPartitionsSection = true;
                        else if (inPartitionsSection && trimmed.StartsWith("Username="))
                            return trimmed.Split('=')[1].Trim();
                        else if (trimmed.StartsWith("[") && inPartitionsSection)
                            break;
                    }
                }
                catch { }
            }
            return Environment.UserName;
        }
    }
}