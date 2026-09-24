using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TNovClient
{
    public partial class NotificationWindow : Window
    {
        private DispatcherTimer _timer;
        private Storyboard _fadeInStoryboard;
        private Storyboard _fadeOutStoryboard;
        private Action _onClick;

        public NotificationWindow()
        {
            InitializeComponent();
            this.Loaded += OnLoaded;

            _fadeInStoryboard = (Storyboard)this.Resources["FadeInStoryboard"];
            _fadeOutStoryboard = (Storyboard)this.Resources["FadeOutStoryboard"];
        }
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var workArea = SystemParameters.WorkArea;

            double width = Math.Max(1, this.ActualWidth);
            double height = Math.Max(1, this.ActualHeight);

            this.Left = Math.Max(0, workArea.Right - width - 20);
            this.Top = Math.Max(0, workArea.Bottom - height - 20);
        }
        /// <summary>
        /// Показать уведомление с заголовком и текстом.
        /// Автоматически закроется через заданное количество секунд с анимацией затухания.
        /// </summary>
        public void ShowNotification(string title, string message, int durationSeconds = 7, Action onClick = null)
        {
            TitleBlock.Text = title;
            MessageBlock.Text = message;
            _onClick = onClick;

            this.Opacity = 0;
            this.Show();
            _fadeInStoryboard.Begin(this);

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(durationSeconds)
            };
            _timer.Tick += (s, e) =>
            {
                _timer.Stop();
                _fadeOutStoryboard.Begin(this);
            };
            _timer.Start();
        }

        private void FadeOut_Completed(object sender, EventArgs e)
        {
            this.Close();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            try { _onClick?.Invoke(); } catch { }
            _timer?.Stop();
            _fadeOutStoryboard.Begin(this);
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer?.Stop();
            _timer = null;
            base.OnClosed(e);
        }
    }
}