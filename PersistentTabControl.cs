using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TNovDesktop
{
    /// <summary>
    /// TabControl, который оставляет содержимое уже открытых вкладок в визуальном дереве.
    /// Обычный TabControl выгружает SelectedContent (Unload) — для WebView2 это ломает
    /// HwndHost: вкладка перестаёт отрисовываться, хотя остальные живут.
    /// Вкладка создаётся при первом выборе, затем скрывается через Visibility.Hidden.
    /// </summary>
    public class PersistentTabControl : TabControl
    {
        private readonly Dictionary<TabItem, UIElement> _parked = new();
        private Panel? _holder;
        private bool _syncing;

        public PersistentTabControl()
        {
            Loaded += (_, _) => SyncItems();
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _holder = GetTemplateChild("PART_ItemsHolder") as Panel;
            SyncItems();
        }

        protected override void OnItemsChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            base.OnItemsChanged(e);
            SyncItems();
        }

        protected override void OnSelectionChanged(SelectionChangedEventArgs e)
        {
            base.OnSelectionChanged(e);
            SyncItems();
        }

        private void SyncItems()
        {
            if (_syncing || _holder == null)
                return;

            _syncing = true;
            try
            {
                foreach (var raw in Items)
                {
                    var tab = raw as TabItem ?? ItemContainerGenerator.ContainerFromItem(raw) as TabItem;
                    if (tab == null)
                        continue;

                    if (tab.IsSelected)
                    {
                        if (!_parked.TryGetValue(tab, out var hosted))
                        {
                            hosted = tab.Content as UIElement;
                            if (hosted == null)
                                continue;

                            tab.Content = null;
                            if (!ReferenceEquals(VisualTreeHelper.GetParent(hosted), _holder))
                                _holder.Children.Add(hosted);
                            _parked[tab] = hosted;
                        }

                        hosted.Visibility = Visibility.Visible;
                        hosted.IsHitTestVisible = true;
                        Panel.SetZIndex(hosted, 1);
                    }
                    else if (_parked.TryGetValue(tab, out var hosted))
                    {
                        hosted.Visibility = Visibility.Hidden;
                        hosted.IsHitTestVisible = false;
                        Panel.SetZIndex(hosted, 0);
                    }
                }
            }
            finally
            {
                _syncing = false;
            }
        }
    }
}
