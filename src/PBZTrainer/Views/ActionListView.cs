using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using PBZTrainer.Models;
using PBZTrainer.Services;

namespace PBZTrainer.Views
{
    /// <summary>一个“列表 + 主操作/次操作”页面的配置。</summary>
    public sealed class ActionSpec
    {
        public string Title;
        public string Subtitle;
        public string LoadType;
        public string PrimaryLabel = "执行";
        public Func<BridgeClient, CatalogRow, double, Task<JsonObject>> Primary;
        public string SecondaryLabel;
        public Func<BridgeClient, CatalogRow, double, Task<JsonObject>> Secondary;
        public bool SecondaryWithoutSelection;
        public bool ShowValue;
        public string ValueLabel = "数值";
        public string ValueDefault = "1";
        public string StateHeader = "状态";
        /// <summary>空列表时的提示（例如某些目录需要先进场景）。</summary>
        public string EmptyHint = "目录为空：请先进入可操作场景后点「刷新」";
        /// <summary>可选：自定义状态列渲染（默认读 completed / state / owned）。</summary>
        public Func<JsonObject, string> StateReader;
        /// <summary>可选：不用游戏目录，直接给一份本地数据（例如内置属性表）。</summary>
        public Func<List<CatalogRow>> LocalRows;
        /// <summary>目录为空时自动重试几次（例如首领招式需要锁定首领）。</summary>
        public int RetryWhenEmpty;
    }

    /// <summary>通用动作页：左侧目录列表，右侧详情 + 操作按钮 + 日志。</summary>
    public sealed class ActionListView : Grid, ISearchablePage
    {
        private readonly BridgeClient _bridge;
        private readonly ActionSpec _spec;
        private readonly List<CatalogRow> _all = new();
        private readonly ListView _list = new();
        private readonly TextBox _search = Ui.Input("", 200);
        TextBox ISearchablePage.SearchBox => _search;
        private readonly TextBlock _count = Ui.Sub("尚未读取目录");
        private readonly TextBlock _detailTitle = Ui.Text("选择左侧条目", 16, "Text", true);
        private readonly TextBlock _detailMeta = Ui.Sub("");
        private readonly TextBlock _stateText = Ui.Text("", 12.5, "Jade");
        private readonly TextBox _desc = new()
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Ui.Brush("SubText"),
            FontSize = 11.5,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 96
        };
        private readonly TextBox _log = Ui.LogBox(112);
        private readonly TextBox _value;
        private readonly TextBlock _valueError = Ui.Text(NumericInput.ErrorMessage, 11.5, "ControlFocus");
        private readonly Button _primary;
        private readonly Button _secondary;
        private bool _running;
        private int _retries;

        public ActionListView(BridgeClient bridge, ActionSpec spec)
        {
            _bridge = bridge;
            _spec = spec;
            _value = Ui.Input(spec.ValueDefault, 96);

            Margin = new Thickness(22, 4, 22, 0);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 头部
            var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(Ui.Col(2, Ui.Text(spec.Title, 17, "Text", true), Ui.Sub(spec.Subtitle)));

            var right = Ui.Row(8);
            right.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(_count);
            _search.Margin = new Thickness(8, 0, 0, 0);
            _search.Background = Ui.Brush("Chip");
            right.Children.Add(_search);
            var refresh = Ui.Button("刷新", () => _ = LoadAsync(), "AccentBtn", 28, 70, 11.5);
            refresh.Margin = new Thickness(8, 0, 0, 0);
            right.Children.Add(refresh);
            Grid.SetColumn(right, 2);
            header.Children.Add(right);
            Children.Add(header);

            // 主体
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(430) });

            var listPanel = Ui.Panel(null, new Thickness(0));
            var gv = new GridView();
            gv.Columns.Add(new GridViewColumn { Header = "名称", DisplayMemberBinding = new Binding("Name"), Width = 220 });
            gv.Columns.Add(new GridViewColumn { Header = "分类", DisplayMemberBinding = new Binding("Category"), Width = 110 });
            gv.Columns.Add(new GridViewColumn { Header = spec.StateHeader, DisplayMemberBinding = new Binding("StateColumn"), Width = 110 });
            _list.View = gv;
            _list.Background = Ui.Brush("Card");
            _list.Foreground = Ui.Brush("Text");
            _list.BorderThickness = new Thickness(0);
            _list.FontSize = 12;
            _list.Margin = new Thickness(10, 8, 10, 10);
            var hs = new Style(typeof(GridViewColumnHeader));
            hs.Setters.Add(new Setter(Control.BackgroundProperty, Ui.Brush("Chip")));
            hs.Setters.Add(new Setter(Control.ForegroundProperty, Ui.Brush("SubText")));
            hs.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            hs.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
            hs.Setters.Add(new Setter(FrameworkElement.HeightProperty, 30.0));
            _list.Resources[typeof(GridViewColumnHeader)] = hs;

            _list.ItemContainerStyle = Ui.Style("GridViewRow");
            _list.SelectionChanged += (_, __) => ShowDetail(_list.SelectedItem as CatalogRow);
            listPanel.Child = _list;
            body.Children.Add(listPanel);

            // 详情
            var stack = new StackPanel();
            stack.Children.Add(_detailTitle);
            stack.Children.Add(_detailMeta);
            stack.Children.Add(new Border { Height = 1, Background = Ui.Brush("Divider"), Margin = new Thickness(0, 8, 0, 8) });
            stack.Children.Add(_desc);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            if (spec.ShowValue)
            {
                actions.Children.Add(Ui.Sub(spec.ValueLabel));
                _value.Margin = new Thickness(6, 0, 10, 0);
                actions.Children.Add(_value);
            }
            _primary = Ui.Button(spec.PrimaryLabel, () => _ = RunAsync(true), "AccentBtn", 30, 108, 12);
            _primary.IsEnabled = false;
            actions.Children.Add(_primary);
            if (!string.IsNullOrEmpty(spec.SecondaryLabel))
            {
                _secondary = Ui.Button(spec.SecondaryLabel, () => _ = RunAsync(false), "Ghost", 30, 108, 12);
                _secondary.Margin = new Thickness(8, 0, 0, 0);
                _secondary.IsEnabled = spec.SecondaryWithoutSelection;
                actions.Children.Add(_secondary);
            }
            stack.Children.Add(actions);
            if (spec.ShowValue)
            {
                stack.Children.Add(_valueError);
                _value.TextChanged += (_, __) => UpdateActions();
            }
            UpdateActions();
            stack.Children.Add(_stateText);
            stack.Children.Add(new Border { Height = 1, Background = Ui.Brush("Divider"), Margin = new Thickness(0, 10, 0, 8) });
            stack.Children.Add(Ui.Sub("操作记录"));
            stack.Children.Add(_log);

            var detail = Ui.Panel(new ScrollViewer
            {
                Content = stack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });
            detail.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(detail, 1);
            body.Children.Add(detail);

            Grid.SetRow(body, 1);
            Children.Add(body);

            _search.TextChanged += (_, __) => ApplyFilter();
            Loaded += (_, __) => { if (_all.Count == 0) _ = LoadAsync(); };
        }

        private void ApplyFilter(bool refreshed = false)
        {
            string q = (_search.Text ?? "").Trim();
            var rows = _all.Where(r => q.Length == 0
                || (r.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.Id?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.Category?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

            CatalogSelection.BindRows(_list, rows, refreshed);
            _count.Text = $"{rows.Count} / {_all.Count} 项";
            Ui.Log(_log, _all.Count == 0 ? _spec.EmptyHint : $"已读取 {_all.Count} 项");
        }

        public async Task LoadAsync()
        {
            if (!_bridge.IsConnected) { _count.Text = "请先连接游戏"; return; }
            _count.Text = "正在读取…";
            try
            {
                if (_spec.LocalRows != null)
                {
                    _all.Clear();
                    _all.AddRange(_spec.LocalRows());
                    ApplyFilter(refreshed: true);
                    return;
                }

                var r = await _bridge.CatalogAsync(_spec.LoadType);
                if (!r.Flag("ok"))
                {
                    _count.Text = r.Str("message") ?? "目录读取失败";
                    return;
                }
                _all.Clear();
                if (r["items"] is JsonArray items)
                {
                    foreach (var node in items.OfType<JsonObject>())
                    {
                        var row = new CatalogRow
                        {
                            Id = node.Str("id") ?? "",
                            Name = node.Str("name") ?? "",
                            Category = node.Str("category") ?? "",
                            Desc = node.Str("description") ?? "",
                            Raw = node
                        };
                        row.StateColumn = (_spec.StateReader ?? ReadState)(node);
                        _all.Add(row);
                    }
                }
                ApplyFilter(refreshed: true);

                if (_all.Count == 0 && _spec.RetryWhenEmpty > _retries)
                {
                    _retries++;
                    _count.Text = $"目录为空，正在重试（{_retries}/{_spec.RetryWhenEmpty}）…";
                    await Task.Delay(2500);
                    await LoadAsync();
                }
                else
                {
                    _retries = 0;
                }
            }
            catch (Exception ex)
            {
                _count.Text = "目录读取失败";
                Ui.Log(_log, "读取失败：" + ex.Message);
            }
        }

        private static string ReadState(JsonObject node)
        {
            if (node == null) return "";
            if (node["completed"] != null) return node.Flag("completed") ? "已完成" : "未完成";
            if (node["state"] != null)
            {
                int st = (int)node["state"].Num(-1);
                return st switch
                {
                    0 => "未接取",
                    1 => "进行中",
                    2 => "已完成",
                    3 => "已失败",
                    _ => "状态 " + st
                };
            }
            if (node["owned"] != null) return node["owned"].Num() > 0 ? "已拥有" : "";
            return "";
        }

        private void ShowDetail(CatalogRow row)
        {
            _detailTitle.Text = row?.Name ?? "选择左侧条目";
            _detailMeta.Text = string.IsNullOrWhiteSpace(row?.Category) ? row?.Id ?? "" : $"{row.Category}  ·  {row.Id}";
            _desc.Text = row == null ? "" : string.IsNullOrWhiteSpace(row.Desc) ? "（无说明）" : row.Desc;
            _stateText.Text = row?.StateColumn ?? "";
            UpdateActions();
        }

        private void UpdateActions()
        {
            bool invalid = _spec.ShowValue && !NumericInput.TryParse(_value.Text, out _);
            bool selected = _list.SelectedItem != null;
            _valueError.Visibility = invalid ? Visibility.Visible : Visibility.Collapsed;
            _primary.IsEnabled = !_running && selected && !invalid;
            if (_secondary != null)
                _secondary.IsEnabled = !_running && (_spec.SecondaryWithoutSelection || selected) && !invalid;
        }

        private async Task RunAsync(bool primary)
        {
            var row = _list.SelectedItem as CatalogRow;
            if (_running || (row == null && (primary || !_spec.SecondaryWithoutSelection))) return;
            double value = 0;
            if (_spec.ShowValue && !NumericInput.TryParse(_value.Text, out value))
            {
                UpdateActions();
                return;
            }
            if (!_bridge.IsConnected) { Ui.Log(_log, "请先连接游戏"); return; }
            var handler = primary ? _spec.Primary : _spec.Secondary;
            if (handler == null) return;

            string label = primary ? _spec.PrimaryLabel : _spec.SecondaryLabel;
            string name = row?.Name ?? "";

            _running = true;
            UpdateActions();
            try
            {
                Ui.Log(_log, $"{label}：{name}");
                var r = await handler(_bridge, row, value);
                if (r == null) { Ui.Log(_log, "未收到结果"); return; }

                string msg = r.Str("message");
                Ui.Log(_log, (r.Flag("ok") ? "✔ " : "✘ ") + (string.IsNullOrEmpty(msg) ? (r.Flag("ok") ? "已执行" : "失败") : msg));
            }
            catch (Exception ex)
            {
                Ui.Log(_log, "异常：" + ex.Message);
            }
            finally
            {
                await LoadAsync();
                _running = false;
                UpdateActions();
            }
        }
    }
}
