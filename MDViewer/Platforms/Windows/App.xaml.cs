using MDViewer.Services;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MDViewer.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	public App()
	{
		AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("appdomain", e.ExceptionObject as Exception);
		this.UnhandledException += (_, e) => Log("xaml", e.Exception);

		this.InitializeComponent();
	}

	private static void Log(string source, Exception? ex)
	{
		try
		{
			var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mdviewer-crash.log");
			File.AppendAllText(path, $"=== {DateTime.Now:s} [{source}] ==={Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
		}
		catch (Exception) { /* nothing useful to do if even logging fails */ }
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	// Fully qualified: Windows.ApplicationModel.Activation also defines this name.
	protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
	{
		CaptureLaunchFile();
		base.OnLaunched(args);
	}

	/// <summary>
	/// Works out which file the app was launched with. Packaged (MSIX) builds get a
	/// File activation from the shell; unpackaged dev runs get a plain command-line
	/// argument instead, so both are handled.
	/// </summary>
	private static void CaptureLaunchFile()
	{
		if (TryActivationFile() is { Length: > 0 } activated)
		{
			PendingFile.Path = activated;
			return;
		}

		if (TryCommandLineFile() is { Length: > 0 } fromArgs)
			PendingFile.Path = fromArgs;
	}

	private static string? TryActivationFile()
	{
		try
		{
			var activated = AppInstance.GetCurrent().GetActivatedEventArgs();
			if (activated?.Kind != ExtendedActivationKind.File) return null;
			if (activated.Data is not IFileActivatedEventArgs fileArgs) return null;

			return fileArgs.Files.OfType<IStorageFile>().FirstOrDefault()?.Path;
		}
		catch (Exception)
		{
			// GetActivatedEventArgs throws when running unpackaged — fall through to argv.
			return null;
		}
	}

	private static string? TryCommandLineFile()
	{
		var args = Environment.GetCommandLineArgs();
		return args.Length > 1 && File.Exists(args[1]) ? args[1] : null;
	}
}
