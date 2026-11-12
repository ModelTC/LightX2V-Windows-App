using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using LightX2V.Windows;
internal static partial class Program
{
    static async Task SettingsDraftChecks()
    {
        var fixture=ReadinessFixture("settings-cancel-verification");var catalog=new ReadinessCatalog{IgnoreCancellation=true};
        using var vm=new MainViewModel(fixture.Preferences,environmentCatalog:catalog,driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[]));
        var original=File.ReadAllBytes(Path.Combine(fixture.Preferences,"preferences.json"));var backend=vm.Setup.Backend;
        var window=new SettingsWindow(vm);window.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var alternate=Path.Combine(fixture.Settings.Workspace,"other-python.exe");File.Copy(fixture.Settings.Python,alternate);
        Children<TextBox>((DependencyObject)window.Content).Single(t=>AutomationProperties.GetAutomationId(t)=="PythonPath").Text=alternate;
        Children<Button>((DependencyObject)window.Content).Single(b=>AutomationProperties.GetAutomationId(b)=="SettingsSave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(catalog.Calls==1,"Saving a changed Python draft starts verification through the existing environment service");
        window.Close();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);catalog.Release.TrySetResult(true);await Task.Delay(50);
        Check(!window.Completed&&vm.Settings.Python==fixture.Settings.Python&&vm.Setup.Backend==backend&&File.ReadAllBytes(Path.Combine(fixture.Preferences,"preferences.json")).SequenceEqual(original),"Closing settings during verification cancels and rejects a late candidate without committing its draft");
        var readyCatalog=new ReadinessCatalog{Hold=false};
        using var savedVm=new MainViewModel(fixture.Preferences,environmentCatalog:readyCatalog,driverPlatform:fixture.Platform,modelDiscovery:new ModelDiscovery([],[]));
        savedVm.Prompt="Preserve the draft while changing Python";
        var saved=new SettingsWindow(savedVm);await Render(saved,640,490,"settings-successful-save");
        Children<TextBox>((DependencyObject)saved.Content).Single(t=>AutomationProperties.GetAutomationId(t)=="PythonPath").Text=alternate;
        Children<Button>((DependencyObject)saved.Content).Single(b=>AutomationProperties.GetAutomationId(b)=="SettingsSave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for(var i=0;i<1000&&!saved.Completed;i++)await Task.Delay(20);
        Check(saved.Completed&&savedVm.Ready&&savedVm.Settings.Python==alternate&&savedVm.Setup.Backend==savedVm.Settings.Backend&&savedVm.Prompt=="Preserve the draft while changing Python","A verified settings save commits once, synchronizes the coordinator and preserves the current creation draft");
    }
}
