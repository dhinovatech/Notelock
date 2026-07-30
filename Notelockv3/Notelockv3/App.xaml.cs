using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using System;
using System.Collections.Generic;

namespace Notelockv3
{
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var fileList = new List<string>();

            try
            {
                var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
                if (activatedArgs.Kind == ExtendedActivationKind.File)
                {
                    if (activatedArgs.Data is IFileActivatedEventArgs fileArgs && fileArgs.Files != null)
                    {
                        foreach (var file in fileArgs.Files)
                        {
                            if (!string.IsNullOrEmpty(file.Path))
                            {
                                fileList.Add(file.Path);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback
            }

            if (fileList.Count == 0)
            {
                var cmdArgs = Environment.GetCommandLineArgs();
                if (cmdArgs.Length > 1)
                {
                    fileList.Add(cmdArgs[1]);
                }
            }

            _window = new MainWindow(fileList.ToArray());
            _window.Activate();
        }
    }
}
