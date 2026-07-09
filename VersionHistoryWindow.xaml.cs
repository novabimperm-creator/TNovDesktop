using System.Windows;

namespace TNovDesktop
{
    public partial class VersionHistoryWindow : Window
    {
        public VersionHistoryWindow(HoleGroupBaseItem item)
        {
            InitializeComponent();
            DataContext = item;   // окно привязывается напрямую к объекту
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}