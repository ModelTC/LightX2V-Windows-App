#define UNICODE
#define _UNICODE
#include <windows.h>
#include <shellapi.h>
#include <string>
#include <vector>

// Quote each argument using the inverse of CommandLineToArgvW's escaping rules.
static std::wstring quote(const std::wstring& value) {
    std::wstring result = L"\"";
    size_t slashes = 0;
    for (wchar_t c : value) {
        if (c == L'\\') { ++slashes; continue; }
        result.append(slashes * (c == L'"' ? 2 : 1), L'\\');
        if (c == L'"') result += L'\\';
        result += c; slashes = 0;
    }
    result.append(slashes * 2, L'\\');
    return result + L'"';
}
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    std::vector<wchar_t> buffer(32768);
    DWORD count = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (!count || count >= buffer.size()) return 1;
    std::wstring root(buffer.data(), count);
    root.resize(root.find_last_of(L"\\/"));
    const std::wstring executable = root + L"\\app\\LightX2V.Windows.exe";
    std::wstring command = quote(executable);
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return 1;
    for (int i = 1; i < argc; ++i) command += L" " + quote(argv[i]);
    LocalFree(argv);
    STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, root.c_str(), &startup, &process)) {
        DWORD error = GetLastError();
        std::wstring message = L"Could not start LightX2V (Windows error " + std::to_wstring(error) + L").\nExtract the complete ZIP, then open LightX2V.exe.\n\n无法启动 LightX2V，请完整解压 ZIP 后再打开 LightX2V.exe。";
        MessageBoxW(nullptr, message.c_str(), L"LightX2V", MB_OK | MB_ICONERROR);
        return 1;
    }
    CloseHandle(process.hThread); CloseHandle(process.hProcess);
    return 0;
}
