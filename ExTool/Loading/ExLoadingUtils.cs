using System;
using LoadingLib;

namespace ExTool.Loading
{
	// Bọc LoadingLib của TakaCAD: Start(ms ước lượng) -> SetMessageLoading/ThrowIfCancelled trong vòng lặp -> Close trong finally
	public static class ExLoadingUtils
	{
		public static void SetMessageLoading(string rawMessage, string jpMessage = "")
		{
			LoadingUtils.SetMessageLoading(rawMessage, jpMessage);
		}

		public static void Start(int ms = 3000)
		{
			LoadingUtils.Start(ms);
		}

		public static void Close(bool isDelay = false, int msDelay = 1000)
		{
			LoadingUtils.Close(isDelay, msDelay);
		}

		public static void SetShowCancelButton(bool isShow)
		{
			LoadingUtils.SetShowCancelButton(isShow);
		}

		public static void ThrowIfCancelled(string customMessage = "")
		{
			if (LoadingUtils.IsLoading && LoadingUtils.IsCancelled)
			{
				var message = string.IsNullOrEmpty(customMessage) ? "Quá trình đã bị hủy bởi người dùng." : customMessage;
				throw new OperationCanceledException(message);
			}
		}

		public static void SetProgressDirect(double percent)
		{
			LoadingUtils.PauseProgress();
			LoadingUtils.SetProgressDirect(percent);
		}

		// Kiểm tra nút Hủy rồi cập nhật "step done/total..." trong khoảng % [from, to]
		public static void ReportProgress(string step, int done, int total, double from = 0, double to = 100)
		{
			ThrowIfCancelled();
			SetMessageLoading($"{step} {done}/{total}...");
			SetProgressDirect(Math.Round(from + (to - from) * done / Math.Max(total, 1), 2));
		}
	}
}
