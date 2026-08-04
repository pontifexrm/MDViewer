namespace MDViewer;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();

#if WINDOWS
		// Subscribed with a lambda so the platform-specific event args type never
		// has to be named — it lives in a different assembly per target.
		blazorWebView.BlazorWebViewInitialized += (_, e) => AttachPrinter(e.WebView);
#endif
	}

#if WINDOWS
	/// <summary>
	/// Hands the live CoreWebView2 to the printer service. This is the whole of the
	/// PDF/print plumbing — WebView2 already knows how to render the page it is
	/// showing, so nothing else needs to know how to lay out a document.
	/// </summary>
	private void AttachPrinter(Microsoft.UI.Xaml.Controls.WebView2 webView)
	{
		var printer = Handler?.MauiContext?.Services.GetService<WinUI.WebView2DocumentPrinter>()
			?? Application.Current?.Handler?.MauiContext?.Services.GetService<WinUI.WebView2DocumentPrinter>();
		if (printer is null) return;

		if (webView.CoreWebView2 is not null)
		{
			printer.Attach(webView.CoreWebView2);
			return;
		}

		// Not ready yet on some launches — attach as soon as it is.
		webView.CoreWebView2Initialized += (s, _) =>
		{
			if (s.CoreWebView2 is not null) printer.Attach(s.CoreWebView2);
		};
	}
#endif
}
