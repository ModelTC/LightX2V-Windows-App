using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace LightX2V.Windows;

public sealed partial class MainViewModel
{
    internal void ShowModelGuide(ModelCard card) => CreateModelGuide(card).ShowDialog();

    public Window CreateModelGuide(ModelCard card)
    {
        var window = new Window
        {
            Title = T["Download guide"], DataContext = this,
            Owner = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(window=>window.IsVisible),
            Width = 620, Height = 510, MinWidth = 480, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.White
        };
        SetupUi.Resources(window);
        var body = new StackPanel { Margin = new Thickness(20) };
        var notice = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        void Notice(string message) { notice.Text = message; notice.Visibility = Visibility.Visible; }
        var title = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(ModelCard.Title)) { Source = card });
        body.Children.Add(title);
        body.Children.Add(new TextBlock { Text = card.Repository, FontSize = 11, Foreground = SetupUi.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        var help = SetupUi.Text("Click automatic download. LightX2V will save and check the files for you; no commands are needed.", 12, false, true);
        help.Margin = new Thickness(0, 12, 0, 12);
        body.Children.Add(help);

        var actions = new WrapPanel();
        var automatic = SetupUi.Button(window, "Download automatically", true, "GuideAutomaticDownload");
        automatic.Command = card.DownloadCommand;
        automatic.SetBinding(ContentControl.ContentProperty, new Binding(nameof(ModelCard.DownloadLabel)) { Source = card });
        actions.Children.Add(automatic);
        var repair = SetupUi.Button(window, "Check and repair", false, "GuideCheckRepair");
        repair.Command = card.RepairCommand;
        repair.SetBinding(FrameworkElement.ToolTipProperty, "T[Verify model files and download missing or damaged files.]");
        repair.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(repair);
        body.Children.Add(actions);

        var state = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = SetupUi.Muted, Margin = new Thickness(0, 10, 0, 0) };
        state.SetBinding(TextBlock.TextProperty, new Binding(nameof(ModelCard.Status)) { Source = card });
        body.Children.Add(state);
        var estimate = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = SetupUi.Muted, Margin = new Thickness(0, 6, 0, 0) };
        estimate.SetBinding(TextBlock.TextProperty, new Binding(nameof(ModelCard.DownloadEta)) { Source = card });
        estimate.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(ModelCard.DownloadEta))
        {
            Source = card, Converter = (IValueConverter)Application.Current.FindResource("NonEmpty")
        });
        System.Windows.Automation.AutomationProperties.SetAutomationId(estimate, "ModelGuideDownloadEta");
        body.Children.Add(estimate);

        var modelScope = UseModelScope || UseAutomaticDownloadSource && card.ActiveDownloadSource != "huggingFace";
        var address = ModelPage.Address(card.Repository, modelScope);
        var path = (card.Path.Length == 0 ? DefaultModelPath(card.Repository) : card.Path).Replace("'", "''");
        var command = modelScope
            ? $"modelscope download --model {card.Repository} --local_dir '{path}'"
            : $"hf download {card.Repository} --local-dir '{path}'";
        var manual = new StackPanel();
        var commandBox = new TextBox
        {
            Text = command, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 70, Padding = new Thickness(10), FontFamily = new("Consolas"), Margin = new Thickness(0, 10, 0, 0)
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(commandBox, "ModelDownloadCommand");
        manual.Children.Add(commandBox);
        var copy = SetupUi.Button(window, "Copy command", false, "CopyModelDownloadCommand");
        copy.Margin = new Thickness(0, 10, 0, 0);
        copy.HorizontalAlignment = HorizontalAlignment.Left;
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(command); Notice(T["Copied"]); }
            catch (Exception error) { Notice(error.Message); }
        };
        manual.Children.Add(copy);
        var advanced = new Expander { Style = (Style)window.FindResource("SetupLog"), Content = manual, Margin = new Thickness(0, 16, 0, 12) };
        advanced.SetBinding(HeaderedContentControl.HeaderProperty, "T[Manual download commands (optional)]");
        System.Windows.Automation.AutomationProperties.SetAutomationId(advanced, "ManualModelDownload");
        body.Children.Add(advanced);

        var link = new TextBox { Text = address.AbsoluteUri, IsReadOnly = true, Padding = new Thickness(8), TextWrapping = TextWrapping.Wrap };
        System.Windows.Automation.AutomationProperties.SetAutomationId(link, "ModelPageAddress");
        body.Children.Add(link);
        System.Windows.Automation.AutomationProperties.SetAutomationId(notice, "ModelPageStatus");
        body.Children.Add(notice);
        var links = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var copyLink = SetupUi.Button(window, "Copy link", false);
        copyLink.Margin = new Thickness(0, 0, 8, 0);
        copyLink.Click += (_, _) =>
        {
            try { Clipboard.SetText(address.AbsoluteUri); Notice(T["Link copied"]); }
            catch (Exception error) { Notice(error.Message); }
        };
        links.Children.Add(copyLink);
        var open = SetupUi.Button(window, "Open model page", false, "OpenModelPage");
        open.Click += (_, _) =>
        {
            try { WindowsShell.OpenWebPage(address); Notice(T["Requested browser to open model page"]); }
            catch (Exception error) { Notice(ShellFailure(error, "Unable to open model page")); }
        };
        links.Children.Add(open);
        body.Children.Add(links);
        var root = new Grid();
        root.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
        window.Content = root;
        return window;
    }
}
