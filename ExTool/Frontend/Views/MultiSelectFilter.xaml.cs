using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ExTool.Frontend.Views
{
	// 1 giá trị trong bộ lọc; Swatch là ô màu nhỏ (bộ lọc màu), null thì ẩn
	public class FilterOption : INotifyPropertyChanged
	{
		private bool _isChecked = true;

		public string Text { get; set; } = "";
		public Brush? Swatch { get; set; }
		public Visibility SwatchVisibility => Swatch == null ? Visibility.Collapsed : Visibility.Visible;

		public bool IsChecked
		{
			get => _isChecked;
			set
			{
				if (_isChecked == value) return;
				_isChecked = value;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
			}
		}

		public event PropertyChangedEventHandler? PropertyChanged;
	}

	// Dropdown lọc chọn nhiều giá trị: tick hết = không lọc, bỏ tick hết = không giá trị nào qua lọc
	public partial class MultiSelectFilter : UserControl
	{
		private List<FilterOption> _options = new List<FilterOption>();
		private HashSet<string> _checked = new HashSet<string>();

		public event EventHandler? Changed;

		public MultiSelectFilter()
		{
			InitializeComponent();
		}

		public void SetOptions(IEnumerable<FilterOption> options)
		{
			_options = options.ToList();
			icOptions.ItemsSource = _options;
			Update(false);
		}

		public bool Matches(string value) => _checked.Count == _options.Count || _checked.Contains(value);

		// Tick lại tất cả, không bắn Changed
		public void Reset()
		{
			foreach (FilterOption option in _options) option.IsChecked = true;
			Update(false);
		}

		private void All_Click(object sender, RoutedEventArgs e)
		{
			bool check = _options.Any(o => !o.IsChecked);
			foreach (FilterOption option in _options) option.IsChecked = check;
			Update(true);
		}

		private void Option_Click(object sender, RoutedEventArgs e) => Update(true);

		private void Update(bool raise)
		{
			_checked = new HashSet<string>(_options.Where(o => o.IsChecked).Select(o => o.Text));
			cbAll.IsChecked = _checked.Count == _options.Count;
			tbSummary.Text = _checked.Count == _options.Count ? "Tất cả"
				: _checked.Count == 0 ? "(Không chọn)"
				: _checked.Count <= 2 ? string.Join(", ", _options.Where(o => o.IsChecked).Select(o => o.Text))
				: $"{_checked.Count} mục";
			btnToggle.ToolTip = _checked.Count == _options.Count ? null : string.Join("\n", _options.Where(o => o.IsChecked).Select(o => o.Text));
			if (raise) Changed?.Invoke(this, EventArgs.Empty);
		}

		// Popup đang mở thì nút không nhận chuột, tránh bấm nút để đóng lại mở lại popup ngay
		private void Popup_Opened(object sender, EventArgs e) => btnToggle.IsHitTestVisible = false;

		private void Popup_Closed(object sender, EventArgs e) => btnToggle.IsHitTestVisible = true;
	}
}
