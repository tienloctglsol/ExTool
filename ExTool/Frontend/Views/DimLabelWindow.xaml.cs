using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ExTool.Backend.Models;

namespace ExTool.Frontend.Views
{
	// Cửa sổ cài đặt lệnh DLB: dim style, layer, tỉ lệ (bước 5), khoảng cách text tới ống; OK thì ghi lại vào settings
	public partial class DimLabelWindow : Window
	{
		private const double ScaleStep = 5;

		private readonly DimLabelSettings _settings;

		public DimLabelWindow(DimLabelSettings settings, List<string> dimStyles, List<string> layers)
		{
			InitializeComponent();
			_settings = settings;
			Select(cbDimStyle, dimStyles, settings.DimStyle);
			Select(cbLayer, layers, settings.Layer);
			tbScale.Text = Format(settings.Scale);
			tbOffset.Text = Format(settings.TextOffset);
			tbRound.Text = settings.Round.ToString(CultureInfo.InvariantCulture);
		}

		private static void Select(ComboBox comboBox, List<string> items, string selected)
		{
			comboBox.ItemsSource = items;
			comboBox.SelectedIndex = items.Count == 0 ? -1 : System.Math.Max(0, items.IndexOf(selected));
		}

		private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

		private static bool TryParse(string text, out double value) =>
			double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

		private void ScaleUp_Click(object sender, RoutedEventArgs e) => StepScale(1);

		private void ScaleDown_Click(object sender, RoutedEventArgs e) => StepScale(-1);

		private void Scale_PreviewKeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Up) StepScale(1);
			else if (e.Key == Key.Down) StepScale(-1);
			else return;
			e.Handled = true;
		}

		// Không cho tỉ lệ xuống tới 0
		private void StepScale(int direction)
		{
			if (!TryParse(tbScale.Text, out double value)) value = 0;
			double next = value + direction * ScaleStep;
			if (next <= 0) return;
			tbScale.Text = Format(next);
			tbScale.CaretIndex = tbScale.Text.Length;
		}

		private void Ok_Click(object sender, RoutedEventArgs e)
		{
			if (cbDimStyle.SelectedItem == null || cbLayer.SelectedItem == null)
			{
				MessageBox.Show(this, "Chưa chọn dim style hoặc layer.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
			if (!TryParse(tbScale.Text, out double scale) || scale <= 0)
			{
				MessageBox.Show(this, "Tỉ lệ phải là số lớn hơn 0.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
				tbScale.Focus();
				return;
			}
			if (!TryParse(tbOffset.Text, out double offset) || offset < 0)
			{
				MessageBox.Show(this, "Khoảng cách text tới ống phải là số không âm.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
				tbOffset.Focus();
				return;
			}
			if (!int.TryParse(tbRound.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int round) || round <= 0)
			{
				MessageBox.Show(this, "Hệ số làm tròn phải là số nguyên lớn hơn 0.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
				tbRound.Focus();
				return;
			}

			_settings.DimStyle = (string)cbDimStyle.SelectedItem;
			_settings.Layer = (string)cbLayer.SelectedItem;
			_settings.Scale = scale;
			_settings.TextOffset = offset;
			_settings.Round = round;
			DialogResult = true;
		}
	}
}
