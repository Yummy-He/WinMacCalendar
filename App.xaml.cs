global using System;
global using System.Collections.Generic;
global using System.Linq;
using System.IO;
using System.Threading;
using System.Windows;

namespace WinMacCalendar;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "桌面日历发生错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        instance = new Mutex(true, @"Local\WinMacCalendar.Widget", out var first);
        if (!first) { Shutdown(); return; }
        try
        {
            var folder = StorageLocation.GetCurrentFolder();
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch
            {
                folder = StorageLocation.DefaultFolder;
                Directory.CreateDirectory(folder);
            }
            MainWindow = new MainWindow(new CalendarStore(Path.Combine(folder, "calendar.db")));
            MainWindow.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "日历启动失败");
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
