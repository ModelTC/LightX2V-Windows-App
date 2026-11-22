namespace LightX2V.Windows;

internal static class UpdateStrings
{
    internal static readonly Dictionary<string, string> Chinese = new()
    {
        ["Application updates"] = "应用更新",
        ["Check for updates"] = "检查更新",
        ["Current build: {0}"] = "当前构建：{0}",
        ["Install and restart"] = "安装并重启",
        ["Checking for updates…"] = "正在检查更新…",
        ["Downloading the update…"] = "正在下载更新…",
        ["Verifying the update…"] = "正在校验更新…",
        ["{0} build {1} is available."] = "发现 {0} 构建 {1}。",
        ["You already have this {0} build or a newer one."] = "当前已安装此 {0} 构建或更新的构建。",
        ["Build information is unavailable. Use a packaged LightX2V app."] = "无法读取构建信息，请使用正式打包的 LightX2V。",
        ["Download and verify the update, then close and restart LightX2V to install it? Save any changes in Settings first."] = "下载并校验更新后，LightX2V 将退出并重新启动。请先保存设置中的更改。确定继续？",
        ["The app update could not finish."] = "应用更新未能完成。",
        ["The update could not be checked. Check your network and try again."] = "无法检查更新，请检查网络后重试。",
        ["The update was cancelled."] = "已取消更新。",
        ["The update timed out. Check your network and try again."] = "更新超时，请检查网络后重试。",
        ["Close the running LightX2V app and try again."] = "请关闭正在运行的 LightX2V 后重试。",
        ["The updated launcher failed to start."] = "更新后的启动器未能启动。",
        ["Could not restart the updated app."] = "无法重新启动更新后的应用。",
        ["The release is missing verified package metadata."] = "发布缺少已验证的安装包信息。",
        ["The release tag does not match its package commit. Check again shortly."] = "发布标签与安装包提交不一致，请稍后重试。",
        ["This build is outside the release history. Download the app from GitHub."] = "此构建不在发布历史中，请从 GitHub 下载应用。",
        ["The downloaded update does not match the release checksum."] = "已下载的更新包与发布校验和不一致。",
        ["The update has an invalid package manifest."] = "更新包的文件清单无效。",
        ["The update contains unexpected package files."] = "更新包包含未预期的文件。",
        ["Move the app outside your workspace and component folders before updating."] = "请先将应用移到工作目录及组件目录之外，再执行更新。",
    };
}
