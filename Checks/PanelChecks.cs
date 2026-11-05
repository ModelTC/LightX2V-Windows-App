using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LightX2V.Windows;

internal static partial class Program
{
    static async Task PanelRun(string fixture)
    {
        var settings=AppSettings.Detect();settings.Workspace=Path.Combine(root,"workspace");
        var state=Files.Read<WorkspaceState>(fixture)!;
        state.Generations=state.Generations.Where(g=>g.Completed).ToList();
        Files.Write(Path.Combine(settings.Workspace,"workspace.json"),state);
        var prefs=Path.Combine(root,"preferences");
        Files.Write(Path.Combine(prefs,"preferences.json"),new Preferences{Settings=settings});
        using(var vm=new MainViewModel(prefs))
        {
            var window=new MainWindow(vm);window.Show();await Until(()=>vm.Ready&&!vm.IsChecking,60);
            vm.NewCreation();vm.Tier=0;vm.Ratio="16:9";vm.Prompt="Panel state draft";
            var model=(Expander)window.FindName("ModelSection");
            var parameters=(Expander)window.FindName("ParametersSection");
            var modelContent=(FrameworkElement)window.FindName("ModelSectionContent");
            var parametersContent=(FrameworkElement)window.FindName("ParametersSectionContent");
            Assert(model.IsExpanded&&parameters.IsExpanded,"Both sidebar sections initially expanded");
            await Task.Delay(150);Shot(window,"expanded");
            ToggleHeader(model);await Until(()=>!vm.ModelExpanded&&!modelContent.IsVisible);
            Assert(parameters.IsExpanded&&parametersContent.IsVisible,"Model header collapses only model content");
            Shot(window,"model-collapsed");
            ToggleHeader(parameters);await Until(()=>!vm.ParametersExpanded&&!parametersContent.IsVisible);
            Assert(!model.IsExpanded&&!parameters.IsExpanded,"Both sections fold to title rows");
            window.WindowState=WindowState.Maximized;await Task.Delay(250);
            Fit(window,WorkArea(window),"collapsed-maximized");Shot(window,"collapsed");
            ToggleHeader(model);await Until(()=>model.IsExpanded&&modelContent.IsVisible);
            Assert(!parameters.IsExpanded,"Model section expands independently");
            ToggleHeader(parameters);await Until(()=>parameters.IsExpanded&&parametersContent.IsVisible);
            Assert(vm.Tier==0&&vm.Ratio=="16:9"&&vm.Prompt=="Panel state draft","Section toggles preserve parameters and prompt");
            window.Width=1100;window.Height=720;window.WindowState=WindowState.Normal;await Task.Delay(200);
            Fit(window,WorkArea(window),"expanded-minimum");Shot(window,"expanded-minimum");
            ToggleHeader(model);ToggleHeader(parameters);await Until(()=>!vm.ModelExpanded&&!vm.ParametersExpanded);
            vm.Prompt="";if(vm.Recent.Count>0)vm.Selected=vm.Recent.First();await Task.Delay(150);Shot(window,"collapsed-conversation");
            window.Close();
        }
        using(var restored=new MainViewModel(prefs))
        {
            Assert(!restored.ModelExpanded&&!restored.ParametersExpanded,"Section choices restore on next launch");
            var window=new MainWindow(restored);window.Show();await Until(()=>restored.Ready&&!restored.IsChecking,60);
            var parameters=(Expander)window.FindName("ParametersSection");
            var peer=new ExpanderAutomationPeer(parameters);
            ((IExpandCollapseProvider)peer.GetPattern(PatternInterface.ExpandCollapse)!).Expand();
            await Until(()=>restored.ParametersExpanded);
            Assert(!restored.ModelExpanded,"Accessible expansion leaves other section folded");
            restored.NewCreation();await Task.Delay(150);Shot(window,"restored");
            window.Close();
        }
    }
    static void ToggleHeader(Expander expander)
    {
        var header=(ToggleButton)expander.Template.FindName("SectionHeader",expander);
        var peer=new ToggleButtonAutomationPeer(header);
        ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!).Toggle();
    }
}
