using LightX2V.Windows;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

internal static partial class Program
{
    static async Task ModelValidationChecks()
    {
        var fixture = ReadinessFixture("model-completeness");
        var model = fixture.Settings.Model;
        Check(ModelValidation.Ready(model, "turbo"), "A complete supported model fixture passes lightweight startup validation");
        foreach (var relative in new[] { "processor/tokenizer.json", "processor/processor_config.json", "scheduler/scheduler_config.json", "text_encoder/config.json", "transformer/config.json", "vae/config.json" })
        {
            var path = Path.Combine(model, relative);
            var bytes = File.ReadAllBytes(path);
            File.Delete(path);
            Check(!ModelValidation.Ready(model, "turbo"), "A missing required model configuration or tokenizer prevents readiness: " + relative);
            File.WriteAllBytes(path, bytes);
        }
        var tokenizerPath = Path.Combine(model, "processor", "tokenizer.json");
        var tokenizer = File.ReadAllBytes(tokenizerPath);
        File.WriteAllText(tokenizerPath, "{\"version\":\"9.0\"}");
        File.SetLastWriteTimeUtc(tokenizerPath, DateTime.UtcNow.AddSeconds(5));
        Check(!ModelValidation.Ready(model, "turbo"), "Changing a tokenizer invalidates the cached lightweight format check");
        File.WriteAllBytes(tokenizerPath, tokenizer);
        var pipelinePath = Path.Combine(model, "model_index.json");
        var pipeline = File.ReadAllBytes(pipelinePath);
        Files.Write(pipelinePath, new { _class_name = "AnotherPipeline", sample_sigmas = ModelCatalog.Get("turbo").SampleSigmas });
        Check(!ModelValidation.Ready(model, "turbo"), "A different pipeline cannot be accepted as the supported Turbo model");
        Files.Write(pipelinePath, new { _class_name = "QwenImage21Pipeline", sample_sigmas = new[] { 1d, 2, 3, 4, 5, 6, 7, 8 } });
        Check(!ModelValidation.Ready(model, "turbo"), "Eight arbitrary sampling values cannot pass the Turbo compatibility check");
        File.WriteAllBytes(pipelinePath, pipeline);

        var component = Path.Combine(model, "transformer");
        var layout = ModelCatalog.Get("turbo").WeightComponents["transformer"];
        var weightsPath = Path.Combine(component, layout.File);
        var weights = File.ReadAllBytes(weightsPath);
        using (var file = File.Create(weightsPath))
        using (var writer = new BinaryWriter(file))
        {
            var emptyHeader = Encoding.UTF8.GetBytes("{\"__metadata__\":{}}");
            writer.Write((ulong)emptyHeader.Length);
            writer.Write(emptyHeader);
        }
        Check(!ModelValidation.Ready(model, "turbo"), "A metadata-only safetensors file cannot be reported as model weights");
        File.WriteAllBytes(weightsPath, weights);
        var shardIndex = Path.Combine(component, layout.Index);
        Files.Write(shardIndex, new { weight_map = new Dictionary<string, string> { ["fixture"] = "missing-shard.safetensors" } });
        Check(!ModelValidation.Ready(model, "turbo"), "An incomplete sharded checkpoint remains unavailable even when a single-file checkpoint also exists");
        Files.Write(shardIndex, new { weight_map = new Dictionary<string, string> { ["fixture"] = "../text_encoder/model.safetensors" } });
        Check(!ModelValidation.Ready(model, "turbo"), "A shard index cannot reference weights outside its own component");
        File.Delete(shardIndex);

        var downloads = new FakeModelDownloader();
        using var vm = new MainViewModel(fixture.Preferences, downloads, driverPlatform: fixture.Platform);
        vm.Prompt = "A basic image after model repair";
        vm.Setup.Page = 2;
        var wizard = new FirstRunWindow(vm);
        await Render(wizard, 760, 700, "models-check-and-repair");
        var repair = Children<Button>((DependencyObject)wizard.Content).Single(button => AutomationProperties.GetAutomationId(button) == "ModelCheckRepair" && ReferenceEquals(button.DataContext, vm.TurboCard));
        Check(vm.TurboCard.Ready && !vm.TurboCard.DownloadCommand.CanExecute(null) && repair.IsEnabled, "Ready model cards expose an explicit check-and-repair action without enabling redundant ordinary downloads");
        repair.Command.Execute(null);
        await UntilDownloadIdle(vm);
        Check(downloads.Requests.Count == 1 && vm.TurboCard.Ready, "Explicit repair reaches the downloader even when lightweight startup validation already passed");
        File.Delete(Path.Combine(model, "processor", "tokenizer.json"));
        vm.TurboCard.StatusCommand.Execute(null);
        Check(!vm.TurboCard.Ready && !vm.CanGenerate && !vm.Setup.CanContinue, "Rechecking a model with a missing tokenizer blocks generation and setup completion");
        await vm.RepairModelAsync("turbo");
        Check(downloads.Requests.Count == 2 && vm.TurboCard.Ready && vm.CanGenerate && vm.Setup.CanContinue, "Repairing a missing tokenizer restores model readiness and ordinary generation");
        wizard.Close();
    }
}
