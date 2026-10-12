using System.Collections;
using System.Windows.Controls;

namespace ExTool.Frontend.Views
{
	// Chọn nhiều dòng 1 lần (1 SelectionChanged), tránh Add từng dòng vào SelectedItems
	public class PipeListView : ListView
	{
		public void SelectRows(IEnumerable rows) => SetSelectedItems(rows);
	}
}
