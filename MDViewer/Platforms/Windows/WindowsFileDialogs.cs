using MDViewer.Services;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MDViewer.WinUI;

/// <summary>
/// WinRT file pickers. Both need the app window handle wired in before use —
/// a desktop app has no implicit picker owner the way a UWP app does.
/// </summary>
public sealed class WindowsFileDialogs : IFileDialogs
{
	private static IntPtr WindowHandle()
	{
		var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
		var platformWindow = window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
		return platformWindow is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(platformWindow);
	}

	public async Task<string?> PickMarkdownAsync()
	{
		var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
		foreach (var ext in new[] { ".md", ".markdown", ".mdown", ".mkd", ".mdtext", ".txt" })
			picker.FileTypeFilter.Add(ext);

		WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle());

		var file = await picker.PickSingleFileAsync();
		return file?.Path;
	}

	public async Task<string?> SaveAsAsync(string suggestedName, string extension, string extensionLabel)
	{
		// No start-folder override: the picker reopens wherever it was last used,
		// which is what you want when saving a variant of the file you just opened.
		var picker = new FileSavePicker
		{
			SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
			SuggestedFileName = suggestedName,
		};
		picker.FileTypeChoices.Add(extensionLabel, new List<string> { extension });

		WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle());

		var file = await picker.PickSaveFileAsync();
		if (file is null) return null;

		// The picker creates a zero-byte placeholder; release its lock so the caller
		// can write with plain System.IO (this app is full-trust, so that's allowed).
		await CachedFileManager.CompleteUpdatesAsync(file);
		return file.Path;
	}
}
