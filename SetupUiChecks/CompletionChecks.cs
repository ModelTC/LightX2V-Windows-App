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
        var settings=AppSettings.Fresh(Path.Combine(root,"completion-isolated"));var prefs=Path.Combine(root,"completion-preferences");
        Files.Write(Path.Combine(settings.Workspace,"workspace.json"),new WorkspaceState{Generations=[new Generation{Status="completed",Directory=Path.Combine(settings.Workspace,"outputs","existing-creation"),CreationId="existing-creation",Request=new(){Prompt="Existing creation"}}]});
        Files.Write(Path.Combine(prefs,"preferences.json"),new Preferences{Settings=settings});var downloads=new FakeModelDownloader();
        using(var vm=new MainViewModel(prefs,downloads,new FakeCatalog(),new FakeDriverPlatform()))
        {
            vm.Prompt="我自己的第一个创作想法";vm.Tier=2;vm.Ratio="4:3";vm.RandomSeed=false;vm.Seed="123456";vm.InputImages.Add(new InputImage{Path=Path.Combine(root,"draft-reference.png"),Name="Draft reference"});
            vm.Setup.Page=3;var wizard=new FirstRunWindow(vm);var closed=false;wizard.Closed+=(_,_)=>closed=true;
            await Render(wizard,760,660,"setup-complete-zh");await Render(wizard,620,470,"setup-complete-small");
            Check(CompletionAction(wizard).IsEnabled&&CompletionAction(wizard).Content.ToString()==vm.T["Start creating"],"The completion page immediately offers Start creating without generating a test image");
            Check(Children<TextBlock>((DependencyObject)wizard.Content).Any(t=>t.Text==vm.T["Congratulations! Your local environment and model files are ready."]),"The final page congratulates the user and confirms environment and model readiness");
            var completion=Children<StackPanel>((DependencyObject)wizard.Content).Single(p=>AutomationProperties.GetAutomationId(p)=="SetupCompletion");
            Check(!Children<FrameworkElement>(completion).Any(e=>(BindingOperations.GetBinding(e,TextBlock.TextProperty)?.Path?.Path??"").Contains("ResourceWarning")),"The completion card has no GPU budget or inference parameter display");
            Check(!Children<TextBlock>((DependencyObject)wizard.Content).Any(t=>t.Text.Contains("512")||t.Text==vm.T["Generate the first image"]),"The completion page does not request a fixed-prompt 512 image test");
            Check(vm.Prompt=="我自己的第一个创作想法"&&vm.Recent.Count==1&&!vm.IsRunning&&downloads.Requests.Count==0,"Opening and rendering completion does not alter the draft, creation history or start a download or inference");
            vm.SetLanguage("en");await Render(wizard,760,660,"setup-complete-en");Check(vm.Setup.PageTitle=="Setup complete"&&CompletionAction(wizard).Content.ToString()=="Start creating","Completion step and action follow the selected language");
            vm.SetLanguage("zh");ClickOffscreen(CompletionAction(wizard));await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(closed&&!vm.IsRunning&&vm.Prompt=="我自己的第一个创作想法"&&vm.Tier==2&&vm.Ratio=="4:3"&&!vm.RandomSeed&&vm.Seed=="123456"&&vm.InputImages.Count==1&&vm.Recent.Count==1,"Start creating closes the wizard and preserves the user's prompt, image attachment, parameters and history");
            Check(!Directory.Exists(Path.Combine(settings.Workspace,"outputs"))&&downloads.Requests.Count==0,"Finishing setup writes no generated image, fixed prompt job or model download");
        }
    }
}
