namespace ExTool.Backend.Models
{
	// Cài đặt lệnh DLB, giữ lại giữa các lần chạy trong phiên CAD
	public class DimLabelSettings
	{
		public string DimStyle { get; set; } = "";
		public string Layer { get; set; } = "";
		public double Scale { get; set; }
		public double TextOffset { get; set; } = 200;
		// DIMRND, số nguyên > 0
		public int Round { get; set; } = 1;
	}
}
