namespace LightX2V.Windows;

internal static class ModelPage
{
    public static Uri Address(string repository,bool modelScope)=>new(
        (modelScope?"https://modelscope.cn/models/":"https://huggingface.co/")+repository,UriKind.Absolute);
}
