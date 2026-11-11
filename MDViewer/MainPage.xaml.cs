namespace MDViewer;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();

#if WINDOWS
		// Subscribed with a lambda so the platform-specific event args type never
		// has to be named — it lives in a different assembly per target.
		blazorWebView.BlazorWebViewInitialized += (_, e) => OnWebViewReady(e.WebView);
#endif
	}

#if WINDOWS
	/// <summary>
	/// Runs <see cref="Setup"/> once the BlazorWebView has a live CoreWebView2 —
	/// it already does by the time this is called on most launches, but not all.
	/// </summary>
	private void OnWebViewReady(Microsoft.UI.Xaml.Controls.WebView2 webView)
	{
		if (webView.CoreWebView2 is not null)
		{
			Setup(webView.CoreWebView2);
			return;
		}

		webView.CoreWebView2Initialized += (s, _) =>
		{
			if (s.CoreWebView2 is not null) Setup(s.CoreWebView2);
		};
	}

	private void Setup(Microsoft.Web.WebView2.Core.CoreWebView2 core)
	{
		// The editor's right-click Paste (wwwroot/format.js) reads the clipboard with
		// navigator.clipboard, which WebView2 would otherwise ask about in a prompt.
		// Granted only to the app's own pages, which are all this view ever shows.
		core.PermissionRequested += (_, args) =>
		{
			if (args.PermissionKind == Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind.ClipboardRead
				&& Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Host == "0.0.0.0")
				args.State = Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Allow;
		};

		// Hands the live CoreWebView2 to the printer service. This is the whole of the
		// PDF/print plumbing — WebView2 already knows how to render the page it is
		// showing, so nothing else needs to know how to lay out a document.
		var printer = Handler?.MauiContext?.Services.GetService<WinUI.WebView2DocumentPrinter>()
			?? Application.Current?.Handler?.MauiContext?.Services.GetService<WinUI.WebView2DocumentPrinter>();
		printer?.Attach(core);
	}
#endif
}
