using LightX2V.Windows;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
internal static partial class Program
{
    static Button CompletionAction(Window window)=>Children<Button>((DependencyObject)window.Content).Single(b=>AutomationProperties.GetAutomationId(b)=="SetupContinue");
    static void ClickOffscreen(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static async Task CompletionChecks()
    {
        var fixture=ReadinessFixture("settings-completion");
        using var vm=new MainViewModel(fixture.Preferences,environmentCatalog:new ReadinessCatalog{Hold=false},driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[]));
        vm.Prompt="Keep my next image";vm.Tier=2;vm.Ratio="4:3";
        var window=new SettingsWindow(vm);await Render(window,688,600,"settings-completion");
        var button=Children<Button>((DependencyObject)window.Content).Single(b=>AutomationProperties.GetAutomationId(b)=="SettingsSave");
        ClickOffscreen(button);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(window.Completed&&vm.Prompt=="Keep my next image"&&vm.Tier==2&&vm.Ratio=="4:3"&&!vm.IsRunning,"Saving unchanged general settings preserves draft and parameters without sample generation");
    }
}
