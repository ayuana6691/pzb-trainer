using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PBZTrainer.Views
{
    internal sealed class ListDetailGrid : Grid
    {
        // Three table columns use 440 DIP; allow room for panel borders, margins and a scrollbar.
        private const double ListWidth = 490;
        private const double MinimumDetailWidth = 400;
        private readonly FrameworkElement _detail;
        private readonly double _preferredDetailWidth;
        private readonly ToggleButton _showDetail;

        public ListDetailGrid(FrameworkElement list, FrameworkElement detail, double preferredDetailWidth)
        {
            _detail = detail;
            _preferredDetailWidth = preferredDetailWidth;
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(new ColumnDefinition());
            RowDefinitions.Add(new RowDefinition());
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition());

            _showDetail = Ui.Chip("显示详情", true);
            _showDetail.HorizontalAlignment = HorizontalAlignment.Left;
            _showDetail.Margin = new Thickness(0, 5, 0, 5);
            _showDetail.Checked += (_, __) => InvalidateMeasure();
            _showDetail.Unchecked += (_, __) => InvalidateMeasure();
            SetRow(_showDetail, 1);
            Children.Add(list);
            Children.Add(_showDetail);
            Children.Add(_detail);
        }

        protected override Size MeasureOverride(Size constraint)
        {
            bool stacked = constraint.Width < ListWidth + MinimumDetailWidth;
            bool detailVisible = !stacked || _showDetail.IsChecked == true;
            ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : Math.Min(_preferredDetailWidth, constraint.Width - ListWidth));
            RowDefinitions[2].Height = stacked && detailVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            SetColumn(_detail, stacked ? 0 : 1);
            SetRow(_detail, stacked ? 2 : 0);
            _detail.Margin = stacked ? new Thickness(0) : new Thickness(10, 0, 0, 0);
            _detail.Visibility = detailVisible ? Visibility.Visible : Visibility.Collapsed;
            _showDetail.Visibility = stacked ? Visibility.Visible : Visibility.Collapsed;
            return base.MeasureOverride(constraint);
        }
    }
}
