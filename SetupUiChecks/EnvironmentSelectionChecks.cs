using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LightX2V.Windows;

internal static partial class Program
{
    static EnvironmentCandidate Valid(string path) => new(path, true, "AMD Radeon 8060S · 2.11.0+rocm7.13.0")
    {
        PythonVersion = "3.12.10",
        TorchVersion = "2.11.0+rocm7.13.0",
        HipVersion = "7.13.0",
        Device = "AMD Radeon 8060S",
        Packages = new Dictionary<string, string>
        {
            ["transformers"] = "5.20.2",
            ["diffusers"] = "0.42.0",
            ["safetensors"] = "0.8.0",
            ["accelerate"] = "1.15.0"
        }
    };

    static async Task EnvironmentSelectionChecks()
    {
        var hybrid = HardwareInfo.FromAdapters([new("AMD Radeon 8060S", "AMD driver"), new("NVIDIA GeForce RTX 4090", "NVIDIA driver")]);
        Check(hybrid.Backend == "cuda" && hybrid.Supports("cuda") && hybrid.Supports("amd_rocm"), "Hybrid graphics retain both adapters and prefer the discrete NVIDIA runtime");
        Check(SetupInstaller.EnvironmentDirectory(root, "cuda") != SetupInstaller.EnvironmentDirectory(root, "amd_rocm"), "CUDA and ROCm installations use separate managed directories");
        var cudaManifest = SetupManifest.Read("cuda");
        Check(!cudaManifest.Packages.Any(p => p.Name.StartsWith("rocm")) && cudaManifest.Packages.Single(p => p.Name == "torch").Version == "2.11.0+cu128", "The pinned CUDA installation contains CUDA PyTorch and no ROCm wheels");
        CudaDriver.ValidateVersion(12080);
        CudaDriver.ValidateVersion(13000);
        try
        {
            CudaDriver.ValidateVersion(12060);
            throw new Exception("Missing driver guard");
        }
        catch (InvalidOperationException)
        {
            Check(true, "An older CUDA driver is rejected before the large environment download");
        }

        var settings = AppSettings.Fresh(Path.Combine(root, "environment-selection", "工作目录 中文"));
        settings.Backend = "amd_rocm";
        var valid = Valid(Path.Combine(settings.Workspace, "fixture-amd", "python.exe"));
        var invalid = new EnvironmentCandidate(Path.Combine(settings.Workspace, "incompatible", "python.exe"), false, "Missing or incompatible dependencies: torch");
        var fake = new FakeCatalog { Found = [invalid, valid] };
        using var model = new EnvironmentSelectionModel(settings, L10n.Current, true, fake,
            new DriverSetupModel("amd_rocm", () => settings.Workspace, L10n.Current, new FakeDriverPlatform()));
        await model.SearchAsync();
        Check(model.Candidates.Count == 2 && model.Candidates[0].Ready && model.Summary.Contains("1"), "Search shows every result with compatible candidates first and the correct count");
        Check(model.Candidates[0].VersionRows.Count == 4 && model.Requirements.Contains("2.11.0+rocm7.13.0") && !model.Requirements.Contains("MPS"), "Dependency cards show the pinned AMD profile and package versions");
        model.SelectCommand.Execute(invalid);
        Check(model.Selected == null, "An incompatible result cannot be selected");
        model.SelectCommand.Execute(valid);
        Check(model.Selected == valid && settings.Python == "", "Selection does not silently save or mutate the caller's settings");
        fake.ProbeResult = valid;
        await model.CheckPythonAsync(valid.Path);
        Check(model.Candidates.Count == 2, "Manually checked paths update the existing result instead of duplicating it");
        fake.InstallResult = valid.Path;
        model.Mirror = false;
        await model.InstallAsync();
        Check(fake.InstallCalls == 1 && !fake.LastMirror && model.Candidates.Count == 2, "Explicit automatic installation uses the chosen official source and rechecks the installed environment");
        model.Mirror = true;
        await model.InstallAsync();
        Check(fake.LastMirror, "The selected Tsinghua source reaches the installer");
        fake.Delayed = true;
        var pending = model.SearchAsync();
        Check(model.Busy, "Search exposes an active state immediately");
        var before = model.Selected;
        model.SelectCommand.Execute(valid);
        Check(model.Selected == before, "Selection is blocked while a check is active");
        await model.CancelAsync();
        await pending;
        Check(!model.Busy && model.Candidates.Count == 2 && model.Error.Length > 0, "Cancellation finishes cleanly and preserves previous search results");
        fake.Delayed = false;
        using (var unsupported = new EnvironmentSelectionModel(settings, L10n.Current, false, fake,
            new DriverSetupModel("amd_rocm", () => settings.Workspace, L10n.Current, new FakeDriverPlatform())))
        {
            await unsupported.InstallAsync();
            Check(unsupported.Error.Length > 0 && fake.InstallCalls == 2, "Automatic installation stays disabled for unsupported hardware");
        }

        await model.SearchAsync();
        var picker = new EnvironmentPickerWindow(settings, L10n.Current, model);
        await Render(picker, 800, 700, "environment-results-zh");
        Check(Children<ComboBox>((DependencyObject)picker.Content).Single().SelectionBoxItem?.ToString() == "AMD ROCm", "Backend picker shows the user-facing label instead of an internal object");
        Check(Children<TextBlock>((DependencyObject)picker.Content).Any(t => t.Text == L10n.Current["Prepare pinned Python and GPU PyTorch dependencies."]), "Localized explanatory text with punctuation renders in full");
        var buttons = Children<Button>((DependencyObject)picker.Content).Where(b => AutomationProperties.GetAutomationId(b) == "SelectEnvironment").ToArray();
        Check(buttons.Length == 2 && buttons.Count(b => b.IsEnabled) == 1, "Only the compatible environment card offers an enabled selection action");
        await Render(picker, 620, 470, "environment-results-small");
        L10n.Current.Use("en");
        await Render(picker, 800, 700, "environment-results-en");
        L10n.Current.Use("zh");
        fake.DelayInstall = true;
        var installing = model.InstallAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Render(picker, 800, 700, "environment-installing");
        Check(model.Installing && !buttons.Any(b => b.IsEnabled), "Installing locks environment selection and exposes progress");
        await model.CancelAsync();
        await installing;
        Check(!model.Busy, "Automatic installation cancellation returns the picker to idle");
        fake.DelayInstall = false;
        model.Backend = "cuda";
        var cuda = valid with
        {
            Path = Path.Combine(settings.Workspace, "fixture-cuda", "python.exe"),
            Device = "NVIDIA GeForce RTX 4090",
            Backend = "cuda",
            HipVersion = "",
            CudaVersion = "12.8",
            TorchVersion = "2.11.0+cu128"
        };
        fake.Found = [cuda];
        fake.ProbeResult = cuda;
        await model.SearchAsync();
        await model.InstallAsync();
        Check(fake.LastBackend == "cuda" && model.Candidates.Single().Backend == "cuda", "The CUDA choice reaches search and automatic installation without reusing ROCm");
        await Render(picker, 800, 700, "environment-cuda-preview");
        model.Backend = "amd_rocm";
        fake.Found = [];
        await model.SearchAsync();
        await Render(picker, 800, 620, "environment-empty");
        Check(model.Summary.Contains("0"), "An empty search has a zero-result state and keeps installation choices available");
        picker.Close();

        var prefs = Path.Combine(root, "environment-selection-preferences");
        Files.Write(Path.Combine(prefs, "preferences.json"), new Preferences { Settings = settings });
        var platform = new FakeDriverPlatform { Computer = HardwareInfo.FromAdapters([new("AMD Radeon fixture GPU", "32")]) };
        using var vm = new MainViewModel(prefs, environmentCatalog: fake, driverPlatform: platform,modelDiscovery:new ModelDiscovery([],[]));
        var wizard = new FirstRunWindow(vm,standalone:true);
        await Render(wizard, 760, 660, "wizard-workspace");
        vm.Setup.Backend = "cuda";
        Check(vm.Setup.EnvironmentSettings().Backend == "cuda" && vm.Settings.Backend != "cuda", "Wizard backend changes remain unsaved drafts");
        vm.Setup.Backend = "amd_rocm";
        await vm.Setup.ScanComputerAsync();
        Check(vm.Setup.ComputerScanned && vm.Setup.Backend == "amd_rocm" && vm.Setup.HardwareLabel.Contains("AMD Radeon fixture GPU"), "The wizard scans the injected AMD fixture, selects ROCm and displays its computer information");
        vm.Setup.Environments.Add(valid);
        vm.Setup.AcceptCandidate(valid);
        vm.Setup.Page = 1;
        await Render(wizard, 760, 660, "wizard-environment");
        await Render(wizard, 620, 470, "wizard-environment-small");
        vm.Setup.UseExisting = false;
        Check(!vm.Setup.UseExisting, "Installation mode changes without losing the selected local environment");
        var bodyScroll = Children<ScrollViewer>((DependencyObject)wizard.Content).First(s => s.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);
        bodyScroll.ScrollToBottom();
        await Render(wizard, 760, 660, "wizard-inline-components");
        bodyScroll.ScrollToTop();
        Check(!Children<Expander>((DependencyObject)wizard.Content).Any(e => AutomationProperties.GetAutomationId(e) == "SetupLogExpander"), "The preparation log is removed from every wizard step");
        Check(!Children<Button>((DependencyObject)wizard.Content).Any(b => AutomationProperties.GetAutomationId(b) == "SetupSearchEnvironment"), "The wizard has no duplicate modal environment chooser entry");
        var radios = Children<RadioButton>((DependencyObject)wizard.Content).Where(r => AutomationProperties.GetAutomationId(r) == "SetupEnvironmentCandidate").ToArray();
        Check(radios.Length >= 1 && radios.Any(r => r.IsEnabled), "Verified local components are selectable inline in step two");
        var scan = Children<Border>((DependencyObject)wizard.Content).Single(b => AutomationProperties.GetAutomationId(b) == "SetupComputerScan");
        var driverCard = Children<DriverSetupCard>((DependencyObject)wizard.Content).Single();
        Check(scan.TransformToAncestor((Visual)wizard.Content).Transform(new Point()).Y < driverCard.TransformToAncestor((Visual)wizard.Content).Transform(new Point()).Y, "Machine scan and backend recommendation appear before the driver and components");
        vm.Setup.Page = 2;
        await Render(wizard, 760, 660, "wizard-models");
        vm.Setup.Page = 3;
        await Render(wizard, 760, 660, "wizard-complete");
        vm.SetLanguage("en");
        vm.Setup.Page = 1;
        await Render(wizard, 760, 660, "wizard-environment-en");
        vm.SetLanguage("zh");
        wizard.Close();
        var general = new SettingsWindow(vm);
        await Render(general, 680, 500, "settings");
        Check(Children<Button>((DependencyObject)general.Content).Any(b => AutomationProperties.GetAutomationId(b) == "SearchEnvironment"), "General settings retains the entry to the dedicated environment picker");
        general.Close();
    }

    sealed class FakeCatalog : IEnvironmentCatalog
    {
        public List<EnvironmentCandidate> Found = [];
        public EnvironmentCandidate? ProbeResult;
        public string InstallResult = "";
        public int InstallCalls, SearchCalls, ProbeCalls, InstalledProbeCalls;
        public bool LastMirror, Delayed, DelayInstall, DelayProbe;
        public string LastBackend = "", SearchBackend = "";
        public List<string>? Events;

        public async Task<List<EnvironmentCandidate>> Search(AppSettings settings, IProgress<SetupProgress> progress, CancellationToken token)
        {
            SearchCalls++;
            SearchBackend = settings.Backend;
            Events?.Add("search:" + SearchBackend);
            if (Delayed) await Task.Delay(Timeout.Infinite, token);
            return Found;
        }

        public async Task<EnvironmentCandidate> Probe(string path, string workspace, CancellationToken token, string backend = "auto")
        {
            ProbeCalls++;
            if (DelayProbe) await Task.Delay(Timeout.Infinite, token);
            return ProbeResult ?? new EnvironmentCandidate(path, false, "Unavailable");
        }

        public async Task<EnvironmentCandidate> ProbeInstalled(string path, string workspace, CancellationToken token, string backend = "auto")
        {
            InstalledProbeCalls++;
            if (DelayProbe) await Task.Delay(Timeout.Infinite, token);
            return ProbeResult ?? new EnvironmentCandidate(path, false, "Unavailable");
        }

        public async Task<string> Install(string workspace, bool mirror, IProgress<SetupProgress> progress, CancellationToken token, string backend = "amd_rocm")
        {
            InstallCalls++;
            LastMirror = mirror;
            LastBackend = backend;
            progress.Report(new("Preparing your environment", "Python / AMD PyTorch", 45));
            if (DelayInstall) await Task.Delay(Timeout.Infinite, token);
            return InstallResult;
        }
    }
}
