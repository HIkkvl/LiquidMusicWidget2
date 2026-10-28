namespace LiquidMusic;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        Directory.CreateDirectory(AppPaths.Logs);
        Application.ThreadException += (_, e) => AppPaths.Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppPaths.Log(e.ExceptionObject);
        if (args.Contains("--self-test")) { Environment.ExitCode = SelfTests.Run(); return; }
        if (args.Contains("--assistant-test")) { Environment.ExitCode = DeepSeekTests.RunAsync().GetAwaiter().GetResult(); return; }
        if (args.Contains("--configure-deepseek")) { Environment.ExitCode = DeepSeekTests.ConfigureAsync().GetAwaiter().GetResult(); return; }
        if (args.Contains("--deepseek-test")) { Environment.ExitCode = DeepSeekTests.LiveAsync().GetAwaiter().GetResult(); return; }
        if (args.Contains("--connection-test")) { Environment.ExitCode = BrowserConnectionTests.RunAsync().GetAwaiter().GetResult(); return; }
        if (args.Contains("--browser-test")) { Environment.ExitCode = SelfTests.BrowserAsync().GetAwaiter().GetResult(); return; }
        if (args.Contains("--live-test")) { Environment.ExitCode = SelfTests.LiveAsync().GetAwaiter().GetResult(); return; }
        if (args.Contains("--diagnose-browser"))
        {
            try { SelfTests.DiagnoseAsync().GetAwaiter().GetResult(); } catch(Exception e){AppPaths.Log(e);Environment.ExitCode=1;}return;
        }
        if (args.Contains("--setup-brave")||args.Contains("--setup-browser"))
        {
            try { var connection=BrowserSetup.ConfigureAsync(true,args.Contains("--update-shortcuts")).GetAwaiter().GetResult(); File.WriteAllText(Path.Combine(AppPaths.Logs,"browser-setup.json"),System.Text.Json.JsonSerializer.Serialize(new{ok=true,browser=connection.Name,shortcutsUpdated=connection.ShortcutsUpdated})); }
            catch(Exception error){AppPaths.Log(error);File.WriteAllText(Path.Combine(AppPaths.Logs,"browser-setup.json"),System.Text.Json.JsonSerializer.Serialize(new{ok=false,error=error.Message}));Environment.ExitCode=1;}
            return;
        }
        using var mutex = new Mutex(true, "Local\\LiquidMusicWidget.Native", out var first);
        if (!first)
        {
            try { using var signal = EventWaitHandle.OpenExisting("Local\\LiquidMusicWidget.Native.Show"); signal.Set(); } catch { }
            return;
        }
        ApplicationConfiguration.Initialize();
        if (args.Contains("--ui-test"))
        {
            Application.Run(new MainForm(true, true));return;
        }
        Application.Run(new MainForm(args.Contains("--hidden")));
    }    
}
