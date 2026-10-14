using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace TNovDesktop
{
    /// <summary>Окно сводки по почте: выжимка сверху и письма за период (важные и непрочитанные — первыми).</summary>
    public partial class MailDigestWindow : Window
    {
        private readonly Action<string> _openItem;
        private readonly Action _openMail;

        internal MailDigestWindow(MailDigest digest, Action<string> openItem, Action openMail)
        {
            InitializeComponent();
            _openItem = openItem;
            _openMail = openMail;

            HeadlineText.Text = digest.Headline;
            SummaryText.Text = digest.Summary;
            ItemsList.ItemsSource = digest.Items.Select(i => new Row(i, digest.Until)).ToList();
        }

        private void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ItemsList.SelectedItem is Row row && !string.IsNullOrEmpty(row.Id))
                _openItem(row.Id);
        }

        private void OpenMailButton_Click(object sender, RoutedEventArgs e)
        {
            _openMail();
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private sealed class Row
        {
            public Row(MailItemInfo item, DateTime until)
            {
                Id = item.Id;
                string marks = (item.IsHighImportance ? "❗ " : "") + (item.IsFlagged ? "🚩 " : "")
                               + (item.HasAttachments ? "📎 " : "");
                SenderLine = marks + MailDigest.SenderOf(item);
                Subject = MailDigest.Shorten(item.Subject, 200);
                Preview = item.Preview;
                var ru = CultureInfo.GetCultureInfo("ru-RU");
                TimeText = item.Received.Date == until.Date
                    ? item.Received.ToString("HH:mm", ru)
                    : item.Received.ToString("ddd d MMM, HH:mm", ru);
                Weight = item.IsRead ? FontWeights.Normal : FontWeights.SemiBold;
                Opacity = item.IsRead && !MailDigest.IsImportant(item) ? 0.7 : 1.0;
            }

            public string Id { get; }
            public string SenderLine { get; }
            public string Subject { get; }
            public string Preview { get; }
            public string TimeText { get; }
            public FontWeight Weight { get; }
            public double Opacity { get; }
        }
    }
}
