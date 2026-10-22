using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Microsoft.Gaming.XboxGameBar;

namespace ScrcpyBar.Widget
{
    sealed partial class App : Application
    {
        public App()
        {
            this.UnhandledException += (s, e) =>
            {
                try
                {
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "crash.txt"),
                        $"{e.Message}\n{e.Exception}\n{e.Exception?.StackTrace}");
                }
                catch { }
            };
            this.InitializeComponent();
            this.Suspending += OnSuspending;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;
            }

            if (rootFrame.Content == null)
            {
                rootFrame.Navigate(typeof(WidgetPage), null);
            }
            Window.Current.Activate();
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            XboxGameBarWidgetActivatedEventArgs widgetArgs = null;
            if (args.Kind == ActivationKind.Protocol)
            {
                var protocolArgs = args as IProtocolActivatedEventArgs;
                if (protocolArgs != null && protocolArgs.Uri.Scheme.Equals("ms-gamebarwidget", StringComparison.OrdinalIgnoreCase))
                {
                    widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
                }
            }

            if (widgetArgs != null)
            {
                Frame rootFrame = Window.Current.Content as Frame;
                if (rootFrame == null)
                {
                    rootFrame = new Frame();
                    Window.Current.Content = rootFrame;
                }

                rootFrame.Navigate(typeof(WidgetPage), widgetArgs);
                Window.Current.Activate();
            }
            else
            {
                base.OnActivated(args);
            }
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            deferral.Complete();
        }
    }
}
